begin;
select plan(25);

select is((select schema_version from public.examtransfer_cloud_meta where id=1), 26,
  'complete exam workflow remains available at schema 26');
select has_column('public','exams','quiz_result_policy','exam result policy exists');
select has_column('public','exams','supervision_mode','exam supervision mode exists');
select has_column('public','exam_sessions','delivery_type','session delivery snapshot exists');
select has_column('public','exam_sessions','supervision_mode','session supervision snapshot exists');
select has_column('public','exam_sessions','quiz_result_policy','session result policy snapshot exists');
select has_column('public','exam_sessions','exam_version','session exam version snapshot exists');
select has_column('public','quiz_attempts','result_policy','attempt result policy snapshot exists');
select has_table('public','quiz_import_sources','teacher-only quiz source table exists');
select has_column('public','quiz_import_sources','cloud_version',
  'teacher-owned quiz source supports optimistic cloud versioning');
select policies_are('public','quiz_import_sources',array['quiz_import_sources_staff_all'],
  'quiz sources have staff-only policy');
select has_function('public','get_public_quiz_attempt',array['uuid'],
  'student-safe attempt RPC exists');
select has_function('public','get_teacher_quiz_attempts',array['uuid'],
  'teacher score RPC exists');
select has_function('public','finalize_public_quiz_attempt',array['uuid','text'],
  'safe finalize RPC exists');
select has_function('public','get_public_student_timeline',array['uuid'],
  'safe student timeline RPC exists');
select has_function('private','prevent_live_exam_workflow_change',array[]::text[],
  'workflow immutability trigger function exists');
select ok(exists(
  select 1 from pg_trigger
  where tgrelid='public.exams'::regclass
    and tgname='trg_prevent_live_exam_workflow_change'
    and not tgisinternal), 'workflow immutability trigger is installed');
select ok((select relrowsecurity from pg_class where oid='public.quiz_import_sources'::regclass),
  'quiz source RLS is enabled');
select ok((select relforcerowsecurity from pg_class where oid='public.quiz_import_sources'::regclass),
  'quiz source RLS is forced');
select ok(not has_table_privilege('anon','public.quiz_import_sources','select'),
  'anon cannot read quiz source metadata');
select ok(not has_table_privilege('authenticated','public.quiz_attempts','select'),
  'authenticated has no whole-row quiz attempt select');
select ok(has_column_privilege('authenticated','public.quiz_attempts','snapshot_json','select'),
  'student can read safe question snapshot');
select ok(not has_column_privilege('authenticated','public.quiz_attempts','score','select'),
  'student cannot select raw score column');
select ok(not has_column_privilege('authenticated','public.quiz_attempts','finalize_idempotency_key','select'),
  'student cannot read finalize idempotency key');
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select ok(position('get_public_quiz_attempt' in (
  select public.get_examtransfer_cloud_capabilities()::text)) > 0,
  'capability contract advertises safe attempt RPC');

select * from finish();
rollback;
