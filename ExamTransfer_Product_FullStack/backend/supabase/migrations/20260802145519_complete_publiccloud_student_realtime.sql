begin;

create table public.student_notification_events (
  id uuid primary key,
  organization_id uuid not null references public.organizations(id) on delete restrict,
  session_id uuid not null references public.exam_sessions(id) on delete restrict,
  participant_id uuid references public.session_participants(id) on delete restrict,
  event_type text not null,
  payload jsonb not null,
  revision bigint not null check (revision > 0),
  mutation_request_id uuid not null,
  resource_id uuid not null,
  occurred_at timestamptz not null,
  created_at timestamptz not null default pg_catalog.now(),
  constraint ck_student_notification_event_type check (event_type in (
    'ParticipantApproved',
    'ParticipantAdmissionRejected',
    'TeacherMessageReceived',
    'SubmissionRejected',
    'ResubmitAllowed',
    'GradeReturned',
    'QuizGradeReturned',
    'GradeReopened',
    'QuizGradeReopened')),
  constraint ck_student_notification_payload_shape check (
    jsonb_typeof(payload) = 'object'
    and payload ?& array[
      'eventId','eventType','sessionId','participantId','submissionId','attemptId',
      'message','reason','score','maxScore','occurredAtUtc','revision']
    and payload - array[
      'eventId','eventType','sessionId','participantId','submissionId','attemptId',
      'message','reason','score','maxScore','occurredAtUtc','revision'] = '{}'::jsonb
    and payload ->> 'eventId' = id::text
    and payload ->> 'eventType' = event_type
    and payload ->> 'sessionId' = session_id::text
    and (participant_id is null
      and payload -> 'participantId' = 'null'::jsonb
      or participant_id is not null
      and payload ->> 'participantId' = participant_id::text)
    and (payload ->> 'revision')::bigint = revision
    and nullif(payload ->> 'occurredAtUtc', '') is not null),
  constraint ck_student_notification_typed_resource check (
    case event_type
      when 'ParticipantApproved' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is null
        and nullif(payload ->> 'attemptId', '') is null
      when 'ParticipantAdmissionRejected' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is null
        and nullif(payload ->> 'attemptId', '') is null
      when 'TeacherMessageReceived' then
        length(btrim(coalesce(payload ->> 'message', ''))) between 1 and 2000
        and nullif(payload ->> 'submissionId', '') is null
        and nullif(payload ->> 'attemptId', '') is null
      when 'SubmissionRejected' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is not null
        and nullif(payload ->> 'attemptId', '') is null
      when 'ResubmitAllowed' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is not null
        and nullif(payload ->> 'attemptId', '') is null
      when 'GradeReturned' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is not null
        and nullif(payload ->> 'attemptId', '') is null
      when 'GradeReopened' then
        participant_id is not null
        and nullif(payload ->> 'submissionId', '') is not null
        and nullif(payload ->> 'attemptId', '') is null
      when 'QuizGradeReturned' then
        participant_id is not null
        and nullif(payload ->> 'attemptId', '') is not null
        and nullif(payload ->> 'submissionId', '') is null
      when 'QuizGradeReopened' then
        participant_id is not null
        and nullif(payload ->> 'attemptId', '') is not null
        and nullif(payload ->> 'submissionId', '') is null
      else false
    end),
  constraint uq_student_notification_session_revision unique (session_id, revision),
  constraint uq_student_notification_mutation_resource
    unique (mutation_request_id, event_type, resource_id)
);

create index ix_student_notification_scope_revision
  on public.student_notification_events(session_id, participant_id, revision, id);
create index ix_student_notification_broadcast_revision
  on public.student_notification_events(session_id, revision, id)
  where participant_id is null;

alter table public.student_notification_events enable row level security;
revoke all on table public.student_notification_events from public, anon, authenticated;
grant select on table public.student_notification_events to authenticated;

create policy student_notification_events_student_select
on public.student_notification_events
for select
to authenticated
using (
  exists (
    select 1
    from public.session_participants participant
    join public.exam_sessions session
      on session.id = participant.session_id
     and session.organization_id = participant.organization_id
    join public.profiles profile
      on profile.id = participant.user_id
     and profile.organization_id = participant.organization_id
    where participant.user_id = (select auth.uid())
      and participant.session_id = student_notification_events.session_id
      and participant.organization_id = student_notification_events.organization_id
      and participant.source_mode = 'PublicCloud'
      and session.access_mode = 'PublicCloud'
      and profile.role = 'Student'
      and profile.is_active = true
      and (student_notification_events.participant_id is null
        or student_notification_events.participant_id = participant.id)
  )
);

create table private.public_student_notification_sequences (
  session_id uuid primary key references public.exam_sessions(id) on delete cascade,
  last_revision bigint not null check (last_revision >= 0)
);
revoke all on table private.public_student_notification_sequences
  from public, anon, authenticated, service_role;

create or replace function private.begin_public_teacher_mutation(
  p_request_id uuid,
  p_organization_id uuid,
  p_action text,
  p_entity_scope text)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_existing private.public_teacher_mutation_requests%rowtype;
begin
  if p_request_id is null then
    raise exception 'REQUEST_ID_REQUIRED' using errcode = '22023';
  end if;
  insert into private.public_teacher_mutation_requests(
    request_id, organization_id, actor_id, action, entity_scope)
  values (p_request_id, p_organization_id, (select auth.uid()), p_action, p_entity_scope)
  on conflict (request_id) do nothing;
  if not found then
    select * into v_existing
    from private.public_teacher_mutation_requests
    where request_id = p_request_id;
    if v_existing.organization_id <> p_organization_id
       or v_existing.actor_id <> (select auth.uid())
       or v_existing.action <> p_action
       or v_existing.entity_scope <> p_entity_scope then
      raise exception 'REQUEST_ID_REUSE_MISMATCH' using errcode = '22023';
    end if;
    if v_existing.result_json is null then
      raise exception 'REQUEST_ALREADY_IN_PROGRESS' using errcode = '55000';
    end if;
    return v_existing.result_json;
  end if;
  perform pg_catalog.set_config(
    'examtransfer.public_teacher_mutation_request_id',
    p_request_id::text,
    true);
  return null;
end
$function$;
revoke all on function private.begin_public_teacher_mutation(uuid,uuid,text,text)
  from public, anon, authenticated, service_role;

create or replace function private.current_public_teacher_mutation()
returns private.public_teacher_mutation_requests
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare
  v_request_text text := nullif(pg_catalog.current_setting(
    'examtransfer.public_teacher_mutation_request_id', true), '');
  v_request private.public_teacher_mutation_requests%rowtype;
begin
  if v_request_text is null then
    return null;
  end if;
  begin
    select * into v_request
    from private.public_teacher_mutation_requests request
    where request.request_id = v_request_text::uuid
      and request.actor_id = (select auth.uid())
      and request.result_json is null;
  exception when invalid_text_representation then
    return null;
  end;
  if not found then
    return null;
  end if;
  return v_request;
end
$function$;
revoke all on function private.current_public_teacher_mutation()
  from public, anon, authenticated, service_role;

create or replace function private.next_public_student_notification_revision(
  p_session_id uuid)
returns bigint
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_revision bigint;
begin
  insert into private.public_student_notification_sequences(session_id, last_revision)
  values (p_session_id, 0)
  on conflict (session_id) do nothing;
  update private.public_student_notification_sequences
  set last_revision = last_revision + 1
  where session_id = p_session_id
  returning last_revision into v_revision;
  if v_revision is null then
    raise exception 'PUBLIC_NOTIFICATION_REVISION_ALLOCATION_FAILED' using errcode = '55000';
  end if;
  return v_revision;
end
$function$;
revoke all on function private.next_public_student_notification_revision(uuid)
  from public, anon, authenticated, service_role;

create or replace function private.emit_public_student_notification(
  p_organization_id uuid,
  p_session_id uuid,
  p_participant_id uuid,
  p_event_type text,
  p_request_id uuid,
  p_resource_id uuid,
  p_submission_id uuid default null,
  p_attempt_id uuid default null,
  p_message text default null,
  p_reason text default null,
  p_score numeric default null,
  p_max_score numeric default null)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_context private.public_teacher_mutation_requests%rowtype;
  v_existing jsonb;
  v_event_id uuid := gen_random_uuid();
  v_revision bigint;
  v_occurred_at timestamptz := pg_catalog.now();
  v_payload jsonb;
begin
  if p_request_id is null or p_resource_id is null then
    raise exception 'PUBLIC_NOTIFICATION_IDENTITY_REQUIRED' using errcode = '22023';
  end if;
  select * into v_context
  from private.current_public_teacher_mutation();
  if v_context.request_id is null
     or v_context.request_id <> p_request_id
     or v_context.organization_id <> p_organization_id then
    raise exception 'PUBLIC_NOTIFICATION_MUTATION_CONTEXT_INVALID' using errcode = '42501';
  end if;
  if not exists (
    select 1 from public.exam_sessions session
    where session.id = p_session_id
      and session.organization_id = p_organization_id
      and session.access_mode = 'PublicCloud') then
    raise exception 'PUBLIC_NOTIFICATION_SESSION_INVALID' using errcode = '42501';
  end if;
  if p_participant_id is not null and not exists (
    select 1 from public.session_participants participant
    join public.profiles profile
      on profile.id = participant.user_id
     and profile.organization_id = participant.organization_id
     and profile.role = 'Student'
     and profile.is_active = true
    where participant.id = p_participant_id
      and participant.session_id = p_session_id
      and participant.organization_id = p_organization_id
      and participant.source_mode = 'PublicCloud'
      and participant.user_id is not null) then
    raise exception 'PUBLIC_NOTIFICATION_PARTICIPANT_INVALID' using errcode = '42501';
  end if;

  if p_event_type not in (
      'ParticipantApproved','ParticipantAdmissionRejected','TeacherMessageReceived',
      'SubmissionRejected','ResubmitAllowed','GradeReturned','QuizGradeReturned',
      'GradeReopened','QuizGradeReopened') then
    raise exception 'PUBLIC_NOTIFICATION_EVENT_TYPE_INVALID' using errcode = '22023';
  end if;
  if p_event_type in ('ParticipantApproved','ParticipantAdmissionRejected')
     and (p_participant_id is null or p_submission_id is not null or p_attempt_id is not null) then
    raise exception 'PUBLIC_NOTIFICATION_PARTICIPANT_CONTRACT_INVALID' using errcode = '22023';
  end if;
  if p_event_type = 'TeacherMessageReceived'
     and (length(btrim(coalesce(p_message, ''))) not between 1 and 2000
       or p_submission_id is not null or p_attempt_id is not null) then
    raise exception 'PUBLIC_NOTIFICATION_MESSAGE_CONTRACT_INVALID' using errcode = '22023';
  end if;
  if p_event_type in ('SubmissionRejected','ResubmitAllowed','GradeReturned','GradeReopened')
     and (p_participant_id is null or p_submission_id is null or p_attempt_id is not null) then
    raise exception 'PUBLIC_NOTIFICATION_SUBMISSION_CONTRACT_INVALID' using errcode = '22023';
  end if;
  if p_event_type in ('QuizGradeReturned','QuizGradeReopened')
     and (p_participant_id is null or p_attempt_id is null or p_submission_id is not null) then
    raise exception 'PUBLIC_NOTIFICATION_ATTEMPT_CONTRACT_INVALID' using errcode = '22023';
  end if;
  if p_score is not null and p_score < 0
     or p_max_score is not null and p_max_score <= 0
     or p_score is not null and p_max_score is not null and p_score > p_max_score then
    raise exception 'PUBLIC_NOTIFICATION_SCORE_CONTRACT_INVALID' using errcode = '22023';
  end if;

  select event.payload into v_existing
  from public.student_notification_events event
  where event.mutation_request_id = p_request_id
    and event.event_type = p_event_type
    and event.resource_id = p_resource_id;
  if found then
    return v_existing;
  end if;

  v_revision := private.next_public_student_notification_revision(p_session_id);
  v_payload := jsonb_build_object(
    'eventId', v_event_id,
    'eventType', p_event_type,
    'sessionId', p_session_id,
    'participantId', p_participant_id,
    'submissionId', p_submission_id,
    'attemptId', p_attempt_id,
    'message', nullif(btrim(coalesce(p_message, '')), ''),
    'reason', nullif(btrim(coalesce(p_reason, '')), ''),
    'score', p_score,
    'maxScore', p_max_score,
    'occurredAtUtc', v_occurred_at,
    'revision', v_revision);

  insert into public.student_notification_events(
    id, organization_id, session_id, participant_id, event_type, payload,
    revision, mutation_request_id, resource_id, occurred_at)
  values (
    v_event_id, p_organization_id, p_session_id, p_participant_id, p_event_type,
    v_payload, v_revision, p_request_id, p_resource_id, v_occurred_at);
  return v_payload;
end
$function$;
revoke all on function private.emit_public_student_notification(
  uuid,uuid,uuid,text,uuid,uuid,uuid,uuid,text,text,numeric,numeric)
  from public, anon, authenticated, service_role;

create or replace function private.capture_public_participant_notification()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_context private.public_teacher_mutation_requests%rowtype;
  v_submission_id uuid;
begin
  if new.source_mode <> 'PublicCloud' then
    return new;
  end if;
  select * into v_context from private.current_public_teacher_mutation();
  if v_context.request_id is null then
    return new;
  end if;

  if v_context.action in ('ApprovePublicParticipant','BulkApprovePublicParticipants')
     and old.status = 'PendingApproval' and new.status = 'Approved' then
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.id, 'ParticipantApproved',
      v_context.request_id, new.id);
  elsif v_context.action = 'RejectPublicParticipant'
     and old.status is distinct from 'Rejected' and new.status = 'Rejected' then
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.id, 'ParticipantAdmissionRejected',
      v_context.request_id, new.id);
  elsif v_context.action = 'AllowPublicResubmission'
     and new.resubmit_allowed = true
     and (old.resubmit_allowed is distinct from new.resubmit_allowed
       or old.resubmit_reason is distinct from new.resubmit_reason) then
    select submission.id into v_submission_id
    from public.submissions submission
    where submission.participant_id = new.id
      and submission.session_id = new.session_id
      and submission.organization_id = new.organization_id
      and submission.source_mode = 'PublicCloud'
    order by submission.attempt_number desc, submission.created_at desc, submission.id desc
    limit 1;
    if v_submission_id is null then
      raise exception 'PUBLIC_RESUBMISSION_SUBMISSION_NOT_FOUND' using errcode = 'P0002';
    end if;
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.id, 'ResubmitAllowed',
      v_context.request_id, v_submission_id,
      p_submission_id => v_submission_id,
      p_reason => new.resubmit_reason);
  end if;
  return new;
end
$function$;
revoke all on function private.capture_public_participant_notification()
  from public, anon, authenticated, service_role;

create trigger capture_public_participant_notification
after update on public.session_participants
for each row execute function private.capture_public_participant_notification();

create or replace function private.capture_public_submission_notification()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_context private.public_teacher_mutation_requests%rowtype;
begin
  if new.source_mode <> 'PublicCloud' then
    return new;
  end if;
  select * into v_context from private.current_public_teacher_mutation();
  if v_context.action = 'RejectPublicSubmission'
     and (old.status is distinct from new.status
       or old.teacher_reject_reason is distinct from new.teacher_reject_reason)
     and new.status = 'Rejected' then
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.participant_id, 'SubmissionRejected',
      v_context.request_id, new.id,
      p_submission_id => new.id,
      p_reason => new.teacher_reject_reason);
  end if;
  return new;
end
$function$;
revoke all on function private.capture_public_submission_notification()
  from public, anon, authenticated, service_role;

create trigger capture_public_submission_notification
after update on public.submissions
for each row execute function private.capture_public_submission_notification();

create or replace function private.capture_public_quiz_grade_notification()
returns trigger
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_context private.public_teacher_mutation_requests%rowtype;
begin
  if new.source_mode <> 'PublicCloud' then
    return new;
  end if;
  select * into v_context from private.current_public_teacher_mutation();
  if v_context.action = 'ReturnPublicQuizGrade'
     and old.grading_status = 'Graded' and new.grading_status = 'Returned' then
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.participant_id, 'QuizGradeReturned',
      v_context.request_id, new.id,
      p_attempt_id => new.id,
      p_score => new.score,
      p_max_score => new.max_score);
  elsif v_context.action = 'ReopenPublicQuizGrade'
     and old.grading_status = 'Returned' and new.grading_status = 'InProgress' then
    perform private.emit_public_student_notification(
      new.organization_id, new.session_id, new.participant_id, 'QuizGradeReopened',
      v_context.request_id, new.id,
      p_attempt_id => new.id);
  end if;
  return new;
end
$function$;
revoke all on function private.capture_public_quiz_grade_notification()
  from public, anon, authenticated, service_role;

create trigger capture_public_quiz_grade_notification
after update on public.quiz_attempts
for each row execute function private.capture_public_quiz_grade_notification();

create or replace function public.send_public_teacher_message(
  p_session_id uuid,
  p_participant_id uuid,
  p_message_type text,
  p_content text,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_session public.exam_sessions%rowtype := private.require_public_session_teacher(p_session_id);
  v_cached jsonb;
  v_event jsonb;
  v_result jsonb;
  v_resource_id uuid := gen_random_uuid();
begin
  if p_message_type not in ('Information','Warning','TimeChange','System') then
    raise exception 'PUBLIC_MESSAGE_TYPE_INVALID' using errcode = '22023';
  end if;
  if length(btrim(coalesce(p_content, ''))) not between 1 and 2000 then
    raise exception 'PUBLIC_MESSAGE_CONTENT_INVALID' using errcode = '22023';
  end if;
  if p_participant_id is not null and not exists (
    select 1 from public.session_participants participant
    where participant.id = p_participant_id
      and participant.session_id = p_session_id
      and participant.organization_id = v_session.organization_id
      and participant.source_mode = 'PublicCloud'
      and participant.status <> 'Rejected'
      and participant.user_id is not null) then
    raise exception 'PUBLIC_MESSAGE_PARTICIPANT_INVALID' using errcode = 'P0002';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'SendPublicTeacherMessage',
    jsonb_build_object(
      'sessionId', p_session_id,
      'participantId', p_participant_id,
      'messageType', p_message_type,
      'content', btrim(p_content))::text);
  if v_cached is not null then
    return v_cached;
  end if;

  v_event := private.emit_public_student_notification(
    v_session.organization_id,
    p_session_id,
    p_participant_id,
    'TeacherMessageReceived',
    p_request_id,
    v_resource_id,
    p_message => btrim(p_content));
  v_result := jsonb_build_object(
    'id', v_event ->> 'eventId',
    'sessionId', p_session_id,
    'senderId', (select auth.uid()),
    'receiverId', p_participant_id,
    'type', p_message_type,
    'content', btrim(p_content),
    'createdAtUtc', v_event ->> 'occurredAtUtc');
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;
revoke all on function public.send_public_teacher_message(uuid,uuid,text,text,uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.send_public_teacher_message(uuid,uuid,text,text,uuid)
  to authenticated;

create or replace function public.get_public_student_notification_events(
  p_session_id uuid,
  p_after_revision bigint,
  p_after_event_id uuid,
  p_limit integer)
returns setof jsonb
language plpgsql
stable
security invoker
set search_path = ''
as $function$
begin
  if p_session_id is null
     or coalesce(p_after_revision, 0) < 0
     or coalesce(p_limit, 0) not between 1 and 100 then
    raise exception 'PUBLIC_NOTIFICATION_CURSOR_INVALID' using errcode = '22023';
  end if;
  return query
  select event.payload
  from public.student_notification_events event
  where event.session_id = p_session_id
    and (event.revision > coalesce(p_after_revision, 0)
      or (event.revision = coalesce(p_after_revision, 0)
        and p_after_event_id is not null
        and event.id > p_after_event_id))
  order by event.revision, event.id
  limit p_limit;
end
$function$;
revoke all on function public.get_public_student_notification_events(uuid,bigint,uuid,integer)
  from public, anon, authenticated, service_role;
grant execute on function public.get_public_student_notification_events(uuid,bigint,uuid,integer)
  to authenticated;

do $block$
begin
  if not exists (
    select 1 from pg_catalog.pg_publication publication
    where publication.pubname = 'supabase_realtime') then
    raise exception 'SUPABASE_REALTIME_PUBLICATION_MISSING' using errcode = '55000';
  end if;
  if not exists (
    select 1 from pg_catalog.pg_publication_tables published
    where published.pubname = 'supabase_realtime'
      and published.schemaname = 'public'
      and published.tablename = 'student_notification_events') then
    alter publication supabase_realtime
      add table public.student_notification_events;
  end if;
end
$block$;

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
  return jsonb_build_object(
    'schemaVersion',
      (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', jsonb_build_array(
      'join_public_session','join_open_public_session_by_room_code',
      'init_public_submission','finalize_public_submission',
      'upsert_public_device_heartbeat','ack_public_device_command',
      'report_public_violation',
      'start_public_quiz_attempt','save_public_quiz_answers',
      'finalize_public_quiz_attempt','get_public_quiz_attempt',
      'get_public_quiz_attempt_review','get_teacher_quiz_attempts',
      'save_public_quiz_grade','return_public_quiz_grade',
      'reopen_public_quiz_grade',
      'verify_public_submission_archive',
      'get_public_exam_manifest','get_public_exam_file_download',
      'approve_public_participant','reject_public_participant',
      'bulk_approve_public_participants','add_public_participant_extra_time',
      'allow_public_resubmission','reject_public_submission',
      'approve_public_enrollment_request','reject_public_enrollment_request',
      'get_public_student_timeline','send_public_teacher_message',
      'get_public_student_notification_events'),
    'buckets', coalesce((
      select jsonb_agg(id order by id)
      from storage.buckets
      where id in ('exam-archives','public-submission-archives')
    ), '[]'::jsonb));
end
$function$;
revoke all on function public.get_examtransfer_cloud_capabilities()
  from public, anon;
grant execute on function public.get_examtransfer_cloud_capabilities()
  to authenticated, service_role;

commit;
