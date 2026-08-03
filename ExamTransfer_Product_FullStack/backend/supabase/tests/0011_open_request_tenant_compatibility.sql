begin;
select plan(59);

insert into auth.users(id,email) values
  ('81000000-0000-0000-0000-000000000001','final-teacher@example.test'),
  ('81000000-0000-0000-0000-000000000002','final-student@example.test'),
  ('81000000-0000-0000-0000-000000000003','final-other@example.test'),
  ('82000000-0000-0000-0000-000000000001','final-other-teacher@example.test'),
  ('82000000-0000-0000-0000-000000000002','final-cross-tenant@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name) values
  ('81000000-0000-0000-0000-000000000000','Final Gate Org'),
  ('82000000-0000-0000-0000-000000000000','Final Gate Other Org');

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('81000000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','Final Teacher','Teacher','final-teacher',null,true,null),
  ('81000000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','Final Student','Student','FINAL01','FINAL01',true,'2008-01-01'),
  ('81000000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','Final Other Student','Student','FINAL02','FINAL02',true,'2008-01-01'),
  ('82000000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','Other Teacher','Teacher','other-teacher',null,true,null),
  ('82000000-0000-0000-0000-000000000002','82000000-0000-0000-0000-000000000000','Cross Tenant Student','Student','CROSS21','CROSS21',true,'2008-01-01');

insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values
  ('81100000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','Final Class A','FINAL-A','2026','Active','Public','81000000-0000-0000-0000-000000000001',now(),now()),
  ('81100000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','Final Class B','FINAL-B','2026','Active','Public','81000000-0000-0000-0000-000000000001',now(),now()),
  ('82100000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','Other Tenant Class','OTHER-A','2026','Active','Public','82000000-0000-0000-0000-000000000001',now(),now());

insert into public.class_members(
  id,organization_id,class_id,user_id,student_code,display_name,created_at,updated_at)
values (
  '81100000-0000-0000-0000-000000000010',
  '81000000-0000-0000-0000-000000000000',
  '81100000-0000-0000-0000-000000000001',
  '81000000-0000-0000-0000-000000000002',
  'FINAL01','Final Student',now(),now());

insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at)
values
  ('81200000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000',null,
   'Final Open File Exam','IT',60,'Published',1,'81000000-0000-0000-0000-000000000001',
   'FileSubmission','Standard','Hidden',now(),now()),
  ('81200000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81100000-0000-0000-0000-000000000001',
   'Final Legacy File Exam','IT',60,'Published',1,'81000000-0000-0000-0000-000000000001',
   'FileSubmission','Standard','Hidden',now(),now()),
  ('81200000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000',null,
   'Final Open Quiz Exam','Math',45,'Published',1,'81000000-0000-0000-0000-000000000001',
   'MultipleChoice','Standard','Hidden',now(),now()),
  ('82200000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000',null,
   'Other Tenant Exam','IT',60,'Published',1,'82000000-0000-0000-0000-000000000001',
   'FileSubmission','Standard','Hidden',now(),now());

insert into public.public_class_assignments(
  id,organization_id,class_id,exam_id,created_at,updated_at)
values (
  '81100000-0000-0000-0000-000000000020',
  '81000000-0000-0000-0000-000000000000',
  '81100000-0000-0000-0000-000000000001',
  '81200000-0000-0000-0000-000000000002',
  now(),now());

insert into public.exam_files(
  id,organization_id,exam_id,version,name,stored_name,mime_type,size_bytes,sha256,
  transfer_status,sync_status,cloud_object_path,created_at,updated_at)
values
  ('81500000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81200000-0000-0000-0000-000000000001',
   1,'open-exam.pdf','open-exam.pdf','application/pdf',1024,repeat('a',64),
   'Completed','Synced','org/final/open-exam.pdf',now(),now()),
  ('81500000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81200000-0000-0000-0000-000000000002',
   1,'class-exam.pdf','class-exam.pdf','application/pdf',1024,repeat('b',64),
   'Completed','Synced','org/final/class-exam.pdf',now(),now());

insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple)
values (
  '81700000-0000-0000-0000-000000000001',
  '81000000-0000-0000-0000-000000000000',
  '81200000-0000-0000-0000-000000000003',1,1,'One plus one?',1,false);
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct)
values
  ('81800000-0000-4000-8000-000000000001','81000000-0000-0000-0000-000000000000','81700000-0000-0000-0000-000000000001',1,'2',true),
  ('81800000-0000-4000-8000-000000000002','81000000-0000-0000-0000-000000000000','81700000-0000-0000-0000-000000000001',2,'3',false);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),26,
  'final OpenRequest tenant compatibility remains available at schema 26');
select ok((select prosecdef from pg_proc
  where oid='private.enforce_public_tenant_consistency()'::regprocedure)
  and (select proconfig::text from pg_proc
    where oid='private.enforce_public_tenant_consistency()'::regprocedure)
      = '{"search_path=\"\""}',
  'tenant trigger function remains security definer with empty search path');
select is((select count(*) from pg_trigger
  where not tgisinternal
    and tgname='trg_public_tenant_consistency'
    and tgfoid='private.enforce_public_tenant_consistency()'::regprocedure),
  11::bigint,
  'exactly eleven tenant consistency triggers remain attached');
select is((
  select array_agg(c.relname::text order by c.relname::text)
  from pg_trigger t
  join pg_class c on c.oid=t.tgrelid
  join pg_namespace n on n.oid=c.relnamespace
  where not t.tgisinternal
    and t.tgname='trg_public_tenant_consistency'
    and t.tgfoid='private.enforce_public_tenant_consistency()'::regprocedure
    and n.nspname='public'),
  array[
    'exam_sessions'::text,
    'public_class_assignments'::text,
    'public_device_command_results'::text,
    'public_device_commands'::text,
    'public_device_connections'::text,
    'quiz_answers'::text,
    'quiz_attempts'::text,
    'session_participants'::text,
    'submission_files'::text,
    'submissions'::text,
    'violations'::text],
  'tenant trigger remains attached to the exact eleven protected tables');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000001',null,'FINAL21','Waiting',null,'PublicCloud',
    false,true,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now())
$$,'PublicCloud OpenRequest accepts a same-tenant classless exam');
select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002',null,'OLDCLS21','Waiting',null,'PublicCloud',
    true,true,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now())
$$,'OpenRequest may use an older same-tenant classed exam while session remains classless');
select throws_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
    'BADCLS21','Waiting','PublicCloud',true,true,36,'FileSubmission','Standard','Hidden',1,
    'OpenRequest',now(),now())
$$,'23514','SESSION_OPENREQUEST_CLASS_FORBIDDEN',
  'OpenRequest rejects a non-null session class');
select throws_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000004','81000000-0000-0000-0000-000000000000',
    '82200000-0000-0000-0000-000000000001',null,'BADTEN21','Waiting','PublicCloud',
    true,true,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now())
$$,'23514','SESSION_TENANT_MISMATCH',
  'OpenRequest rejects an exam from another tenant');
select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000005','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000001',
    'CLASS21','Waiting',null,'PublicCloud',true,true,36,'FileSubmission','Standard','Hidden',1,
    'ClassMembersOnly',now(),now())
$$,'ClassMembersOnly retains a valid same-tenant exam and class');
select throws_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000006','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002',null,'NOCLASS21','Waiting','PublicCloud',
    true,true,36,'FileSubmission','Standard','Hidden',1,'ClassMembersOnly',now(),now())
$$,'23514','SESSION_CLASS_REQUIRED',
  'ClassMembersOnly still requires a session class');
select throws_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000007','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002','82100000-0000-0000-0000-000000000001',
    'XCLASS21','Waiting','PublicCloud',true,true,36,'FileSubmission','Standard','Hidden',1,
    'ClassMembersOnly',now(),now())
$$,'23514','SESSION_TENANT_MISMATCH',
  'ClassMembersOnly rejects a class from another tenant');
select throws_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000008','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000002',
    'MISMATCH','Waiting','PublicCloud',true,true,36,'FileSubmission','Standard','Hidden',1,
    'ClassMembersOnly',now(),now())
$$,'23514','SESSION_TENANT_MISMATCH',
  'ClassMembersOnly rejects an exam and class mismatch');
select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '81300000-0000-0000-0000-000000000009','81000000-0000-0000-0000-000000000000',
    '81200000-0000-0000-0000-000000000001',null,'LANONLY','Draft','LanOnly',
    true,false,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now())
$$,'LanOnly classless session is unaffected by PublicCloud trigger rules');
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values (
  '81300000-0000-0000-0000-000000000010','81000000-0000-0000-0000-000000000000',
  '81200000-0000-0000-0000-000000000001',null,'DRAFT21','Draft','PublicCloud',
  true,false,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now());
select lives_ok($$
  update public.exam_sessions
  set status='Waiting',accepting_participants=true,updated_at=now()
  where id='81300000-0000-0000-0000-000000000010'
$$,'OpenRequest Draft to Waiting update remains valid');
select throws_ok($$
  update public.exam_sessions
  set class_id='81100000-0000-0000-0000-000000000001'
  where id='81300000-0000-0000-0000-000000000002'
$$,'23514','SESSION_OPENREQUEST_CLASS_FORBIDDEN',
  'OpenRequest update cannot attach a class');
select throws_ok($$
  update public.exam_sessions
  set exam_id='82200000-0000-0000-0000-000000000001'
  where id='81300000-0000-0000-0000-000000000001'
$$,'23514','SESSION_TENANT_MISMATCH',
  'OpenRequest update cannot attach another tenant exam');

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values (
  '81300000-0000-0000-0000-000000000011','81000000-0000-0000-0000-000000000000',
  '81200000-0000-0000-0000-000000000003',null,'QUIZ21','InProgress',now(),'PublicCloud',
  true,false,36,'MultipleChoice','Standard','Hidden',1,'OpenRequest',now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,approved_at,download_status,submission_status,extra_time_minutes,
  resubmit_allowed,source_mode,created_at,updated_at)
values (
  '81400000-0000-0000-0000-000000000011','81000000-0000-0000-0000-000000000000',
  '81300000-0000-0000-0000-000000000011','81000000-0000-0000-0000-000000000002',
  'FINAL01','Final Student','quiz-device','Approved',now(),now(),'Completed','NotStarted',
  0,false,'PublicCloud',now(),now());

create temporary table final_gate_values(
  key text primary key,
  value jsonb,
  id uuid)
on commit drop;
grant select,insert,update on final_gate_values to authenticated,service_role;

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into final_gate_values(key,value) values (
  'open-join',
  public.join_open_public_session_by_room_code(
    'FINAL21','open-device','student-machine','1.0.0','{}'));
select is((select value->>'sessionId' from final_gate_values where key='open-join'),
  '81300000-0000-0000-0000-000000000001',
  'OpenRequest room-code RPC joins the exact same-tenant session');
select is((select value->>'participantStatus' from final_gate_values where key='open-join'),
  'PendingApproval',
  'OpenRequest room-code join preserves teacher approval');
select is(public.get_public_student_timeline(
  '81300000-0000-0000-0000-000000000001')->>'admissionMode',
  'OpenRequest',
  'Pending OpenRequest participant can read the authoritative timeline');

reset role;
insert into public.public_device_connections(
  id,organization_id,session_id,participant_id,user_id,device_id,connection_state,
  heartbeat_at,source_mode,cloud_version,created_at,updated_at)
values (
  '81500000-0000-0000-0000-000000000010','81000000-0000-0000-0000-000000000000',
  '81300000-0000-0000-0000-000000000001',
  (select (value->>'participantId')::uuid from final_gate_values where key='open-join'),
  '81000000-0000-0000-0000-000000000002','open-device','Online',now(),
  'PublicCloud',1,now(),now());
select ok(exists(select 1 from public.public_device_connections
  where id='81500000-0000-0000-0000-000000000010'),
  'OpenRequest participant creates a tenant-consistent device connection');

set local role service_role;
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select is(public.issue_public_device_command(
  '81600000-0000-0000-0000-000000000001',
  '81300000-0000-0000-0000-000000000001','open-device','ShowWarning','{}',
  now(),now()+interval '5 minutes','81000000-0000-0000-0000-000000000001',
  repeat('a',64)),
  '81600000-0000-0000-0000-000000000001'::uuid,
  'service path issues a command to the OpenRequest connection');

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '81300000-0000-0000-0000-000000000001','open-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'Pending OpenRequest participant cannot report a violation');
select throws_ok($$select public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000001','open-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'Pending OpenRequest participant cannot acknowledge a command');
select throws_ok($$select public.get_public_exam_manifest(
  '81300000-0000-0000-0000-000000000001')$$,
  '42501','PUBLIC_EXAM_MANIFEST_FORBIDDEN',
  'Pending OpenRequest participant cannot receive the exam manifest');

select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',
  true);
select is(public.approve_public_participant(
  '81300000-0000-0000-0000-000000000001',
  (select (value->>'participantId')::uuid from final_gate_values where key='open-join'),
  '81900000-0000-0000-0000-000000000001')->>'status',
  'Approved',
  'owning teacher approves the OpenRequest participant');

reset role;
update public.exam_sessions
set status='InProgress',started_at=now(),accepting_participants=false,updated_at=now()
where id='81300000-0000-0000-0000-000000000001';

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select is(public.upsert_public_device_heartbeat(
  '81300000-0000-0000-0000-000000000001','open-device','Online',
  'ExamTransfer','[]','1.0.0','1.0.0'),
  '81500000-0000-0000-0000-000000000010'::uuid,
  'Approved OpenRequest participant updates the matched heartbeat');
insert into final_gate_values(key,id) values (
  'open-violation',
  public.report_public_violation(
    '81300000-0000-0000-0000-000000000001','open-device','FocusLost','{}'));
select ok((select id from final_gate_values where key='open-violation') is not null,
  'Approved OpenRequest participant reports a classless violation');
select is(public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000001','open-device','Received',null,null),
  'Received',
  'Approved OpenRequest participant acknowledges command receipt');
select is(public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000001','open-device','Executed',null,null),
  'Executed',
  'OpenRequest command preserves Received to Executed transition');
select is(jsonb_array_length(public.get_public_exam_manifest(
  '81300000-0000-0000-0000-000000000001')),
  1,
  'Approved OpenRequest participant receives the manifest after session start');
select results_eq($$select file_name from public.get_public_exam_file_download(
  '81300000-0000-0000-0000-000000000001',
  '81500000-0000-0000-0000-000000000001')$$,
  array['open-exam.pdf'::text],
  'Approved OpenRequest participant resolves the guarded file download');
insert into final_gate_values(key,id) values (
  'open-submission',
  public.init_public_submission(
    '81300000-0000-0000-0000-000000000001',
    'final-open-submission-0001','answer.zip',1024,repeat('c',64)));
select ok((select id from final_gate_values where key='open-submission') is not null
  and exists(select 1 from public.submission_files
    where submission_id=(select id from final_gate_values where key='open-submission')
      and source_mode='PublicCloud'),
  'OpenRequest submission and archive row pass tenant trigger branches');

insert into final_gate_values(key,id) values (
  'quiz-connection',
  public.upsert_public_device_heartbeat(
    '81300000-0000-0000-0000-000000000011','quiz-device','Online',
    'ExamTransfer','[]','1.0.0','1.0.0'));
reset role;
update public.public_device_connections
set policy_state='Applied',policy_lease_expires_at=now()+interval '2 hours'
where id=(select id from final_gate_values where key='quiz-connection');
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into final_gate_values(key,id) values (
  'quiz-attempt',
  public.start_public_quiz_attempt(
    '81300000-0000-0000-0000-000000000011','final-quiz-start-0001'));
select ok((select id from final_gate_values where key='quiz-attempt') is not null,
  'OpenRequest quiz attempt passes participant/session tenant trigger');
select is(public.save_public_quiz_answers(
  (select id from final_gate_values where key='quiz-attempt'),
  '81700000-0000-0000-0000-000000000001',
  '["81800000-0000-4000-8000-000000000001"]',1,now()),
  1::bigint,
  'OpenRequest quiz answer passes attempt/question tenant trigger');
select is((public.finalize_public_quiz_attempt(
  (select id from final_gate_values where key='quiz-attempt'),
  'final-quiz-finish-0001')->>'scoreVisible')::boolean,
  false,
  'OpenRequest quiz finalize preserves hidden-score policy');
select ok(position('isCorrect' in public.get_public_quiz_attempt(
  (select id from final_gate_values where key='quiz-attempt'))::text)=0,
  'OpenRequest quiz projection does not expose correct answers');

reset role;
select throws_ok($$update public.public_class_assignments
  set organization_id='82000000-0000-0000-0000-000000000000'
  where id='81100000-0000-0000-0000-000000000020'$$,
  '23514','ASSIGNMENT_TENANT_MISMATCH',
  'assignment trigger still rejects a cross-tenant update');
select throws_ok($$update public.session_participants
  set organization_id='82000000-0000-0000-0000-000000000000'
  where id=(select (value->>'participantId')::uuid from final_gate_values where key='open-join')$$,
  '23514','PARTICIPANT_TENANT_MISMATCH',
  'participant trigger still rejects a cross-tenant update');
select throws_ok($$update public.public_device_connections
  set user_id='81000000-0000-0000-0000-000000000003'
  where id='81500000-0000-0000-0000-000000000010'$$,
  '23514','DEVICE_TENANT_MISMATCH',
  'device trigger still rejects a mismatched participant user');
select throws_ok($$update public.public_device_commands
  set device_id='wrong-device'
  where command_id='81600000-0000-0000-0000-000000000001'$$,
  '23514','COMMAND_DEVICE_MISMATCH',
  'command trigger still rejects a device without a matching connection');
select throws_ok($$update public.public_device_command_results
  set device_id='wrong-device'
  where command_id='81600000-0000-0000-0000-000000000001'$$,
  '23514','COMMAND_RESULT_MISMATCH',
  'command result trigger still rejects a mismatched device');
select throws_ok($$update public.violations
  set class_id='81100000-0000-0000-0000-000000000001'
  where id=(select id from final_gate_values where key='open-violation')$$,
  '23514','VIOLATION_TENANT_MISMATCH',
  'violation trigger keeps null-safe class matching for OpenRequest');
select throws_ok($$update public.submissions
  set organization_id='82000000-0000-0000-0000-000000000000'
  where id=(select id from final_gate_values where key='open-submission')$$,
  '23514','SUBMISSION_TENANT_MISMATCH',
  'submission trigger still rejects a cross-tenant update');
select throws_ok($$update public.submission_files
  set organization_id='82000000-0000-0000-0000-000000000000'
  where submission_id=(select id from final_gate_values where key='open-submission')$$,
  '23514','SUBMISSION_FILE_TENANT_MISMATCH',
  'submission file trigger still rejects a cross-tenant update');
select throws_ok($$update public.quiz_attempts
  set organization_id='82000000-0000-0000-0000-000000000000'
  where id=(select id from final_gate_values where key='quiz-attempt')$$,
  '23514','QUIZ_ATTEMPT_TENANT_MISMATCH',
  'quiz attempt trigger still rejects a cross-tenant update');
select throws_ok($$update public.quiz_answers
  set organization_id='82000000-0000-0000-0000-000000000000'
  where attempt_id=(select id from final_gate_values where key='quiz-attempt')$$,
  '23514','QUIZ_ANSWER_TENANT_MISMATCH',
  'quiz answer trigger still rejects a cross-tenant update');

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000003","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '81300000-0000-0000-0000-000000000001','open-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'different same-tenant user cannot report the OpenRequest violation');
select throws_ok($$select public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000001','open-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'different same-tenant user cannot acknowledge the OpenRequest command');
select set_config(
  'request.jwt.claims',
  '{"sub":"82000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '81300000-0000-0000-0000-000000000001','open-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'cross-tenant user cannot discover the OpenRequest violation path');
select throws_ok($$select public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000001','open-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'cross-tenant user cannot discover the OpenRequest command path');

select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into final_gate_values(key,id) values (
  'class-participant',
  public.join_public_session(
    '81300000-0000-0000-0000-000000000005',
    'class-device','student-machine','1.0.0','{}'));
select ok((select id from final_gate_values where key='class-participant') is not null,
  'ClassMembersOnly join still requires and accepts valid membership and assignment');
insert into final_gate_values(key,id) values (
  'class-connection',
  public.upsert_public_device_heartbeat(
    '81300000-0000-0000-0000-000000000005','class-device','Online',
    'ExamTransfer','[]','1.0.0','1.0.0'));
select ok((select id from final_gate_values where key='class-connection') is not null,
  'ClassMembersOnly heartbeat remains valid');
insert into final_gate_values(key,id) values (
  'class-violation',
  public.report_public_violation(
    '81300000-0000-0000-0000-000000000005','class-device','FocusLost','{}'));
select is((select class_id from public.violations
  where id=(select id from final_gate_values where key='class-violation')),
  '81100000-0000-0000-0000-000000000001'::uuid,
  'ClassMembersOnly violation retains the legacy class id');

reset role;
set local role service_role;
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select is(public.issue_public_device_command(
  '81600000-0000-0000-0000-000000000002',
  '81300000-0000-0000-0000-000000000005','class-device','ShowWarning','{}',
  now(),now()+interval '5 minutes','81000000-0000-0000-0000-000000000001',
  repeat('b',64)),
  '81600000-0000-0000-0000-000000000002'::uuid,
  'service path still issues a ClassMembersOnly command');
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select is(public.ack_public_device_command(
  '81600000-0000-0000-0000-000000000002','class-device','Received',null,null),
  'Received',
  'ClassMembersOnly command acknowledgement remains valid');

reset role;
update public.exam_sessions
set status='InProgress',started_at=now(),accepting_participants=false,updated_at=now()
where id='81300000-0000-0000-0000-000000000005';
set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select is(jsonb_array_length(public.get_public_exam_manifest(
  '81300000-0000-0000-0000-000000000005')),
  1,
  'ClassMembersOnly manifest and assignment gate remain valid');
select results_eq($$select file_name from public.get_public_exam_file_download(
  '81300000-0000-0000-0000-000000000005',
  '81500000-0000-0000-0000-000000000002')$$,
  array['class-exam.pdf'::text],
  'ClassMembersOnly guarded download remains valid');
select ok(public.init_public_submission(
  '81300000-0000-0000-0000-000000000005',
  'final-class-submission-0001','class-answer.zip',1024,repeat('d',64)) is not null,
  'ClassMembersOnly submission remains valid');

reset role;
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select ok(public.get_examtransfer_cloud_capabilities()->'criticalRpcs'
  ?& array[
    'join_open_public_session_by_room_code',
    'report_public_violation',
    'ack_public_device_command'],
  'schema 21 capability retains all OpenRequest critical RPCs');

select * from finish();
rollback;
