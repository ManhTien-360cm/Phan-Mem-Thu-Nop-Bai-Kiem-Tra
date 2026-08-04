begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(8);

select has_function(
  'public',
  'save_public_quiz_grade',
  array['uuid','numeric','text','bigint','uuid'],
  'canonical quiz grading RPC remains available');
select is(
  pg_catalog.pg_get_function_result(
    pg_catalog.to_regprocedure(
      'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)')),
  'jsonb',
  'canonical quiz grading RPC retains its result contract');
select ok(
  pg_catalog.to_regprocedure(
    'public.rpc_pull_exam_changes(timestamp with time zone,uuid)') is null,
  'legacy pull RPC is absent');
select ok(
  pg_catalog.to_regprocedure('public.rpc_push_local_events(jsonb[])') is null,
  'legacy push RPC is absent');
select ok(
  pg_catalog.to_regprocedure(
    'public.save_public_quiz_grade(uuid,uuid,text,timestamp with time zone,jsonb)') is null,
  'legacy quiz grade overload is absent');
select ok(
  (select relrowsecurity
   from pg_catalog.pg_class
   where oid = 'public.student_notification_events'::regclass),
  'A-07 event RLS remains enabled');
select is(
  (select count(*)::integer
   from pg_catalog.pg_policies
   where schemaname = 'public'
     and tablename = 'student_notification_events'
     and policyname = 'student_notification_events_student_select'),
  1,
  'A-07 student event policy remains unchanged');
select is(
  (select count(*)::integer
   from pg_catalog.pg_publication_tables
   where pubname = 'supabase_realtime'
     and schemaname = 'public'
     and tablename = 'student_notification_events'),
  1,
  'A-07 Realtime publication remains unchanged');

select * from finish();
rollback;
