begin;
create extension if not exists pgtap with schema extensions;
select plan(22);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),27,
  'Quiz grading and score-10 invariants remain available at schema 27');
select has_column('public','quiz_attempts','auto_score','quiz attempts store immutable auto score');
select has_column('public','quiz_attempts','grading_status','quiz attempts store grading status');
select has_column('public','quiz_attempts','general_comment','quiz attempts store teacher comment');
select has_column('public','quiz_attempts','grader_id','quiz attempts store grader');
select has_column('public','quiz_attempts','graded_at','quiz attempts store graded timestamp');
select has_column('public','quiz_attempts','returned_at','quiz attempts store returned timestamp');
select has_function('public','get_public_quiz_attempt_review',array['uuid'],
  'student quiz review RPC exists');
select has_function('public','get_public_quiz_attempt',array['uuid'],
  'student attempt RPC remains available');
select has_function('public','get_teacher_quiz_attempts',array['uuid'],
  'teacher attempt RPC remains available');
select has_function('public','finalize_public_quiz_attempt',array['uuid','text'],
  'public finalize RPC remains available');
select has_function('private','notify_public_quiz_grade_returned',array[]::text[],
  'PublicCloud grade-return broadcast trigger function exists');
select has_trigger('public','quiz_attempts','quiz_attempts_notify_grade_returned',
  'quiz attempt return emits an equivalent realtime event');
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select ok(public.get_examtransfer_cloud_capabilities()->'criticalRpcs'
  ? 'get_public_quiz_attempt_review',
  'capabilities advertise masked student review');
select ok(position('returned_at is not null' in lower(pg_get_functiondef(
  'public.get_public_quiz_attempt(uuid)'::regprocedure))) > 0,
  'student score becomes visible after teacher return');
select ok(position('auto_score = v_score' in lower(pg_get_functiondef(
  'public.finalize_public_quiz_attempt(uuid,text)'::regprocedure))) > 0,
  'finalize persists auto score');
select ok(position('grading_status = ''graded''' in lower(pg_get_functiondef(
  'public.finalize_public_quiz_attempt(uuid,text)'::regprocedure))) > 0,
  'finalize marks attempt graded');
select ok(position('v_returned := v_attempt.grading_status = ''returned'' and v_attempt.returned_at is not null' in lower(pg_get_functiondef(
  'public.get_public_quiz_attempt_review(uuid)'::regprocedure))) > 0,
  'review RPC gates correct choices on Returned state and timestamp');
select ok(not has_function_privilege('anon',
  'public.get_public_quiz_attempt_review(uuid)','EXECUTE'),
  'anon cannot execute student review');
select function_lang_is('public','get_public_quiz_attempt_review',array['uuid'],'plpgsql',
  'student review uses controlled plpgsql function');
select ok(has_function_privilege('authenticated',
  'public.get_public_quiz_attempt_review(uuid)','EXECUTE'),
  'authenticated students may execute owned review');
select ok(exists(
  select 1 from pg_constraint
  where conrelid='public.quiz_attempts'::regclass
    and conname='quiz_attempts_score10_check'),
  'score10 constraint exists');

select * from finish();
rollback;
