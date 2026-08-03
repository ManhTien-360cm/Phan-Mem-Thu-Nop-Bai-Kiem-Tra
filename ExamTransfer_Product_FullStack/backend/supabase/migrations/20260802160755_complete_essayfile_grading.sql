begin;

alter table public.grades
  add column if not exists revision bigint not null default 0;
update public.grades
set revision = 1
where revision < 1;
alter table public.grades drop constraint if exists ck_grades_score10;
alter table public.grades add constraint ck_grades_score10 check (
  max_score = 10.00
  and (score is null or score between 0 and 10.00)
  and status in ('InProgress','Graded','Returned')
  and (status <> 'Returned' or (score is not null and returned_at is not null))
  and revision > 0);
create unique index if not exists ux_grades_submission_authoritative
  on public.grades(submission_id);

drop policy if exists grades_tenant_insert on public.grades;
drop policy if exists grades_tenant_update on public.grades;
drop policy if exists grades_tenant_delete on public.grades;
drop policy if exists rubric_scores_tenant_select on public.rubric_scores;
drop policy if exists rubric_scores_tenant_insert on public.rubric_scores;
drop policy if exists rubric_scores_tenant_update on public.rubric_scores;
drop policy if exists rubric_scores_tenant_delete on public.rubric_scores;
drop policy if exists graded_attachments_tenant_select on public.graded_attachments;
drop policy if exists graded_attachments_tenant_insert on public.graded_attachments;
drop policy if exists graded_attachments_tenant_update on public.graded_attachments;
drop policy if exists graded_attachments_tenant_delete on public.graded_attachments;

create policy rubric_scores_staff_or_returned_owner_select
on public.rubric_scores for select to authenticated using (
  organization_id = (select public.current_organization_id())
  and exists (
    select 1
    from public.grades grade
    join public.submissions submission on submission.id = grade.submission_id
    join public.session_participants participant on participant.id = submission.participant_id
    where grade.id = rubric_scores.grade_id
      and grade.organization_id = rubric_scores.organization_id
      and (
        (select public.current_examtransfer_role()) in ('Admin','Teacher')
        or (grade.status = 'Returned' and participant.user_id = (select auth.uid())))));

create policy graded_attachments_staff_or_returned_owner_select
on public.graded_attachments for select to authenticated using (
  organization_id = (select public.current_organization_id())
  and exists (
    select 1
    from public.grades grade
    join public.submissions submission on submission.id = grade.submission_id
    join public.session_participants participant on participant.id = submission.participant_id
    where grade.id = graded_attachments.grade_id
      and grade.organization_id = graded_attachments.organization_id
      and (
        (select public.current_examtransfer_role()) in ('Admin','Teacher')
        or (grade.status = 'Returned' and participant.user_id = (select auth.uid())))));

create or replace function private.require_public_essay_submission(
  p_submission_id uuid)
returns public.submissions
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_session public.exam_sessions%rowtype;
begin
  select * into v_submission
  from public.submissions submission
  where submission.id = p_submission_id
    and submission.source_mode = 'PublicCloud'
    and submission.is_official = true
    and submission.status in ('Submitted','LateSubmitted');
  if not found then
    raise exception 'PUBLIC_ESSAY_SUBMISSION_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_session := private.require_public_session_teacher(v_submission.session_id);
  if v_submission.organization_id <> v_session.organization_id
     or v_session.access_mode <> 'PublicCloud'
     or v_session.delivery_type <> 'FileSubmission'
     or not exists (
       select 1
       from public.exams exam
       where exam.id = v_session.exam_id
         and exam.organization_id = v_session.organization_id
         and exam.delivery_type = 'FileSubmission')
     or not exists (
       select 1
       from public.session_participants participant
       where participant.id = v_submission.participant_id
         and participant.session_id = v_session.id
         and participant.organization_id = v_session.organization_id
         and participant.source_mode = 'PublicCloud')
     or (
       select count(*)
       from public.submission_files file
       where file.submission_id = v_submission.id
         and file.organization_id = v_session.organization_id
         and file.source_mode = 'PublicCloud'
         and file.archive_signature_verified = true) <> 1 then
    raise exception 'PUBLIC_ESSAY_SUBMISSION_SCOPE_INVALID' using errcode = '42501';
  end if;
  return v_submission;
end
$function$;
revoke all on function private.require_public_essay_submission(uuid)
  from public, anon, authenticated, service_role;

create or replace function private.public_essay_grade_result(p_submission_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $function$
  select jsonb_build_object(
    'gradeId', grade.id,
    'submissionId', submission.id,
    'sessionId', submission.session_id,
    'participantId', submission.participant_id,
    'score', grade.score,
    'maxScore', 10.00,
    'status', coalesce(grade.status, 'NotGraded'),
    'generalComment', grade.general_comment,
    'graderId', grade.grader_id,
    'gradedAt', grade.graded_at,
    'returnedAt', grade.returned_at,
    'revision', coalesce(grade.revision, 0),
    'cloudVersion', coalesce(grade.cloud_version, 0),
    'updatedAt', coalesce(grade.updated_at, submission.updated_at),
    'rubricScores', coalesce((
      select jsonb_agg(jsonb_build_object(
        'criterionKey', rubric.criterion_key,
        'title', rubric.title,
        'score', rubric.score,
        'maxScore', rubric.max_score,
        'comment', rubric.comment,
        'order', rubric.sort_order)
        order by rubric.sort_order, rubric.criterion_key)
      from public.rubric_scores rubric
      where rubric.grade_id = grade.id), '[]'::jsonb),
    'attachments', coalesce((
      select jsonb_agg(jsonb_build_object(
        'id', attachment.id,
        'name', attachment.name,
        'sizeBytes', attachment.size_bytes,
        'sha256', attachment.sha256,
        'mimeType', coalesce(attachment.mime_type, 'application/octet-stream'))
        order by attachment.created_at, attachment.id)
      from public.graded_attachments attachment
      where attachment.grade_id = grade.id), '[]'::jsonb))
  from public.submissions submission
  left join public.grades grade on grade.submission_id = submission.id
  where submission.id = p_submission_id
$function$;
revoke all on function private.public_essay_grade_result(uuid)
  from public, anon, authenticated, service_role;

create or replace function public.get_public_essay_grade(p_submission_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
begin
  perform private.require_public_essay_submission(p_submission_id);
  return private.public_essay_grade_result(p_submission_id);
end
$function$;

create or replace function public.save_public_essay_grade(
  p_submission_id uuid,
  p_score numeric,
  p_rubric_scores jsonb,
  p_general_comment text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_grade public.grades%rowtype;
  v_grade_id uuid;
  v_cached jsonb;
  v_result jsonb;
  v_now timestamptz := pg_catalog.now();
begin
  v_submission := private.require_public_essay_submission(p_submission_id);
  if p_expected_cloud_version is null or p_expected_cloud_version < 0 then
    raise exception 'ESSAY_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;
  if p_score is not null and (p_score < 0 or p_score > 10.00) then
    raise exception 'ESSAY_GRADE_SCORE_INVALID' using errcode = '22023';
  end if;
  if jsonb_typeof(coalesce(p_rubric_scores, '[]'::jsonb)) <> 'array'
     or exists (
       select 1 from jsonb_array_elements(coalesce(p_rubric_scores, '[]'::jsonb)) item
       where jsonb_typeof(item) <> 'object'
          or length(btrim(coalesce(item ->> 'criterionKey', ''))) = 0
          or length(btrim(coalesce(item ->> 'title', ''))) = 0
          or coalesce((item ->> 'maxScore')::numeric, 0) <= 0
          or coalesce((item ->> 'score')::numeric, -1) < 0
          or (item ->> 'score')::numeric > (item ->> 'maxScore')::numeric)
     or exists (
       select 1
       from jsonb_array_elements(coalesce(p_rubric_scores, '[]'::jsonb)) item
       group by btrim(item ->> 'criterionKey')
       having count(*) > 1)
     or coalesce((
       select sum((item ->> 'maxScore')::numeric)
       from jsonb_array_elements(coalesce(p_rubric_scores, '[]'::jsonb)) item), 0) > 10.00 then
    raise exception 'ESSAY_GRADE_RUBRIC_INVALID' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_submission.organization_id,
    'SavePublicEssayGrade',
    jsonb_build_object(
      'submissionId', p_submission_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'score', p_score,
      'rubricScores', coalesce(p_rubric_scores, '[]'::jsonb),
      'generalComment', nullif(btrim(coalesce(p_general_comment, '')), ''))::text);
  if v_cached is not null then
    return v_cached;
  end if;

  perform pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_submission_id::text, 0));
  select * into v_grade
  from public.grades grade
  where grade.submission_id = p_submission_id
  for update;
  if found then
    if v_grade.cloud_version <> p_expected_cloud_version then
      raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
    end if;
    if v_grade.status = 'Returned' then
      raise exception 'ESSAY_GRADE_REOPEN_REQUIRED' using errcode = '55000';
    end if;
    v_grade_id := v_grade.id;
    update public.grades
    set score = p_score,
        max_score = 10.00,
        general_comment = nullif(btrim(coalesce(p_general_comment, '')), ''),
        status = 'Graded',
        grader_id = (select auth.uid()),
        graded_at = v_now,
        returned_at = null,
        revision = revision + 1,
        cloud_version = private.next_public_cloud_version(),
        updated_at = v_now
    where id = v_grade_id
      and cloud_version = p_expected_cloud_version;
    if not found then
      raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
    end if;
  else
    if p_expected_cloud_version <> 0 then
      raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
    end if;
    v_grade_id := pg_catalog.gen_random_uuid();
    insert into public.grades(
      id, organization_id, submission_id, status, score, max_score,
      general_comment, grader_id, graded_at, returned_at,
      revision, cloud_version, created_at, updated_at)
    values (
      v_grade_id, v_submission.organization_id, p_submission_id, 'Graded',
      p_score, 10.00, nullif(btrim(coalesce(p_general_comment, '')), ''),
      (select auth.uid()), v_now, null, 1, private.next_public_cloud_version(),
      v_now, v_now);
  end if;

  delete from public.rubric_scores where grade_id = v_grade_id;
  insert into public.rubric_scores(
    id, organization_id, grade_id, criterion_key, title, score, max_score,
    comment, sort_order, cloud_version, created_at, updated_at)
  select pg_catalog.gen_random_uuid(), v_submission.organization_id, v_grade_id,
    btrim(item ->> 'criterionKey'), btrim(item ->> 'title'),
    (item ->> 'score')::numeric, (item ->> 'maxScore')::numeric,
    nullif(btrim(coalesce(item ->> 'comment', '')), ''),
    coalesce((item ->> 'order')::integer, 0),
    private.next_public_cloud_version(), v_now, v_now
  from jsonb_array_elements(coalesce(p_rubric_scores, '[]'::jsonb)) item;

  v_result := private.public_essay_grade_result(p_submission_id);
  perform private.write_public_teacher_audit(
    v_submission.organization_id,
    v_submission.session_id,
    'SavePublicEssayGrade',
    'grades',
    v_grade_id,
    p_request_id,
    case when v_grade.id is null then null else to_jsonb(v_grade) end,
    v_result);
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.return_public_essay_grade(
  p_submission_id uuid,
  p_message text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_grade public.grades%rowtype;
  v_cached jsonb;
  v_result jsonb;
  v_now timestamptz := pg_catalog.now();
begin
  v_submission := private.require_public_essay_submission(p_submission_id);
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'ESSAY_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_submission.organization_id,
    'ReturnPublicEssayGrade',
    jsonb_build_object(
      'submissionId', p_submission_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'message', nullif(btrim(coalesce(p_message, '')), ''))::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_grade
  from public.grades grade
  where grade.submission_id = p_submission_id
  for update;
  if not found or v_grade.cloud_version <> p_expected_cloud_version then
    raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_grade.status <> 'Graded'
     or v_grade.score is null
     or v_grade.score < 0
     or v_grade.score > 10.00 then
    raise exception 'ESSAY_GRADE_NOT_RETURNABLE' using errcode = '55000';
  end if;
  update public.grades
  set status = 'Returned',
      returned_at = v_now,
      grader_id = (select auth.uid()),
      graded_at = coalesce(graded_at, v_now),
      revision = revision + 1,
      cloud_version = private.next_public_cloud_version(),
      updated_at = v_now
  where id = v_grade.id and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  perform private.emit_public_student_notification(
    v_submission.organization_id,
    v_submission.session_id,
    v_submission.participant_id,
    'GradeReturned',
    p_request_id,
    v_grade.id,
    p_submission_id => p_submission_id,
    p_message => nullif(btrim(coalesce(p_message, '')), ''),
    p_score => v_grade.score,
    p_max_score => 10.00);
  v_result := private.public_essay_grade_result(p_submission_id);
  perform private.write_public_teacher_audit(
    v_submission.organization_id,
    v_submission.session_id,
    'ReturnPublicEssayGrade',
    'grades',
    v_grade.id,
    p_request_id,
    to_jsonb(v_grade),
    v_result || jsonb_build_object('message', nullif(btrim(coalesce(p_message, '')), '')));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reopen_public_essay_grade(
  p_submission_id uuid,
  p_reason text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_submission public.submissions%rowtype;
  v_grade public.grades%rowtype;
  v_cached jsonb;
  v_result jsonb;
  v_now timestamptz := pg_catalog.now();
begin
  if length(btrim(coalesce(p_reason, ''))) < 3 then
    raise exception 'ESSAY_GRADE_REOPEN_REASON_REQUIRED' using errcode = '22023';
  end if;
  v_submission := private.require_public_essay_submission(p_submission_id);
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'ESSAY_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;
  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_submission.organization_id,
    'ReopenPublicEssayGrade',
    jsonb_build_object(
      'submissionId', p_submission_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'reason', btrim(p_reason))::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_grade
  from public.grades grade
  where grade.submission_id = p_submission_id
  for update;
  if not found or v_grade.cloud_version <> p_expected_cloud_version then
    raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_grade.status <> 'Returned' or v_grade.returned_at is null then
    raise exception 'ESSAY_GRADE_NOT_REOPENABLE' using errcode = '55000';
  end if;
  update public.grades
  set status = 'Graded',
      returned_at = null,
      grader_id = (select auth.uid()),
      revision = revision + 1,
      cloud_version = private.next_public_cloud_version(),
      updated_at = v_now
  where id = v_grade.id and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'ESSAY_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  perform private.emit_public_student_notification(
    v_submission.organization_id,
    v_submission.session_id,
    v_submission.participant_id,
    'GradeReopened',
    p_request_id,
    v_grade.id,
    p_submission_id => p_submission_id,
    p_reason => btrim(p_reason));
  v_result := private.public_essay_grade_result(p_submission_id);
  perform private.write_public_teacher_audit(
    v_submission.organization_id,
    v_submission.session_id,
    'ReopenPublicEssayGrade',
    'grades',
    v_grade.id,
    p_request_id,
    to_jsonb(v_grade),
    v_result || jsonb_build_object('reason', btrim(p_reason)));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

revoke all on function public.get_public_essay_grade(uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.save_public_essay_grade(uuid,numeric,jsonb,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.return_public_essay_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.reopen_public_essay_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.get_public_essay_grade(uuid) to authenticated;
grant execute on function public.save_public_essay_grade(uuid,numeric,jsonb,text,bigint,uuid) to authenticated;
grant execute on function public.return_public_essay_grade(uuid,text,bigint,uuid) to authenticated;
grant execute on function public.reopen_public_essay_grade(uuid,text,bigint,uuid) to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 24,
    updated_at = pg_catalog.now()
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
      'get_public_essay_grade','save_public_essay_grade',
      'return_public_essay_grade','reopen_public_essay_grade',
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
