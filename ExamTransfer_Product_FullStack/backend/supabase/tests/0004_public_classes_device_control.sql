begin;
select plan(57);

select has_function('public', 'join_public_session', array['uuid','text','text','text','jsonb'], 'join RPC exists');
select has_function('public', 'init_public_submission', array['uuid','text','text','bigint','text'], 'submission init RPC exists');
select has_function('public', 'finalize_public_submission', array['uuid','text'], 'submission finalize RPC exists');
select has_function('public', 'upsert_public_device_heartbeat', array['uuid','text','text','text','jsonb','text','text'], 'heartbeat RPC exists');
select has_function('public', 'report_public_violation', array['uuid','text','text','jsonb'], 'violation RPC exists');
select has_function('public', 'ack_public_device_command', array['uuid','text','text','text','text'], 'command ack RPC exists');
select has_function('public', 'start_public_quiz_attempt', array['uuid','text'], 'quiz start RPC exists');
select has_function('public', 'save_public_quiz_answers', array['uuid','uuid','jsonb','bigint','timestamptz'], 'quiz save RPC exists');
select has_function('public', 'finalize_public_quiz_attempt', array['uuid','text'], 'quiz finalize RPC exists');
select has_function('public', 'issue_public_device_command', array['uuid','uuid','text','text','jsonb','timestamptz','timestamptz','uuid','text'], 'service command RPC exists');

select has_index('public', 'submission_files', 'ux_public_submission_single_file', 'one PublicCloud archive per submission is unique');
select is((select file_size_limit from storage.buckets where id = 'public-submission-archives'), 10485760::bigint, 'public bucket is capped at 10 MiB');
select is((select count(*) from pg_policies where schemaname='public' and tablename='session_participants' and cmd in ('INSERT','UPDATE') and policyname like '%owner%'), 0::bigint, 'Student participant write policies removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='submissions' and cmd in ('INSERT','UPDATE') and policyname like '%owner%'), 0::bigint, 'Student submission write policies removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='submission_files' and cmd in ('INSERT','UPDATE') and policyname like '%owner%'), 0::bigint, 'Student file write policies removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='public_device_connections' and cmd in ('INSERT','UPDATE')), 0::bigint, 'direct connection write policies removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='public_device_command_results' and cmd='INSERT'), 0::bigint, 'direct command-result insert policy removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='violations' and policyname='violations_public_owner_insert'), 0::bigint, 'direct Student violation insert policy removed');
select is((select count(*) from pg_policies where schemaname='public' and tablename='public_device_commands' and cmd in ('INSERT','UPDATE')), 0::bigint, 'authenticated command write policies removed');
select is((select count(*) from pg_policies where schemaname='storage' and tablename='objects' and policyname='examtransfer_public_submission_owner_update'), 0::bigint, 'public archive cannot be overwritten');
select is((select count(*) from pg_policies where schemaname='storage' and tablename='objects' and policyname='examtransfer_public_submission_owner_insert'), 1::bigint, 'public archive has one insert-only policy');
select is((select count(*) from pg_policies where schemaname='realtime' and tablename='messages' and policyname like 'examtransfer_broadcast_%' and (qual like '%extension%broadcast%' or with_check like '%extension%broadcast%')), 2::bigint, 'Broadcast policies are extension-specific');
select is((select count(*) from pg_policies where schemaname='realtime' and tablename='messages' and policyname like 'examtransfer_presence_%' and (qual like '%extension%presence%' or with_check like '%extension%presence%')), 2::bigint, 'Presence policies are extension-specific');
select has_table('public', 'cloud_sync_cursors', 'cloud pull cursor table exists');
select has_column('public', 'session_participants', 'source_mode', 'participant authority marker exists');
select has_column('public', 'session_participants', 'cloud_version', 'participant cloud version exists');
select ok(not has_function_privilege('anon', 'public.join_public_session(uuid,text,text,text,jsonb)', 'EXECUTE'), 'anon cannot execute Student RPC');
select ok(has_function_privilege('authenticated', 'public.join_public_session(uuid,text,text,text,jsonb)', 'EXECUTE'), 'authenticated can execute guarded Student RPC');

-- Real RLS contexts: rows are seeded as migration owner, then calls run as the
-- authenticated role with JWT claims exactly as PostgREST supplies them.
insert into auth.users(id, email) values
  ('10000000-0000-0000-0000-000000000001','teacher1@example.test'),
  ('10000000-0000-0000-0000-000000000002','student1@example.test'),
  ('20000000-0000-0000-0000-000000000002','student2@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('10000000-0000-0000-0000-000000000000','Org One'),
  ('20000000-0000-0000-0000-000000000000','Org Two');
insert into public.profiles(id,organization_id,display_name,role,username,student_code,is_active,date_of_birth) values
  ('10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','Teacher One','Teacher','teacher1',null,true,null),
  ('10000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000000','Student One','Student','S001','S001',true,'2008-01-01'),
  ('20000000-0000-0000-0000-000000000002','20000000-0000-0000-0000-000000000000','Student Two','Student','S002','S002',true,'2008-01-01');
insert into public.classes(id,organization_id,name,code,school_year,status,access_mode,created_at,updated_at) values
  ('11000000-0000-0000-0000-000000000000','10000000-0000-0000-0000-000000000000','Class One','C1','2026','Active','Public',now(),now()),
  ('22000000-0000-0000-0000-000000000000','20000000-0000-0000-0000-000000000000','Class Two','C2','2026','Active','Public',now(),now());
insert into public.exams(id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at) values
  ('12000000-0000-0000-0000-000000000000','10000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','File Exam','IT',60,'Published',1,'10000000-0000-0000-0000-000000000001','FileSubmission','None','Hidden',now(),now()),
  ('12000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','Quiz Exam','IT',60,'Published',1,'10000000-0000-0000-0000-000000000001','MultipleChoice','Standard','Hidden',now(),now()),
  ('22000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000000','22000000-0000-0000-0000-000000000000','Other Exam','IT',60,'Published',1,null,'FileSubmission','None','Hidden',now(),now());
insert into public.exam_sessions(id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,auto_approve,accepting_participants,delivery_type,supervision_mode,quiz_result_policy,exam_version,created_at,updated_at) values
  ('13000000-0000-0000-0000-000000000000','10000000-0000-0000-0000-000000000000','12000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','FILE1','Waiting',now(),'PublicCloud',true,true,'FileSubmission','None','Hidden',1,now(),now()),
  ('13000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','12000000-0000-0000-0000-000000000001','11000000-0000-0000-0000-000000000000','QUIZ1','Waiting',now(),'PublicCloud',true,true,'MultipleChoice','Standard','Hidden',1,now(),now()),
  ('23000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000000','22000000-0000-0000-0000-000000000001','22000000-0000-0000-0000-000000000000','OTHER','Waiting',now(),'PublicCloud',true,true,'FileSubmission','None','Hidden',1,now(),now());
insert into public.class_members(id,organization_id,class_id,user_id,student_code,display_name,created_at,updated_at) values
  (gen_random_uuid(),'10000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','10000000-0000-0000-0000-000000000002','S001','Student One',now(),now()),
  (gen_random_uuid(),'20000000-0000-0000-0000-000000000000','22000000-0000-0000-0000-000000000000','20000000-0000-0000-0000-000000000002','S002','Student Two',now(),now());
insert into public.public_class_assignments(organization_id,class_id,exam_id) values
  ('10000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','12000000-0000-0000-0000-000000000000'),
  ('10000000-0000-0000-0000-000000000000','11000000-0000-0000-0000-000000000000','12000000-0000-0000-0000-000000000001'),
  ('20000000-0000-0000-0000-000000000000','22000000-0000-0000-0000-000000000000','22000000-0000-0000-0000-000000000001');
insert into public.quiz_questions(id,organization_id,exam_id,version,sort_order,question_text,points,multiple) values
  ('14000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','12000000-0000-0000-0000-000000000001',1,1,'Two plus two?',1,false);
insert into public.quiz_choices(id,organization_id,question_id,sort_order,choice_text,is_correct) values
  ('15000000-0000-4000-8000-000000000001','10000000-0000-0000-0000-000000000000','14000000-0000-0000-0000-000000000001',1,'4',true),
  ('15000000-0000-4000-8000-000000000002','10000000-0000-0000-0000-000000000000','14000000-0000-0000-0000-000000000001',2,'5',false);

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"10000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
create temporary table tap_values(key text primary key, value uuid) on commit drop;
insert into tap_values values ('participant', public.join_public_session('13000000-0000-0000-0000-000000000000','device-one','test','1','{}'));
select is(public.join_public_session('13000000-0000-0000-0000-000000000000','device-one','test','1','{}'), (select value from tap_values where key='participant'), 'join RPC is idempotent');
select is((select organization_id from public.session_participants where id=(select value from tap_values where key='participant')), '10000000-0000-0000-0000-000000000000'::uuid, 'participant organization is server-derived');
select is((select status from public.session_participants where id=(select value from tap_values where key='participant')), 'Approved', 'participant approval is server-derived');
select results_eq($$with changed as (update public.session_participants set status='Approved' where id=(select value from tap_values where key='participant') returning 1) select count(*)::bigint from changed$$, array[0::bigint], 'Student cannot directly set participant status');
select results_eq($$with changed as (update public.session_participants set extra_time_minutes=999,resubmit_allowed=true where id=(select value from tap_values where key='participant') returning 1) select count(*)::bigint from changed$$, array[0::bigint], 'Student cannot grant extra time or resubmit');
select throws_ok($$select public.join_public_session('23000000-0000-0000-0000-000000000001','device-one','test','1','{}')$$, 'P0002', 'PUBLIC_CLASS_SESSION_NOT_FOUND', 'cross-tenant class session join is rejected');
insert into tap_values values ('connection', public.upsert_public_device_heartbeat('13000000-0000-0000-0000-000000000000','device-one','Online','ExamTransfer','[]','1','1'));
select is(public.upsert_public_device_heartbeat('13000000-0000-0000-0000-000000000000','device-one','Online','ExamTransfer','[]','1','1'), (select value from tap_values where key='connection'), 'heartbeat upsert is idempotent');
select is((select violation_count from public.public_device_connections where id=(select value from tap_values where key='connection')), 0, 'Student cannot supply server-managed device counters');
select throws_ok($$update public.public_device_connections set policy_state='Applied',lock_state='Locked',violation_count=999 where id=(select value from tap_values where key='connection')$$, '42501', 'permission denied for table public_device_connections', 'Student cannot directly set device policy, lock, or count');
select ok(public.report_public_violation('13000000-0000-0000-0000-000000000000','device-one','FocusLost','{}') is not null, 'violation RPC creates server-owned evidence row');
select is((select source_mode from public.violations order by created_at desc limit 1), 'PublicCloud', 'violation authority is PublicCloud');
insert into tap_values values ('quiz_participant', public.join_public_session('13000000-0000-0000-0000-000000000001','device-quiz','test','1','{}'));
insert into tap_values values ('quiz_connection', public.upsert_public_device_heartbeat('13000000-0000-0000-0000-000000000001','device-quiz','Online','ExamTransfer','[]','1','1'));

reset role;
update public.public_device_connections
set policy_state='Applied', policy_lease_expires_at=now()+interval '2 hours'
where id=(select value from tap_values where key='quiz_connection');
insert into public.public_device_commands(command_id,organization_id,session_id,device_id,command_type,payload,created_at,expires_at,issued_by,signature)
values ('16000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','13000000-0000-0000-0000-000000000000','device-one','ShowWarning','{}',now(),now()+interval '5 minutes','10000000-0000-0000-0000-000000000001',repeat('a',64));
update public.exam_sessions set status='InProgress' where id in ('13000000-0000-0000-0000-000000000000','13000000-0000-0000-0000-000000000001');
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"10000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$insert into public.public_device_command_results(command_id,organization_id,device_id,status,received_at) values ('16000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000000','device-one','Received',now())$$, '42501', 'permission denied for table public_device_command_results', 'Student cannot directly insert command results');
select is(public.ack_public_device_command('16000000-0000-0000-0000-000000000001','device-one','Received',null,null), 'Received', 'command can enter Received state');
select is(public.ack_public_device_command('16000000-0000-0000-0000-000000000001','device-one','Executed',null,null), 'Executed', 'command can enter one final state');
select is(public.ack_public_device_command('16000000-0000-0000-0000-000000000001','device-one','Executed',null,null), 'Executed', 'final command result is idempotent');
select throws_ok($$select public.ack_public_device_command('16000000-0000-0000-0000-000000000001','device-one','Failed','ERR','forged')$$, '55000', 'COMMAND_RESULT_FINAL', 'final command result cannot transition again');
insert into tap_values values ('submission', public.init_public_submission('13000000-0000-0000-0000-000000000000','submission-key-0001','answer.zip',1024,repeat('a',64)));
select is(public.init_public_submission('13000000-0000-0000-0000-000000000000','submission-key-0001','answer.zip',1024,repeat('a',64)), (select value from tap_values where key='submission'), 'submission init is idempotent');
select results_eq($$with changed as (update public.submissions set status='Submitted',is_official=true,is_late=false,receipt_code='forged',receipt_signature='forged',server_received_at=now(),deadline_at=now()+interval '1 day' where id=(select value from tap_values where key='submission') returning 1) select count(*)::bigint from changed$$, array[0::bigint], 'Student cannot directly set submission status, receipt, deadline, late, or official fields');
select results_eq($$with changed as (update public.submission_files set archive_signature_verified=true where submission_id=(select value from tap_values where key='submission') returning 1) select count(*)::bigint from changed$$, array[0::bigint], 'Student cannot set archive verification');
select lives_ok($$
  insert into storage.objects(id,bucket_id,name,owner_id)
  select
    '17000000-0000-4000-8000-000000000001'::uuid,
    'public-submission-archives',
    f.cloud_object_path,
    '10000000-0000-0000-0000-000000000002'
  from public.submission_files f
  where f.submission_id=(select value from tap_values where key='submission')
$$, 'Student can upload the exact initialized PublicCloud archive path');
select throws_ok($$select public.finalize_public_submission((select value from tap_values where key='submission'),'submission-key-0001')$$, '55000', 'ARCHIVE_NOT_VERIFIED_BY_BACKEND', 'unverified archive cannot be finalized');
insert into tap_values values ('quiz_attempt', public.start_public_quiz_attempt('13000000-0000-0000-0000-000000000001','quiz-start-0001'));
select is(public.start_public_quiz_attempt('13000000-0000-0000-0000-000000000001','quiz-start-0001'), (select value from tap_values where key='quiz_attempt'), 'quiz start is idempotent');
select is(public.save_public_quiz_answers((select value from tap_values where key='quiz_attempt'),'14000000-0000-0000-0000-000000000001','["15000000-0000-4000-8000-000000000001"]',1,now()), 1::bigint, 'quiz answer revision is accepted');
select is(public.save_public_quiz_answers((select value from tap_values where key='quiz_attempt'),'14000000-0000-0000-0000-000000000001','["15000000-0000-4000-8000-000000000002"]',1,now()), 1::bigint, 'stale quiz revision is idempotently ignored');
select is((public.finalize_public_quiz_attempt((select value from tap_values where key='quiz_attempt'),'quiz-final-0001')->>'scoreVisible')::boolean, false, 'hidden quiz score is masked after server grading');
select is(public.finalize_public_quiz_attempt((select value from tap_values where key='quiz_attempt'),'quiz-final-0001')->>'score', null::text, 'quiz finalize is idempotent without leaking hidden score');
select results_eq($$with changed as (update public.quiz_attempts set score=999,status='Finalized' where id=(select value from tap_values where key='quiz_attempt') returning 1) select count(*)::bigint from changed$$, array[0::bigint], 'Student cannot directly set quiz status or score');

reset role;
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"10000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is(public.current_examtransfer_role(), 'Teacher', 'Teacher JWT context retains staff access');
select is((
  select score
  from public.get_teacher_quiz_attempts('13000000-0000-0000-0000-000000000001')
  where id=(select value from tap_values where key='quiz_attempt')),
  10::numeric,
  'teacher retains access to server score through the staff-only projection');

select * from finish();
rollback;
