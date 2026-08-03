begin;

do $cleanup$
declare
  v_organization_id constant uuid := '560f402b-5db4-413f-aa1c-391bbc78fbe0';
  v_target_ids constant uuid[] := array[
    '56666af9-b930-444f-83cb-dd072a3bdf6e'::uuid,
    'a5c5c4e5-6631-4d9d-a868-055f897a6b57'::uuid,
    '33367b33-927b-4235-bad6-bf8dcec8ddc0'::uuid
  ];
  v_guard_ids constant uuid[] := array[
    '77cb9b9f-42d3-4b3d-807e-6ab6c1060bed'::uuid,
    '66cc9c26-0a43-427f-9b66-25d9cc778172'::uuid
  ];
  v_preserved_participant_id constant uuid :=
    '720eaf21-6b90-4cfa-b3cc-2bf58df09e3e';
  v_guard_snapshot jsonb;
  v_participant_snapshot jsonb;
  v_row_count integer;
begin
  -- A clean installation has no production organization or repair targets.
  -- When the production organization exists, every repair precondition is
  -- mandatory and any drift aborts the whole migration.
  if not exists (
    select 1
    from public.organizations
    where id = v_organization_id
  ) then
    return;
  end if;

  select count(*)::integer
  into v_row_count
  from public.exam_sessions
  where id = any(v_target_ids);
  if v_row_count <> 3 then
    raise exception 'A01_TARGET_SESSION_COUNT_DRIFT: expected 3, found %', v_row_count;
  end if;

  if exists (
    select 1
    from public.exam_sessions
    where id = any(v_target_ids)
      and (
        organization_id is distinct from v_organization_id
        or room_code is distinct from '222222'
        or access_mode is distinct from 'PublicCloud'
        or admission_mode is distinct from 'OpenRequest'
        or status is distinct from 'Waiting'
        or accepting_participants is distinct from true
      )
  ) then
    raise exception 'A01_TARGET_SESSION_SNAPSHOT_DRIFT';
  end if;

  select count(*)::integer
  into v_row_count
  from public.session_participants
  where session_id = any(v_target_ids);
  if v_row_count <> 1 or not exists (
    select 1
    from public.session_participants
    where id = v_preserved_participant_id
      and session_id = '56666af9-b930-444f-83cb-dd072a3bdf6e'
      and status = 'PendingApproval'
      and approved_at is null
      and submission_status = 'NotStarted'
      and download_status = 'NotStarted'
      and resubmit_allowed = false
  ) then
    raise exception 'A01_PRESERVED_PARTICIPANT_SNAPSHOT_DRIFT';
  end if;

  if exists (
    select 1
    from public.submissions
    where session_id = any(v_target_ids)
  ) or exists (
    select 1
    from public.grades g
    join public.submissions sub on sub.id = g.submission_id
    where sub.session_id = any(v_target_ids)
  ) or exists (
    select 1
    from public.quiz_attempts
    where session_id = any(v_target_ids)
  ) then
    raise exception 'A01_TARGET_ACTIVITY_DRIFT';
  end if;

  select count(*)::integer
  into v_row_count
  from public.exam_sessions
  where id = any(v_guard_ids)
    and status = 'Finished';
  if v_row_count <> 2 then
    raise exception 'A01_NO_TOUCH_SESSION_SNAPSHOT_DRIFT';
  end if;

  select jsonb_agg(to_jsonb(s) order by s.id)
  into v_guard_snapshot
  from public.exam_sessions s
  where s.id = any(v_guard_ids);

  select to_jsonb(p)
  into v_participant_snapshot
  from public.session_participants p
  where p.id = v_preserved_participant_id;

  update public.exam_sessions
  set status = 'Cancelled',
      accepting_participants = false,
      ended_at = coalesce(ended_at, pg_catalog.clock_timestamp()),
      sequence = sequence + 1,
      cloud_version = cloud_version + 1,
      updated_at = pg_catalog.clock_timestamp()
  where id = any(v_target_ids)
    and status = 'Waiting'
    and accepting_participants = true;
  get diagnostics v_row_count = row_count;
  if v_row_count <> 3 then
    raise exception 'A01_CANCEL_TARGET_COUNT_DRIFT: expected 3, updated %', v_row_count;
  end if;

  update public.exam_sessions
  set status = 'Archived',
      sequence = sequence + 1,
      cloud_version = cloud_version + 1,
      updated_at = pg_catalog.clock_timestamp()
  where id = any(v_target_ids)
    and status = 'Cancelled'
    and accepting_participants = false;
  get diagnostics v_row_count = row_count;
  if v_row_count <> 3 then
    raise exception 'A01_ARCHIVE_TARGET_COUNT_DRIFT: expected 3, updated %', v_row_count;
  end if;

  if (
    select to_jsonb(p)
    from public.session_participants p
    where p.id = v_preserved_participant_id
  ) is distinct from v_participant_snapshot then
    raise exception 'A01_PRESERVED_PARTICIPANT_CHANGED';
  end if;

  if (
    select jsonb_agg(to_jsonb(s) order by s.id)
    from public.exam_sessions s
    where s.id = any(v_guard_ids)
  ) is distinct from v_guard_snapshot then
    raise exception 'A01_NO_TOUCH_SESSION_CHANGED';
  end if;
end
$cleanup$;

drop index if exists public.ix_exam_sessions_open_public_room;

create unique index ux_exam_sessions_active_public_room
  on public.exam_sessions(organization_id, (upper(btrim(room_code))))
  where access_mode = 'PublicCloud'
    and admission_mode = 'OpenRequest'
    and status = 'Waiting'
    and accepting_participants = true;

create or replace function public.join_open_public_session_by_room_code(
  p_room_code text,
  p_device_id text,
  p_machine_name text default null,
  p_app_version text default null,
  p_capability_json jsonb default '{}'::jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
  v_exam public.exams%rowtype;
  v_participant public.session_participants%rowtype;
  v_room_code text := upper(btrim(coalesce(p_room_code, '')));
  v_status text;
  v_count integer;
begin
  if length(v_room_code) not between 4 and 12 then
    raise exception 'ROOM_CODE_INVALID' using errcode = '22023';
  end if;
  if length(btrim(coalesce(p_device_id, ''))) not between 1 and 128 then
    raise exception 'DEVICE_ID_INVALID' using errcode = '22023';
  end if;
  if pg_column_size(coalesce(p_capability_json, '{}'::jsonb)) > 32768 then
    raise exception 'CAPABILITY_PAYLOAD_TOO_LARGE' using errcode = '22023';
  end if;

  select count(*) into v_count
  from public.exam_sessions s
  where s.organization_id = v_profile.organization_id
    and upper(btrim(s.room_code)) = v_room_code
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true;
  if v_count = 0 then
    raise exception 'OPEN_PUBLIC_SESSION_NOT_FOUND' using errcode = 'P0002';
  elsif v_count > 1 then
    raise exception 'OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS' using errcode = 'P0003';
  end if;

  select s.* into v_session
  from public.exam_sessions s
  where s.organization_id = v_profile.organization_id
    and upper(btrim(s.room_code)) = v_room_code
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true;

  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(v_session.id::text, 0));
  select s.* into v_session
  from public.exam_sessions s
  where s.id = v_session.id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.admission_mode = 'OpenRequest'
    and s.status = 'Waiting'
    and s.accepting_participants = true
  for update;
  if not found then
    raise exception 'SESSION_NOT_ACCEPTING_PARTICIPANTS' using errcode = '55000';
  end if;

  select e.* into v_exam
  from public.exams e
  where e.id = v_session.exam_id
    and e.organization_id = v_session.organization_id;
  if not found then
    raise exception 'PUBLIC_EXAM_NOT_FOUND' using errcode = 'P0002';
  end if;

  select p.* into v_participant
  from public.session_participants p
  where p.session_id = v_session.id
    and (
      p.user_id = v_profile.id
      or lower(btrim(p.student_code)) = lower(btrim(v_profile.student_code))
    )
  order by case when p.user_id = v_profile.id then 0 else 1 end
  limit 1;
  if found then
    if v_participant.user_id is distinct from v_profile.id then
      raise exception 'PARTICIPANT_ACCOUNT_MISMATCH' using errcode = '42501';
    end if;
    if v_participant.device_id is distinct from btrim(p_device_id) then
      raise exception 'PARTICIPANT_DEVICE_CONFLICT' using errcode = '23505';
    end if;
  else
    if exists (
      select 1 from public.session_participants p
      where p.session_id = v_session.id
        and p.device_id = btrim(p_device_id)
    ) then
      raise exception 'PARTICIPANT_DEVICE_CONFLICT' using errcode = '23505';
    end if;
    if v_session.capacity is not null and (
      select count(*) from public.session_participants p
      where p.session_id = v_session.id and p.status <> 'Rejected'
    ) >= v_session.capacity then
      raise exception 'SESSION_CAPACITY_REACHED' using errcode = '54000';
    end if;

    v_status := case when v_session.auto_approve then 'Approved' else 'PendingApproval' end;
    insert into public.session_participants(
      id, organization_id, session_id, user_id, student_code, display_name,
      class_name, device_id, machine_name, app_version, status, joined_at,
      approved_at, last_seen_at, download_status, submission_status,
      extra_time_minutes, resubmit_allowed, capability_json, source_mode,
      cloud_version, created_at, updated_at)
    values (
      gen_random_uuid(), v_profile.organization_id, v_session.id,
      v_profile.id, btrim(v_profile.student_code), v_profile.display_name, null,
      btrim(p_device_id), nullif(btrim(p_machine_name), ''),
      nullif(btrim(p_app_version), ''), v_status,
      pg_catalog.now(),
      case when v_status = 'Approved' then pg_catalog.now() else null end,
      pg_catalog.now(), 'NotStarted', 'NotStarted', 0, false,
      coalesce(p_capability_json, '{}'::jsonb), 'PublicCloud',
      private.next_public_cloud_version(), pg_catalog.now(), pg_catalog.now())
    returning * into v_participant;
  end if;

  return jsonb_build_object(
    'sessionId', v_session.id,
    'examId', v_session.exam_id,
    'participantId', v_participant.id,
    'participantStatus', v_participant.status,
    'sessionStatus', v_session.status,
    'roomCode', v_session.room_code,
    'examTitle', v_exam.title,
    'subject', v_exam.subject,
    'durationMinutes', v_exam.duration_minutes,
    'deliveryType', v_session.delivery_type,
    'supervisionMode', v_session.supervision_mode,
    'quizResultPolicy', v_session.quiz_result_policy,
    'plannedStartUtc', v_session.planned_start_at,
    'capacity', v_session.capacity,
    'currentParticipantCount', (
      select count(*) from public.session_participants p
      where p.session_id = v_session.id and p.status <> 'Rejected'
    ));
end
$function$;

revoke all on function public.join_open_public_session_by_room_code(text,text,text,text,jsonb)
  from public, anon;
grant execute on function public.join_open_public_session_by_room_code(text,text,text,text,jsonb)
  to authenticated;

commit;
