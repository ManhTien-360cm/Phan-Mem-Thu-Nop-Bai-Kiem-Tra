begin;
select plan(39);

select is((select schema_version from public.examtransfer_cloud_meta where id=1), 27,
  'session-first workflow remains compatible at schema version 27');
select has_column('public','exam_sessions','admission_mode',
  'session admission mode exists');
select ok(exists(
  select 1 from pg_constraint
  where conrelid='public.exam_sessions'::regclass
    and conname='exam_sessions_admission_mode_check'),
  'session admission mode has a database check constraint');
select ok((select convalidated from pg_constraint
  where conrelid='public.exam_sessions'::regclass
    and conname='exam_sessions_admission_mode_check'),
  'session admission mode constraint validates migrated rows');
select has_function('public','join_open_public_session_by_room_code',
  array['text','text','text','text','jsonb'],
  'room-code OpenRequest join RPC exists');
select has_function('public','get_public_exam_manifest',array['uuid'],
  'guarded exam manifest RPC exists');
select ok(not has_function_privilege(
  'anon',
  'public.join_open_public_session_by_room_code(text,text,text,text,jsonb)',
  'EXECUTE'),
  'anon cannot join an OpenRequest session');
select ok(has_function_privilege(
  'authenticated',
  'public.join_open_public_session_by_room_code(text,text,text,text,jsonb)',
  'EXECUTE'),
  'authenticated students can execute guarded OpenRequest join');
select ok((select prosecdef from pg_proc
  where oid='public.join_open_public_session_by_room_code(text,text,text,text,jsonb)'::regprocedure),
  'OpenRequest join is security definer');
select is((select proconfig::text from pg_proc
  where oid='public.join_open_public_session_by_room_code(text,text,text,text,jsonb)'::regprocedure),
  '{"search_path=\"\""}',
  'OpenRequest join has an empty search path');
select ok(position('ClassMembersOnly' in pg_get_functiondef(
  'public.join_public_session(uuid,text,text,text,jsonb)'::regprocedure)) > 0,
  'legacy class join remains an executable ClassMembersOnly branch');
select ok(position('OpenRequest' in pg_get_functiondef(
  'public.get_public_student_timeline(uuid)'::regprocedure)) > 0,
  'student timeline explicitly branches for OpenRequest');
select ok(position('OpenRequest' in pg_get_functiondef(
  'public.init_public_submission(uuid,text,text,bigint,text)'::regprocedure)) > 0,
  'file submission explicitly branches for OpenRequest');
select ok(position('ClassMembersOnly' in pg_get_functiondef(
  'public.start_public_quiz_attempt(uuid,text)'::regprocedure)) > 0,
  'quiz start limits membership checks to ClassMembersOnly');

insert into auth.users(id,email) values
  ('61000000-0000-0000-0000-000000000001','open-teacher@example.test'),
  ('61000000-0000-0000-0000-000000000002','open-student@example.test'),
  ('61000000-0000-0000-0000-000000000003','open-capacity@example.test'),
  ('62000000-0000-0000-0000-000000000002','open-other@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('61000000-0000-0000-0000-000000000000','Open Org'),
  ('62000000-0000-0000-0000-000000000000','Other Open Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('61000000-0000-0000-0000-000000000001','61000000-0000-0000-0000-000000000000','Open Teacher','Teacher','open-teacher',null,true,null),
  ('61000000-0000-0000-0000-000000000002','61000000-0000-0000-0000-000000000000','Open Student','Student','OPEN01','OPEN01',true,'2008-01-01'),
  ('61000000-0000-0000-0000-000000000003','61000000-0000-0000-0000-000000000000','Capacity Student','Student','OPEN02','OPEN02',true,'2008-01-01'),
  ('62000000-0000-0000-0000-000000000002','62000000-0000-0000-0000-000000000000','Other Student','Student','OTHER01','OTHER01',true,'2008-01-01');

insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '61100000-0000-0000-0000-000000000000',
  '61000000-0000-0000-0000-000000000000',
  'Legacy Class','LEGACY','2026','Active','Public',
  '61000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at)
values
  ('61200000-0000-0000-0000-000000000001','61000000-0000-0000-0000-000000000000',null,
   'Open File Exam','IT',60,'Published',1,'61000000-0000-0000-0000-000000000001',
   'FileSubmission','None','Hidden',now(),now()),
  ('61200000-0000-0000-0000-000000000002','61000000-0000-0000-0000-000000000000',null,
   'Open Quiz Exam','Math',45,'Published',1,'61000000-0000-0000-0000-000000000001',
   'MultipleChoice','Standard','Hidden',now(),now()),
  ('61200000-0000-0000-0000-000000000003','61000000-0000-0000-0000-000000000000','61100000-0000-0000-0000-000000000000',
   'Legacy Class Exam','IT',60,'Published',1,'61000000-0000-0000-0000-000000000001',
   'FileSubmission','None','Hidden',now(),now());
insert into public.public_class_assignments(organization_id,class_id,exam_id)
values (
  '61000000-0000-0000-0000-000000000000',
  '61100000-0000-0000-0000-000000000000',
  '61200000-0000-0000-0000-000000000003');

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values
  ('61300000-0000-0000-0000-000000000001','61000000-0000-0000-0000-000000000000','61200000-0000-0000-0000-000000000001',null,
   'OPEN19','Waiting',null,'PublicCloud',false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now()),
  ('61300000-0000-0000-0000-000000000002','61000000-0000-0000-0000-000000000000','61200000-0000-0000-0000-000000000001',null,
   'DRAFT19','Draft',null,'PublicCloud',false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now()),
  ('61300000-0000-0000-0000-000000000003','61000000-0000-0000-0000-000000000000','61200000-0000-0000-0000-000000000001',null,
   'FULL19','Waiting',null,'PublicCloud',false,true,1,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now()),
  ('61300000-0000-0000-0000-000000000004','61000000-0000-0000-0000-000000000000','61200000-0000-0000-0000-000000000002',null,
   'QUIZ19','InProgress',now(),'PublicCloud',true,false,36,'MultipleChoice','Standard','Hidden',1,'OpenRequest',now(),now()),
  ('61300000-0000-0000-0000-000000000005','61000000-0000-0000-0000-000000000000','61200000-0000-0000-0000-000000000003','61100000-0000-0000-0000-000000000000',
   'CLASS19','Waiting',null,'PublicCloud',true,true,36,'FileSubmission','None','Hidden',1,'ClassMembersOnly',now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values (
  '61400000-0000-0000-0000-000000000003',
  '61000000-0000-0000-0000-000000000000',
  '61300000-0000-0000-0000-000000000003',
  '61000000-0000-0000-0000-000000000003',
  'OPEN02','Capacity Student','capacity-device','PendingApproval',
  now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now());
insert into public.exam_files(
  id,organization_id,exam_id,version,name,stored_name,mime_type,size_bytes,sha256,
  transfer_status,sync_status,cloud_object_path,created_at,updated_at)
values (
  '61500000-0000-0000-0000-000000000001',
  '61000000-0000-0000-0000-000000000000',
  '61200000-0000-0000-0000-000000000001',
  1,'exam.pdf','exam.pdf','application/pdf',1024,repeat('a',64),
  'Completed','Synced','org/exams/exam.pdf',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values (
  '61600000-0000-0000-0000-000000000001',
  '61000000-0000-0000-0000-000000000000',
  '61200000-0000-0000-0000-000000000002',1,1,'Two plus two?',1,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('61700000-0000-4000-8000-000000000001','61000000-0000-0000-0000-000000000000','61600000-0000-0000-0000-000000000001',1,'4',true),
  ('61700000-0000-4000-8000-000000000002','61000000-0000-0000-0000-000000000000','61600000-0000-0000-0000-000000000001',2,'5',false);

create temporary table open19_values(
  key text primary key,
  value jsonb,
  id uuid)
on commit drop;
grant select,insert,update on open19_values to authenticated;

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into open19_values(key,value) values (
  'join',
  public.join_open_public_session_by_room_code(
    'open19','open-device','student-machine','1.0.0','{}'));
select is((select value->>'sessionId' from open19_values where key='join'),
  '61300000-0000-0000-0000-000000000001',
  'OpenRequest joins the exact same-tenant room code');
select is((select value->>'participantStatus' from open19_values where key='join'),
  'PendingApproval',
  'OpenRequest preserves teacher approval policy');
select is((
  public.join_open_public_session_by_room_code(
    'OPEN19','open-device','student-machine','1.0.0','{}')->>'participantId'),
  (select value->>'participantId' from open19_values where key='join'),
  'same user and device rejoin idempotently');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'MISSING19','open-device','student-machine','1.0.0','{}')$$,
  'P0002','OPEN_PUBLIC_SESSION_NOT_FOUND',
  'wrong room code returns a typed not-found error');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'DRAFT19','open-device','student-machine','1.0.0','{}')$$,
  'P0002','OPEN_PUBLIC_SESSION_NOT_FOUND',
  'Draft OpenRequest sessions do not accept joins');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'OPEN19','different-device','student-machine','1.0.0','{}')$$,
  '23505','PARTICIPANT_DEVICE_CONFLICT',
  'rejoin from a different device is rejected');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'FULL19','open-device','student-machine','1.0.0','{}')$$,
  '54000','SESSION_CAPACITY_REACHED',
  'OpenRequest capacity is enforced under the session lock');
select ok((select value->>'participantId' from open19_values where key='join') is not null,
  'OpenRequest join requires no class membership or assignment');
insert into open19_values(key,value) values (
  'timeline',
  public.get_public_student_timeline('61300000-0000-0000-0000-000000000001'));
select is((select value->>'admissionMode' from open19_values where key='timeline'),
  'OpenRequest',
  'OpenRequest participant can read the authoritative timeline');
select is((select value->>'resubmitAllowed' from open19_values where key='timeline'),
  'false',
  'student timeline returns false resubmit authority');
reset role;
update public.session_participants
set resubmit_allowed = true,
    cloud_version = private.next_public_cloud_version(),
    updated_at = pg_catalog.now()
where id = (select (value->>'participantId')::uuid from open19_values where key='join');
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
update open19_values
set value = public.get_public_student_timeline(
  '61300000-0000-0000-0000-000000000001')
where key = 'timeline';
select is((select value->>'resubmitAllowed' from open19_values where key='timeline'),
  'true',
  'student timeline returns true resubmit authority');
reset role;
update public.session_participants
set resubmit_allowed = false,
    cloud_version = private.next_public_cloud_version(),
    updated_at = pg_catalog.now()
where id = (select (value->>'participantId')::uuid from open19_values where key='join');
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.get_public_exam_manifest(
  '61300000-0000-0000-0000-000000000001')$$,
  '42501','PUBLIC_EXAM_MANIFEST_FORBIDDEN',
  'unapproved participant cannot receive an exam manifest');

select set_config(
  'request.jwt.claims',
  '{"sub":"62000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.get_public_student_timeline(
  '61300000-0000-0000-0000-000000000001')$$,
  'P0002','PUBLIC_STUDENT_TIMELINE_NOT_FOUND',
  'student timeline never exposes another participant authority');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'OPEN19','other-device','other-machine','1.0.0','{}')$$,
  'P0002','OPEN_PUBLIC_SESSION_NOT_FOUND',
  'cross-tenant room-code join is denied without disclosing the room');

select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.join_public_session(
  '61300000-0000-0000-0000-000000000005',
  'legacy-device','student-machine','1.0.0','{}')$$,
  '42501','CLASS_MEMBERSHIP_REQUIRED',
  'legacy ClassMembersOnly branch still requires class membership');

reset role;
update public.session_participants
set status='Approved', approved_at=now()
where id=(select (value->>'participantId')::uuid from open19_values where key='join');
update public.exam_sessions
set status='InProgress', started_at=now(), accepting_participants=false
where id='61300000-0000-0000-0000-000000000001';
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,approved_at,download_status,submission_status,extra_time_minutes,
  resubmit_allowed,source_mode,created_at,updated_at)
values (
  '61400000-0000-0000-0000-000000000004',
  '61000000-0000-0000-0000-000000000000',
  '61300000-0000-0000-0000-000000000004',
  '61000000-0000-0000-0000-000000000002',
  'OPEN01','Open Student','quiz-device','Approved',now(),now(),
  'Completed','NotStarted',0,false,'PublicCloud',now(),now());

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into open19_values(key,id) values (
  'heartbeat',
  public.upsert_public_device_heartbeat(
    '61300000-0000-0000-0000-000000000001',
    'open-device','Online','ExamTransfer','[]','1.0.0','1.0.0'));
select ok((select id from open19_values where key='heartbeat') is not null,
  'OpenRequest approved participant can report heartbeat');
insert into open19_values(key,value) values (
  'manifest',
  public.get_public_exam_manifest('61300000-0000-0000-0000-000000000001'));
select is(jsonb_array_length((select value from open19_values where key='manifest')),1,
  'OpenRequest manifest is available only after session start');
select results_eq($$select file_name from public.get_public_exam_file_download(
  '61300000-0000-0000-0000-000000000001',
  '61500000-0000-0000-0000-000000000001')$$,
  array['exam.pdf'::text],
  'OpenRequest guarded file download resolves after start');
insert into open19_values(key,id) values (
  'submission',
  public.init_public_submission(
    '61300000-0000-0000-0000-000000000001',
    'open-submission-0001','answer.zip',1024,repeat('b',64)));
select ok((select id from open19_values where key='submission') is not null,
  'OpenRequest participant can initialize a file submission');
select throws_ok($$select public.finalize_public_submission(
  (select id from open19_values where key='submission'),
  'open-submission-0001')$$,
  '55000','ARCHIVE_NOT_VERIFIED_BY_BACKEND',
  'OpenRequest submission keeps backend archive verification');

insert into open19_values(key,id) values (
  'quiz-heartbeat',
  public.upsert_public_device_heartbeat(
    '61300000-0000-0000-0000-000000000004',
    'quiz-device','Online','ExamTransfer','[]','1.0.0','1.0.0'));
reset role;
update public.public_device_connections
set policy_state='Applied',policy_lease_expires_at=now()+interval '2 hours'
where id=(select id from open19_values where key='quiz-heartbeat');
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"61000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into open19_values(key,id) values (
  'attempt',
  public.start_public_quiz_attempt(
    '61300000-0000-0000-0000-000000000004','open-quiz-start-0001'));
select ok((select id from open19_values where key='attempt') is not null,
  'OpenRequest quiz starts without a class assignment');
select is(public.save_public_quiz_answers(
  (select id from open19_values where key='attempt'),
  '61600000-0000-0000-0000-000000000001',
  '["61700000-0000-4000-8000-000000000001"]',1,now()),1::bigint,
  'OpenRequest quiz answer save preserves revision semantics');
select is((public.finalize_public_quiz_attempt(
  (select id from open19_values where key='attempt'),
  'open-quiz-final-0001')->>'scoreVisible')::boolean,false,
  'OpenRequest hidden quiz score remains masked');
select ok(position('isCorrect' in public.get_public_quiz_attempt(
  (select id from open19_values where key='attempt'))::text)=0,
  'student quiz projection never exposes correct-answer markers');

reset role;
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select ok(position('join_open_public_session_by_room_code' in
  public.get_examtransfer_cloud_capabilities()::text)>0
  and position('get_public_exam_manifest' in
  public.get_examtransfer_cloud_capabilities()::text)>0,
  'schema 21 capability advertises both session-first RPCs');

select * from finish();
rollback;
