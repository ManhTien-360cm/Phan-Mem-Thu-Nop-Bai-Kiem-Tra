begin;

create or replace function private.calculate_public_quiz_grade(p_attempt_id uuid)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_snapshot_question jsonb;
  v_snapshot_choice jsonb;
  v_answer public.quiz_answers%rowtype;
  v_question public.quiz_questions%rowtype;
  v_question_id uuid;
  v_choice_id uuid;
  v_seen_questions uuid[] := array[]::uuid[];
  v_snapshot_choice_ids uuid[];
  v_selected_ids uuid[];
  v_correct_ids uuid[];
  v_total integer := 0;
  v_answered integer := 0;
  v_correct integer := 0;
  v_score numeric(10,2) := 0;
  v_max_score numeric(10,2) := 0;
begin
  select a.* into v_attempt
  from public.quiz_attempts a
  where a.id = p_attempt_id;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  select s.* into v_session
  from public.exam_sessions s
  where s.id = v_attempt.session_id
    and s.organization_id = v_attempt.organization_id;
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_GRAPH_INVALID' using errcode = '22023';
  end if;
  if v_attempt.source_mode <> 'PublicCloud'
     or v_session.access_mode <> 'PublicCloud'
     or v_session.delivery_type <> 'MultipleChoice'
     or v_attempt.status <> 'Finalized'
     or v_attempt.exam_version <> v_session.exam_version
     or not exists (
       select 1
       from public.exams e
       where e.id = v_session.exam_id
         and e.organization_id = v_session.organization_id
         and e.delivery_type = 'MultipleChoice')
     or not exists (
       select 1
       from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_attempt.session_id
         and p.organization_id = v_attempt.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_GRAPH_INVALID' using errcode = '22023';
  end if;
  if pg_catalog.jsonb_typeof(v_attempt.snapshot_json) <> 'array'
     or pg_catalog.jsonb_array_length(v_attempt.snapshot_json) = 0 then
    raise exception 'PUBLIC_QUIZ_SNAPSHOT_INVALID' using errcode = '22023';
  end if;

  for v_snapshot_question in
    select value from pg_catalog.jsonb_array_elements(v_attempt.snapshot_json)
  loop
    begin
      v_question_id := (v_snapshot_question ->> 'id')::uuid;
    exception when others then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_INVALID' using errcode = '22023';
    end;
    if v_question_id = any(v_seen_questions) then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_DUPLICATE' using errcode = '22023';
    end if;
    v_seen_questions := pg_catalog.array_append(v_seen_questions, v_question_id);

    select q.* into v_question
    from public.quiz_questions q
    where q.id = v_question_id
      and q.exam_id = v_session.exam_id
      and q.organization_id = v_attempt.organization_id
      and q.version = v_attempt.exam_version;
    if not found
       or v_question.points <= 0
       or v_question.points <> pg_catalog.round(v_question.points, 2)
       or v_question.points is distinct from (v_snapshot_question ->> 'points')::numeric
       or v_question.multiple is distinct from
          coalesce((v_snapshot_question ->> 'multiple')::boolean, false)
       or pg_catalog.jsonb_typeof(v_snapshot_question -> 'choices') <> 'array' then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_QUESTION_MISMATCH' using errcode = '22023';
    end if;

    v_snapshot_choice_ids := array[]::uuid[];
    for v_snapshot_choice in
      select value from pg_catalog.jsonb_array_elements(v_snapshot_question -> 'choices')
    loop
      begin
        v_choice_id := (v_snapshot_choice ->> 'id')::uuid;
      exception when others then
        raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_INVALID' using errcode = '22023';
      end;
      if v_choice_id = any(v_snapshot_choice_ids)
         or not exists (
           select 1 from public.quiz_choices c
           where c.id = v_choice_id
             and c.question_id = v_question_id
             and c.organization_id = v_attempt.organization_id) then
        raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_MISMATCH' using errcode = '22023';
      end if;
      v_snapshot_choice_ids := pg_catalog.array_append(v_snapshot_choice_ids, v_choice_id);
    end loop;
    if coalesce(pg_catalog.array_length(v_snapshot_choice_ids, 1), 0) = 0
       or coalesce(pg_catalog.array_length(v_snapshot_choice_ids, 1), 0) <>
          (select pg_catalog.count(*) from public.quiz_choices c
           where c.question_id = v_question_id
             and c.organization_id = v_attempt.organization_id) then
      raise exception 'PUBLIC_QUIZ_SNAPSHOT_CHOICE_COUNT_MISMATCH' using errcode = '22023';
    end if;

    select coalesce(pg_catalog.array_agg(c.id order by c.id), array[]::uuid[])
      into v_correct_ids
    from public.quiz_choices c
    where c.question_id = v_question_id
      and c.organization_id = v_attempt.organization_id
      and c.is_correct;
    if coalesce(pg_catalog.array_length(v_correct_ids, 1), 0) = 0 then
      raise exception 'PUBLIC_QUIZ_CORRECT_CHOICE_MISSING' using errcode = '22023';
    end if;

    select a.* into v_answer
    from public.quiz_answers a
    where a.attempt_id = p_attempt_id
      and a.question_id = v_question_id;
    v_selected_ids := array[]::uuid[];
    if found then
      if v_answer.organization_id <> v_attempt.organization_id
         or pg_catalog.jsonb_typeof(v_answer.choice_ids) <> 'array' then
        raise exception 'PUBLIC_QUIZ_ANSWER_GRAPH_INVALID' using errcode = '22023';
      end if;
      for v_snapshot_choice in
        select value from pg_catalog.jsonb_array_elements(v_answer.choice_ids)
      loop
        begin
          v_choice_id := (v_snapshot_choice #>> '{}')::uuid;
        exception when others then
          raise exception 'PUBLIC_QUIZ_ANSWER_CHOICE_INVALID' using errcode = '22023';
        end;
        if v_choice_id = any(v_selected_ids)
           or not exists (
             select 1 from public.quiz_choices c
             where c.id = v_choice_id
               and c.question_id = v_question_id
               and c.organization_id = v_attempt.organization_id) then
          raise exception 'PUBLIC_QUIZ_ANSWER_CHOICE_MISMATCH' using errcode = '22023';
        end if;
        v_selected_ids := pg_catalog.array_append(v_selected_ids, v_choice_id);
      end loop;
      if not v_question.multiple
         and coalesce(pg_catalog.array_length(v_selected_ids, 1), 0) > 1 then
        raise exception 'PUBLIC_QUIZ_ANSWER_MULTIPLE_INVALID' using errcode = '22023';
      end if;
    end if;

    v_total := v_total + 1;
    v_max_score := v_max_score + v_question.points;
    if coalesce(pg_catalog.array_length(v_selected_ids, 1), 0) > 0 then
      v_answered := v_answered + 1;
      if v_selected_ids @> v_correct_ids and v_correct_ids @> v_selected_ids then
        v_correct := v_correct + 1;
        v_score := v_score + v_question.points;
      end if;
    end if;
  end loop;

  if v_total <> (
       select pg_catalog.count(*)
       from public.quiz_questions q
       where q.exam_id = v_session.exam_id
         and q.organization_id = v_attempt.organization_id
         and q.version = v_attempt.exam_version)
     or exists (
       select 1 from public.quiz_answers a
       where a.attempt_id = p_attempt_id
         and (a.organization_id <> v_attempt.organization_id
           or not (a.question_id = any(v_seen_questions)))) then
    raise exception 'PUBLIC_QUIZ_ANSWER_GRAPH_INVALID' using errcode = '22023';
  end if;
  if v_max_score <> 10.00
     or v_attempt.max_score <> v_max_score
     or v_score < 0
     or v_score > v_max_score
     or v_score <> pg_catalog.round(v_score, 2) then
    raise exception 'PUBLIC_QUIZ_SCORE_INVARIANT_INVALID' using errcode = '22023';
  end if;

  return pg_catalog.jsonb_build_object(
    'score', v_score,
    'maxScore', v_max_score,
    'totalQuestions', v_total,
    'answeredQuestions', v_answered,
    'correctCount', v_correct,
    'incorrectCount', v_answered - v_correct,
    'unansweredCount', v_total - v_answered);
end
$function$;
revoke all on function private.calculate_public_quiz_grade(uuid)
  from public, anon, authenticated, service_role;

create or replace function public.save_public_quiz_grade(
  p_attempt_id uuid,
  p_score numeric,
  p_general_comment text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_calculated jsonb;
  v_result jsonb;
  v_score numeric(10,2);
  v_max_score numeric(10,2);
begin
  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or v_session.delivery_type <> 'MultipleChoice'
     or not exists (
       select 1 from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'SavePublicQuizGrade',
    pg_catalog.jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'score', p_score,
      'generalComment', nullif(pg_catalog.btrim(coalesce(p_general_comment, '')), '')
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized' then
    raise exception 'QUIZ_ATTEMPT_NOT_FINALIZED' using errcode = '55000';
  end if;
  if v_attempt.grading_status = 'Returned' then
    raise exception 'QUIZ_GRADE_REOPEN_REQUIRED' using errcode = '55000';
  end if;

  v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
  v_score := (v_calculated ->> 'score')::numeric;
  v_max_score := (v_calculated ->> 'maxScore')::numeric;
  if p_score is not null
     and (p_score <> pg_catalog.round(p_score, 2) or p_score <> v_score) then
    raise exception 'QUIZ_GRADE_CLIENT_SCORE_MISMATCH' using errcode = '22023';
  end if;

  update public.quiz_attempts
  set auto_score = v_score,
      score = v_score,
      max_score = v_max_score,
      general_comment = nullif(pg_catalog.btrim(coalesce(p_general_comment, '')), ''),
      grading_status = 'Graded',
      returned_at = null,
      grader_id = (select auth.uid()),
      graded_at = pg_catalog.now(),
      updated_at = pg_catalog.now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'SavePublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    pg_catalog.to_jsonb(v_attempt),
    v_result || pg_catalog.jsonb_build_object('summary', v_calculated));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.return_public_quiz_grade(
  p_attempt_id uuid,
  p_message text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_calculated jsonb;
  v_result jsonb;
begin
  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or v_session.delivery_type <> 'MultipleChoice'
     or not exists (
       select 1 from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'ReturnPublicQuizGrade',
    pg_catalog.jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'message', nullif(pg_catalog.btrim(coalesce(p_message, '')), '')
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized'
     or v_attempt.grading_status <> 'Graded' then
    raise exception 'QUIZ_GRADE_NOT_RETURNABLE' using errcode = '55000';
  end if;
  v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
  if v_attempt.auto_score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.max_score is distinct from (v_calculated ->> 'maxScore')::numeric then
    raise exception 'QUIZ_GRADE_NOT_AUTHORITATIVE' using errcode = '22023';
  end if;

  update public.quiz_attempts
  set grading_status = 'Returned',
      returned_at = pg_catalog.now(),
      grader_id = (select auth.uid()),
      graded_at = coalesce(graded_at, pg_catalog.now()),
      updated_at = pg_catalog.now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'ReturnPublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    pg_catalog.to_jsonb(v_attempt),
    v_result || pg_catalog.jsonb_build_object(
      'message', nullif(pg_catalog.btrim(coalesce(p_message, '')), ''),
      'summary', v_calculated));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

create or replace function public.reopen_public_quiz_grade(
  p_attempt_id uuid,
  p_reason text,
  p_expected_cloud_version bigint,
  p_request_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_attempt public.quiz_attempts%rowtype;
  v_session public.exam_sessions%rowtype;
  v_cached jsonb;
  v_calculated jsonb;
  v_result jsonb;
begin
  if pg_catalog.length(pg_catalog.btrim(coalesce(p_reason, ''))) < 3 then
    raise exception 'QUIZ_GRADE_REOPEN_REASON_REQUIRED' using errcode = '22023';
  end if;
  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id and source_mode = 'PublicCloud';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  v_session := private.require_public_session_teacher(v_attempt.session_id);
  if v_attempt.organization_id <> v_session.organization_id
     or v_session.delivery_type <> 'MultipleChoice'
     or not exists (
       select 1 from public.session_participants p
       where p.id = v_attempt.participant_id
         and p.session_id = v_session.id
         and p.organization_id = v_session.organization_id
         and p.source_mode = 'PublicCloud') then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_ORGANIZATION_MISMATCH' using errcode = '42501';
  end if;
  if p_expected_cloud_version is null or p_expected_cloud_version < 1 then
    raise exception 'QUIZ_GRADE_VERSION_REQUIRED' using errcode = '22023';
  end if;

  v_cached := private.begin_public_teacher_mutation(
    p_request_id,
    v_session.organization_id,
    'ReopenPublicQuizGrade',
    pg_catalog.jsonb_build_object(
      'attemptId', p_attempt_id,
      'expectedCloudVersion', p_expected_cloud_version,
      'reason', pg_catalog.btrim(p_reason)
    )::text);
  if v_cached is not null then
    return v_cached;
  end if;

  select * into v_attempt
  from public.quiz_attempts
  where id = p_attempt_id
    and organization_id = v_session.organization_id
    and source_mode = 'PublicCloud'
  for update;
  if v_attempt.cloud_version <> p_expected_cloud_version then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;
  if v_attempt.status <> 'Finalized'
     or v_attempt.grading_status <> 'Returned'
     or v_attempt.returned_at is null then
    raise exception 'QUIZ_GRADE_NOT_REOPENABLE' using errcode = '55000';
  end if;
  v_calculated := private.calculate_public_quiz_grade(p_attempt_id);
  if v_attempt.auto_score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.score is distinct from (v_calculated ->> 'score')::numeric
     or v_attempt.max_score is distinct from (v_calculated ->> 'maxScore')::numeric then
    raise exception 'QUIZ_GRADE_NOT_AUTHORITATIVE' using errcode = '22023';
  end if;

  update public.quiz_attempts
  set grading_status = 'Graded',
      returned_at = null,
      grader_id = (select auth.uid()),
      updated_at = pg_catalog.now()
  where id = p_attempt_id
    and cloud_version = p_expected_cloud_version;
  if not found then
    raise exception 'QUIZ_GRADE_VERSION_CONFLICT' using errcode = '40001';
  end if;

  v_result := private.public_quiz_grade_result(p_attempt_id);
  perform private.write_public_teacher_audit(
    v_session.organization_id,
    v_session.id,
    'ReopenPublicQuizGrade',
    'quiz_attempts',
    p_attempt_id,
    p_request_id,
    pg_catalog.to_jsonb(v_attempt),
    v_result || pg_catalog.jsonb_build_object(
      'reason', pg_catalog.btrim(p_reason),
      'summary', v_calculated));
  return private.finish_public_teacher_mutation(p_request_id, v_result);
end
$function$;

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
     and old.grading_status = 'Returned' and new.grading_status = 'Graded' then
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

drop policy if exists quiz_attempts_student_own on public.quiz_attempts;
create policy quiz_attempts_student_own on public.quiz_attempts
for select to authenticated
using (
  organization_id = (select public.current_organization_id())
  and (select public.current_examtransfer_role()) = 'Student'
  and grading_status = 'Returned'
  and returned_at is not null
  and exists (
    select 1
    from public.session_participants p
    where p.id = participant_id
      and p.session_id = session_id
      and p.organization_id = organization_id
      and p.user_id = (select auth.uid())
      and p.source_mode = 'PublicCloud'));

create or replace function public.get_public_quiz_attempt(p_attempt_id uuid)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_result jsonb;
begin
  select pg_catalog.jsonb_build_object(
    'id', a.id,
    'sessionId', a.session_id,
    'participantId', a.participant_id,
    'status', a.status,
    'examVersion', a.exam_version,
    'resultPolicy', a.result_policy,
    'startedAtUtc', a.started_at,
    'deadlineUtc', a.deadline_at,
    'finalizedAtUtc', a.finalized_at,
    'scoreVisible', a.status = 'Finalized'
      and a.grading_status = 'Returned' and a.returned_at is not null,
    'score', case when a.status = 'Finalized'
      and a.grading_status = 'Returned' and a.returned_at is not null
      then a.score else null end,
    'maxScore', a.max_score,
    'questions', a.snapshot_json,
    'answers', coalesce((
      select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
        'questionId', ans.question_id,
        'choiceIds', ans.choice_ids,
        'revision', ans.revision,
        'clientUpdatedAtUtc', ans.client_updated_at)
        order by ans.question_id)
      from public.quiz_answers ans
      where ans.attempt_id = a.id
        and ans.organization_id = a.organization_id), '[]'::jsonb))
  into v_result
  from public.quiz_attempts a
  join public.session_participants p
    on p.id = a.participant_id
   and p.session_id = a.session_id
   and p.organization_id = a.organization_id
   and p.user_id = v_profile.id
  join public.exam_sessions s
    on s.id = a.session_id
   and s.organization_id = a.organization_id
   and s.access_mode = 'PublicCloud'
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud';
  if v_result is null then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;
  return v_result;
end
$function$;

create or replace function public.get_public_quiz_attempt_review(p_attempt_id uuid)
returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_attempt public.quiz_attempts%rowtype;
  v_questions jsonb;
  v_returned boolean;
begin
  select a.* into v_attempt
  from public.quiz_attempts a
  join public.session_participants p
    on p.id = a.participant_id
   and p.session_id = a.session_id
   and p.organization_id = a.organization_id
   and p.user_id = v_profile.id
  join public.exam_sessions s
    on s.id = a.session_id
   and s.organization_id = a.organization_id
   and s.access_mode = 'PublicCloud'
  where a.id = p_attempt_id
    and a.organization_id = v_profile.organization_id
    and a.source_mode = 'PublicCloud'
    and a.status = 'Finalized';
  if not found then
    raise exception 'PUBLIC_QUIZ_ATTEMPT_NOT_FOUND' using errcode = 'P0002';
  end if;

  v_returned := v_attempt.grading_status = 'Returned' and v_attempt.returned_at is not null;
  if not v_returned then
    v_questions := v_attempt.snapshot_json;
  else
    select coalesce(
      pg_catalog.jsonb_agg(
        q.value || pg_catalog.jsonb_build_object(
          'choices',
          (select coalesce(
             pg_catalog.jsonb_agg(
               c.value || pg_catalog.jsonb_build_object(
                 'correct', coalesce(qc.is_correct, false))),
             '[]'::jsonb)
           from pg_catalog.jsonb_array_elements(q.value -> 'choices') c(value)
           left join public.quiz_choices qc
             on qc.id = (c.value ->> 'id')::uuid
            and qc.question_id = (q.value ->> 'id')::uuid
            and qc.organization_id = v_attempt.organization_id))),
      '[]'::jsonb)
    into v_questions
    from pg_catalog.jsonb_array_elements(v_attempt.snapshot_json) q(value);
  end if;

  return pg_catalog.jsonb_build_object(
    'attemptId', v_attempt.id,
    'scoreVisible', v_returned,
    'score', case when v_returned then v_attempt.score else null end,
    'maxScore', v_attempt.max_score,
    'correctAnswersVisible', v_returned,
    'generalComment', case when v_returned then v_attempt.general_comment else null end,
    'questions', v_questions,
    'answers', coalesce((
      select pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
        'questionId', ans.question_id,
        'choiceIds', ans.choice_ids,
        'revision', ans.revision,
        'clientUpdatedAtUtc', ans.client_updated_at)
        order by ans.question_id)
      from public.quiz_answers ans
      where ans.attempt_id = v_attempt.id
        and ans.organization_id = v_attempt.organization_id), '[]'::jsonb));
end
$function$;

revoke all on function public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.return_public_quiz_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.reopen_public_quiz_grade(uuid,text,bigint,uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.get_public_quiz_attempt(uuid)
  from public, anon, authenticated, service_role;
revoke all on function public.get_public_quiz_attempt_review(uuid)
  from public, anon, authenticated, service_role;
grant execute on function public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)
  to authenticated;
grant execute on function public.return_public_quiz_grade(uuid,text,bigint,uuid)
  to authenticated;
grant execute on function public.reopen_public_quiz_grade(uuid,text,bigint,uuid)
  to authenticated;
grant execute on function public.get_public_quiz_attempt(uuid)
  to authenticated;
grant execute on function public.get_public_quiz_attempt_review(uuid)
  to authenticated;

update public.examtransfer_cloud_meta
set schema_version = 25,
    updated_at = pg_catalog.now()
where id = 1;

commit;
