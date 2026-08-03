begin;

alter table public.quiz_attempts
  add column if not exists attempt_number integer not null default 1;
alter table public.quiz_attempts
  drop constraint if exists ck_quiz_attempts_attempt_number;
alter table public.quiz_attempts
  add constraint ck_quiz_attempts_attempt_number check (attempt_number > 0);

create unique index if not exists ux_quiz_attempts_participant_attempt_number
  on public.quiz_attempts(organization_id, session_id, participant_id, attempt_number);
create index if not exists ix_grades_student_results
  on public.grades(organization_id, returned_at desc, submission_id)
  where status = 'Returned' and returned_at is not null;
create index if not exists ix_quiz_attempts_student_results
  on public.quiz_attempts(organization_id, returned_at desc, id)
  where grading_status = 'Returned' and returned_at is not null;
create index if not exists ix_participants_student_results
  on public.session_participants(organization_id, user_id, session_id, id)
  where source_mode = 'PublicCloud';

create or replace function public.get_student_results(
  p_page_size integer default 50,
  p_cursor_returned_at timestamptz default null,
  p_cursor_result_type text default null,
  p_cursor_result_id uuid default null)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_cursor_type_order integer;
  v_items jsonb;
  v_next_cursor jsonb;
begin
  if p_page_size is null or p_page_size < 1 or p_page_size > 100 then
    raise exception 'STUDENT_RESULTS_PAGE_SIZE_INVALID' using errcode = '22023';
  end if;
  if (p_cursor_returned_at is null) <> (p_cursor_result_type is null)
     or (p_cursor_returned_at is null) <> (p_cursor_result_id is null) then
    raise exception 'STUDENT_RESULTS_CURSOR_INCOMPLETE' using errcode = '22023';
  end if;
  if p_cursor_returned_at is not null then
    v_cursor_type_order := case p_cursor_result_type
      when 'EssayFile' then 1
      when 'Quiz' then 2
      else null
    end;
    if v_cursor_type_order is null then
      raise exception 'STUDENT_RESULTS_CURSOR_TYPE_INVALID' using errcode = '22023';
    end if;
  end if;

  with candidates as (
    select
      1 as result_type_order,
      'EssayFile'::text as result_type,
      submission.id as result_id,
      grade.returned_at,
      pg_catalog.jsonb_build_object(
        'resultType', 'EssayFile',
        'examId', exam.id,
        'examTitle', exam.title,
        'sessionId', session.id,
        'submissionId', submission.id,
        'attemptId', null,
        'attemptNumber', submission.attempt_number,
        'status', 'Returned',
        'score', grade.score,
        'maxScore', grade.max_score,
        'generalComment', grade.general_comment,
        'returnedAtUtc', grade.returned_at,
        'attachments', coalesce((
          select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
            'attachmentId', attachment.id,
            'fileName', coalesce(
              nullif(pg_catalog.regexp_replace(
                pg_catalog.regexp_replace(attachment.name, E'^.*[\\\\/]', ''),
                '[[:cntrl:]]', '', 'g'), ''),
              'attachment-' || pg_catalog.replace(attachment.id::text, '-', '')),
            'contentType', coalesce(nullif(attachment.mime_type, ''), 'application/octet-stream'),
            'sizeBytes', attachment.size_bytes)
            order by attachment.created_at, attachment.id)
          from public.graded_attachments attachment
          where attachment.grade_id = grade.id
            and attachment.organization_id = grade.organization_id), '[]'::jsonb),
        'quizSummary', null) as payload
    from public.grades grade
    join public.submissions submission
      on submission.id = grade.submission_id
     and submission.organization_id = grade.organization_id
     and submission.source_mode = 'PublicCloud'
     and submission.is_official = true
     and submission.status in ('Submitted', 'LateSubmitted')
    join public.session_participants participant
      on participant.id = submission.participant_id
     and participant.session_id = submission.session_id
     and participant.organization_id = grade.organization_id
     and participant.user_id = v_profile.id
     and participant.source_mode = 'PublicCloud'
    join public.exam_sessions session
      on session.id = submission.session_id
     and session.organization_id = grade.organization_id
     and session.access_mode = 'PublicCloud'
     and session.delivery_type = 'FileSubmission'
    join public.exams exam
      on exam.id = session.exam_id
     and exam.organization_id = grade.organization_id
     and exam.delivery_type = 'FileSubmission'
    where grade.organization_id = v_profile.organization_id
      and grade.status = 'Returned'
      and grade.returned_at is not null

    union all

    select
      2 as result_type_order,
      'Quiz'::text as result_type,
      attempt.id as result_id,
      attempt.returned_at,
      pg_catalog.jsonb_build_object(
        'resultType', 'Quiz',
        'examId', exam.id,
        'examTitle', exam.title,
        'sessionId', session.id,
        'submissionId', null,
        'attemptId', attempt.id,
        'attemptNumber', attempt.attempt_number,
        'status', 'Returned',
        'score', attempt.score,
        'maxScore', attempt.max_score,
        'generalComment', attempt.general_comment,
        'returnedAtUtc', attempt.returned_at,
        'attachments', '[]'::jsonb,
        'quizSummary', pg_catalog.jsonb_build_object(
          'totalQuestions', (calculated.value ->> 'totalQuestions')::integer,
          'answeredQuestions', (calculated.value ->> 'answeredQuestions')::integer,
          'correctCount', (calculated.value ->> 'correctCount')::integer,
          'incorrectCount', (calculated.value ->> 'incorrectCount')::integer,
          'unansweredCount', (calculated.value ->> 'unansweredCount')::integer,
          'earnedPoints', (calculated.value ->> 'score')::numeric,
          'maxPoints', (calculated.value ->> 'maxScore')::numeric)) as payload
    from public.quiz_attempts attempt
    join public.session_participants participant
      on participant.id = attempt.participant_id
     and participant.session_id = attempt.session_id
     and participant.organization_id = attempt.organization_id
     and participant.user_id = v_profile.id
     and participant.source_mode = 'PublicCloud'
    join public.exam_sessions session
      on session.id = attempt.session_id
     and session.organization_id = attempt.organization_id
     and session.access_mode = 'PublicCloud'
     and session.delivery_type = 'MultipleChoice'
    join public.exams exam
      on exam.id = session.exam_id
     and exam.organization_id = attempt.organization_id
     and exam.delivery_type = 'MultipleChoice'
    cross join lateral (
      select private.calculate_public_quiz_grade(attempt.id) as value
    ) calculated
    where attempt.organization_id = v_profile.organization_id
      and attempt.source_mode = 'PublicCloud'
      and attempt.status = 'Finalized'
      and attempt.grading_status = 'Returned'
      and attempt.returned_at is not null
      and attempt.score = (calculated.value ->> 'score')::numeric
      and attempt.max_score = (calculated.value ->> 'maxScore')::numeric
  ), page_rows as (
    select *, pg_catalog.row_number() over (
      order by returned_at desc, result_type_order, result_id) as ordinal
    from candidates
    where p_cursor_returned_at is null
       or returned_at < p_cursor_returned_at
       or (returned_at = p_cursor_returned_at and result_type_order > v_cursor_type_order)
       or (returned_at = p_cursor_returned_at and result_type_order = v_cursor_type_order
           and result_id > p_cursor_result_id)
    order by returned_at desc, result_type_order, result_id
    limit p_page_size + 1
  )
  select
    coalesce(pg_catalog.jsonb_agg(payload order by returned_at desc, result_type_order, result_id)
      filter (where ordinal <= p_page_size), '[]'::jsonb),
    case when pg_catalog.count(*) > p_page_size then
      (pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
        'returnedAtUtc', returned_at,
        'resultType', result_type,
        'resultId', result_id)
        order by returned_at desc, result_type_order, result_id) -> (p_page_size - 1))
      else null end
  into v_items, v_next_cursor
  from page_rows;

  return pg_catalog.jsonb_build_object(
    'items', v_items,
    'nextCursor', v_next_cursor);
end
$function$;

revoke all on function public.get_student_results(integer,timestamptz,text,uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.get_student_results(integer,timestamptz,text,uuid)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 26,
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
  return pg_catalog.jsonb_build_object(
    'schemaVersion',
      (select schema_version from public.examtransfer_cloud_meta where id = 1),
    'criticalRpcs', pg_catalog.jsonb_build_array(
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
      'get_public_student_notification_events','get_student_results'),
    'buckets', coalesce((
      select pg_catalog.jsonb_agg(id order by id)
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
