begin;

alter table public.submissions
  add column if not exists computed_is_late boolean not null default false,
  add column if not exists late_override boolean;

update public.submissions
set computed_is_late = coalesce(server_received_at > deadline_at, is_late),
    is_late = coalesce(late_override, coalesce(server_received_at > deadline_at, is_late))
where server_received_at is not null;

create or replace function private.apply_submission_late_state()
returns trigger
language plpgsql
set search_path = ''
as $function$
begin
  if new.server_received_at is not null then
    new.computed_is_late := new.server_received_at > new.deadline_at;
  end if;
  new.is_late := coalesce(new.late_override, new.computed_is_late);
  if new.status in ('Submitted','LateSubmitted') then
    new.status := case when new.is_late then 'LateSubmitted' else 'Submitted' end;
  end if;
  return new;
end
$function$;
revoke all on function private.apply_submission_late_state() from public, anon, authenticated, service_role;

drop trigger if exists submissions_apply_late_state on public.submissions;
create trigger submissions_apply_late_state
before insert or update of server_received_at, deadline_at, late_override, computed_is_late, is_late, status
on public.submissions
for each row execute function private.apply_submission_late_state();

create or replace function public.set_public_submission_late_override(
  p_submission_id uuid,
  p_late_override boolean,
  p_reason text,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if length(btrim(coalesce(p_reason, ''))) < 3 then
    raise exception 'LATE_OVERRIDE_REASON_REQUIRED' using errcode = '22023';
  end if;
  select * into v_submission
  from public.submissions
  where id = p_submission_id and source_mode = 'PublicCloud'
  for update;
  if not found then
    raise exception 'PUBLIC_SUBMISSION_NOT_FOUND' using errcode = 'P0002';
  end if;
  v_session := private.require_public_session_teacher(v_submission.session_id);
  if v_submission.organization_id <> v_session.organization_id
     or not v_submission.is_official
     or v_submission.status not in ('Submitted','LateSubmitted')
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_submission.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_SUBMISSION_LATE_OVERRIDE_FORBIDDEN' using errcode = '42501';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'SetPublicSubmissionLateOverride',
    p_submission_id::text);
  if v_cached is not null then return v_cached; end if;

  update public.submissions
  set late_override = p_late_override,
      updated_at = pg_catalog.now()
  where id = p_submission_id;
  update public.session_participants p
  set submission_status = s.status,
      updated_at = pg_catalog.now()
  from public.submissions s
  where s.id = p_submission_id and p.id = s.participant_id;

  select pg_catalog.jsonb_build_object(
    'submissionId', s.id,
    'computedIsLate', s.computed_is_late,
    'lateOverride', s.late_override,
    'effectiveIsLate', s.is_late,
    'status', s.status,
    'cloudVersion', s.cloud_version,
    'updatedAt', s.updated_at)
  into v_result
  from public.submissions s where s.id = p_submission_id;

  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'SetPublicSubmissionLateOverride',
    'submissions',
    p_submission_id,
    p_request_id,
    to_jsonb(v_submission),
    v_result || pg_catalog.jsonb_build_object('reason', btrim(p_reason)));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;
revoke all on function public.set_public_submission_late_override(uuid,boolean,text,uuid)
  from public, anon;
grant execute on function public.set_public_submission_late_override(uuid,boolean,text,uuid)
  to authenticated;

-- Public submission archives require an exact, verified submission/file chain.
drop policy if exists examtransfer_storage_staff_select on storage.objects;
create policy examtransfer_storage_staff_select
on storage.objects for select to authenticated using (
  storage.objects.bucket_id in ('exam-archives','submission-archives','report-exports','backup-archives')
  and (storage.foldername(storage.objects.name))[1] = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher'));

drop policy if exists examtransfer_public_submission_staff_select on storage.objects;
create policy examtransfer_public_submission_staff_select
on storage.objects for select to authenticated using (
  storage.objects.bucket_id = 'public-submission-archives'
  and (storage.foldername(storage.objects.name))[1] = (select public.current_organization_id())::text
  and (select public.current_examtransfer_role()) in ('Admin','Teacher')
  and exists (
    select 1
    from public.submission_files f
    join public.submissions s on s.id = f.submission_id
    join public.session_participants p on p.id = s.participant_id
    join public.exam_sessions es on es.id = s.session_id
    join public.exams e on e.id = es.exam_id
    join public.profiles actor on actor.id = (select auth.uid())
    where f.cloud_object_path = storage.objects.name
      and f.archive_signature_verified = true
      and f.transfer_status in ('Verified','Completed')
      and s.is_official = true
      and s.status in ('Submitted','LateSubmitted')
      and s.source_mode = 'PublicCloud'
      and p.session_id = s.session_id
      and s.organization_id = (select public.current_organization_id())
      and actor.organization_id = s.organization_id
      and actor.is_active = true
      and (actor.role = 'Admin' or e.created_by = actor.id)));

create or replace function public.allow_public_resubmission(
  p_participant_id uuid, p_reason text, p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_participant public.session_participants%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_result jsonb;
begin
  if length(btrim(coalesce(p_reason,''))) < 3 then
    raise exception 'RESUBMISSION_REASON_REQUIRED' using errcode = '22023';
  end if;
  select * into v_participant from public.session_participants
  where id = p_participant_id and source_mode = 'PublicCloud' for update;
  if not found then raise exception 'PUBLIC_PARTICIPANT_NOT_FOUND' using errcode = 'P0002'; end if;
  v_session := private.require_public_session_teacher(v_participant.session_id);
  perform private.assert_public_participant_organization(v_participant, v_session.organization_id);
  v_cached := private.begin_public_teacher_mutation(
    p_request_id, v_session.organization_id, 'AllowPublicResubmission', p_participant_id::text);
  if v_cached is not null then return v_cached; end if;
  if v_participant.submission_status <> 'Rejected'
     or not exists (
       select 1 from public.submissions s
       where s.participant_id = p_participant_id
         and s.session_id = v_session.id
         and s.status = 'Rejected'
         and s.is_official = true) then
    raise exception 'RESUBMISSION_REQUIRES_REJECTED_ATTEMPT' using errcode = '55000';
  end if;
  update public.session_participants
  set resubmit_allowed = true, resubmit_reason = btrim(p_reason), updated_at = pg_catalog.now()
  where id = p_participant_id;
  v_result := private.public_participant_result(p_participant_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id, v_session.id, 'AllowPublicResubmission',
    'session_participants', p_participant_id, p_request_id, to_jsonb(v_participant), v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;
revoke all on function public.allow_public_resubmission(uuid,text,uuid) from public, anon;
grant execute on function public.allow_public_resubmission(uuid,text,uuid) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 27, updated_at = pg_catalog.now()
where id = 1;

create or replace function public.get_examtransfer_cloud_capabilities()
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $function$
begin
  if (select auth.uid()) is null
     and coalesce((select auth.jwt() ->> 'role'), '') <> 'service_role' then
    raise exception 'AUTHENTICATION_REQUIRED' using errcode = '28000';
  end if;
  return pg_catalog.jsonb_build_object(
    'schemaVersion', (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', pg_catalog.jsonb_build_array(
      'join_public_session','join_open_public_session_by_room_code',
      'init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command','report_public_violation',
      'start_public_quiz_attempt','save_public_quiz_answers','finalize_public_quiz_attempt',
      'get_public_quiz_attempt','get_public_quiz_attempt_review','get_teacher_quiz_attempts',
      'save_public_quiz_grade','return_public_quiz_grade','reopen_public_quiz_grade',
      'get_public_essay_grade','save_public_essay_grade','return_public_essay_grade','reopen_public_essay_grade',
      'verify_public_submission_archive','get_public_exam_manifest','get_public_exam_file_download',
      'approve_public_participant','reject_public_participant','bulk_approve_public_participants',
      'add_public_participant_extra_time','allow_public_resubmission','reject_public_submission',
      'set_public_submission_late_override',
      'approve_public_enrollment_request','reject_public_enrollment_request',
      'get_public_student_timeline','send_public_teacher_message',
      'get_public_student_notification_events','get_student_results'),
    'buckets', coalesce((select pg_catalog.jsonb_agg(id order by id)
      from storage.buckets where id in ('exam-archives','public-submission-archives')), '[]'::jsonb));
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities() from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities() to authenticated, service_role;

commit;
