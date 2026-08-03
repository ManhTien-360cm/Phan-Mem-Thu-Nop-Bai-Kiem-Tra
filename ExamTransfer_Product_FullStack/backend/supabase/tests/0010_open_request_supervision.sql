begin;
select plan(24);

insert into auth.users(id,email) values
  ('71000000-0000-0000-0000-000000000001','supervision-teacher@example.test'),
  ('71000000-0000-0000-0000-000000000002','supervision-open@example.test'),
  ('71000000-0000-0000-0000-000000000003','supervision-pending@example.test'),
  ('71000000-0000-0000-0000-000000000004','supervision-other@example.test'),
  ('72000000-0000-0000-0000-000000000002','supervision-cross-tenant@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name) values
  ('71000000-0000-0000-0000-000000000000','Supervision Org'),
  ('72000000-0000-0000-0000-000000000000','Cross Tenant Org');

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','Supervision Teacher','Teacher','supervision-teacher',null,true,null),
  ('71000000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','Open Student','Student','SUP01','SUP01',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','Pending Student','Student','SUP02','SUP02',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000004','71000000-0000-0000-0000-000000000000','Other Student','Student','SUP03','SUP03',true,'2008-01-01'),
  ('72000000-0000-0000-0000-000000000002','72000000-0000-0000-0000-000000000000','Cross Tenant Student','Student','CROSS01','CROSS01',true,'2008-01-01');

insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '71100000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000000',
  'Supervision Legacy Class','SUP-CLASS','2026','Active','Public',
  '71000000-0000-0000-0000-000000000001',now(),now());

insert into public.class_members(
  id,organization_id,class_id,user_id,student_code,display_name,created_at,updated_at)
values (
  '71100000-0000-0000-0000-000000000001',
  '71000000-0000-0000-0000-000000000000',
  '71100000-0000-0000-0000-000000000000',
  '71000000-0000-0000-0000-000000000002',
  'SUP01','Open Student',now(),now());

insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at)
values
  ('71200000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000',null,
   'Open Supervision Exam','IT',60,'Published',1,'71000000-0000-0000-0000-000000000001',
   'FileSubmission','Standard','Hidden',now(),now()),
  ('71200000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71100000-0000-0000-0000-000000000000',
   'Class Supervision Exam','IT',60,'Published',1,'71000000-0000-0000-0000-000000000001',
   'FileSubmission','Standard','Hidden',now(),now());

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values
  ('71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000001',null,
   'SUP-OPEN','InProgress',now(),'PublicCloud',true,false,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now()),
  ('71300000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000001',null,
   'SUP-PENDING','Waiting',null,'PublicCloud',false,true,36,'FileSubmission','Standard','Hidden',1,'OpenRequest',now(),now()),
  ('71300000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000000',
   'SUP-CLASS','InProgress',now(),'PublicCloud',true,false,36,'FileSubmission','Standard','Hidden',1,'ClassMembersOnly',now(),now());

insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,approved_at,download_status,submission_status,extra_time_minutes,
  resubmit_allowed,source_mode,created_at,updated_at)
values
  ('71400000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000002',
   'SUP01','Open Student','open-device','Approved',now(),now(),'Completed','NotStarted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000003',
   'SUP02','Pending Student','pending-device','PendingApproval',now(),null,'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000002',
   'SUP01','Open Student','class-device','Approved',now(),now(),'Completed','NotStarted',0,false,'PublicCloud',now(),now());

insert into public.public_device_connections(
  id,organization_id,session_id,participant_id,user_id,device_id,connection_state,
  heartbeat_at,source_mode,cloud_version,created_at,updated_at)
values
  ('71500000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000002',
   'open-device','Online',now(),'PublicCloud',1,now(),now()),
  ('71500000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000002','71400000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000003',
   'pending-device','Online',now(),'PublicCloud',1,now(),now()),
  ('71500000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000003','71400000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000002',
   'class-device','Online',now(),'PublicCloud',1,now(),now());

insert into public.public_device_commands(
  command_id,organization_id,session_id,device_id,command_type,payload,
  created_at,expires_at,issued_by,signature,source_mode,cloud_version,updated_at)
values
  ('71600000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','open-device','ShowWarning','{}',
   now(),now()+interval '5 minutes','71000000-0000-0000-0000-000000000001',repeat('a',64),'PublicCloud',1,now()),
  ('71600000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','open-device','ShowWarning','{}',
   now()-interval '10 minutes',now()-interval '5 minutes','71000000-0000-0000-0000-000000000001',repeat('b',64),'PublicCloud',1,now()),
  ('71600000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000002','pending-device','ShowWarning','{}',
   now(),now()+interval '5 minutes','71000000-0000-0000-0000-000000000001',repeat('c',64),'PublicCloud',1,now()),
  ('71600000-0000-0000-0000-000000000004','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000003','class-device','ShowWarning','{}',
   now(),now()+interval '5 minutes','71000000-0000-0000-0000-000000000001',repeat('d',64),'PublicCloud',1,now());

create temporary table supervision_values(
  key text primary key,
  id uuid)
on commit drop;
grant select,insert on supervision_values to authenticated;

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into supervision_values(key,id) values (
  'open-violation',
  public.report_public_violation(
    '71300000-0000-0000-0000-000000000001',
    'open-device','FocusLost','{"window":"browser"}'));
select ok((select id from supervision_values where key='open-violation') is not null,
  'OpenRequest approved student can create a violation without a class');

reset role;
select ok(exists(
  select 1
  from public.violations v
  where v.id=(select id from supervision_values where key='open-violation')
    and v.class_id is null
    and v.participant_id='71400000-0000-0000-0000-000000000001'
    and v.session_id='71300000-0000-0000-0000-000000000001'
    and v.source_mode='PublicCloud'),
  'OpenRequest violation keeps null class and server-derived participant, session and source');
select is((
  select violation_count
  from public.public_device_connections
  where id='71500000-0000-0000-0000-000000000001'),
  1,
  'OpenRequest violation increments the matched connection counter');

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000003","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '71300000-0000-0000-0000-000000000002','pending-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'Pending OpenRequest participant cannot report a violation');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '71300000-0000-0000-0000-000000000001','wrong-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'wrong device cannot report a violation');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000004","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '71300000-0000-0000-0000-000000000001','open-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'different same-tenant user cannot report another participant violation');

select set_config(
  'request.jwt.claims',
  '{"sub":"72000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '71300000-0000-0000-0000-000000000001','open-device','FocusLost','{}')$$,
  'P0002','DEVICE_CONNECTION_NOT_FOUND',
  'cross-tenant user cannot discover or report another tenant violation');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.report_public_violation(
  '71300000-0000-0000-0000-000000000001','open-device','FocusLost',
  jsonb_build_object('blob',repeat('x',70000)))$$,
  '22023','VIOLATION_EVIDENCE_TOO_LARGE',
  'OpenRequest violation preserves the evidence size limit');
insert into supervision_values(key,id) values (
  'class-violation',
  public.report_public_violation(
    '71300000-0000-0000-0000-000000000003',
    'class-device','FocusLost','{}'));

reset role;
select ok(exists(
  select 1
  from public.violations
  where id=(select id from supervision_values where key='class-violation')
    and class_id='71100000-0000-0000-0000-000000000000'),
  'ClassMembersOnly member still creates a violation with the legacy class id');

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select is(public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Received',null,null),
  'Received',
  'OpenRequest student acknowledges command receipt without a class');
select is(public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Executed',null,null),
  'Executed',
  'OpenRequest command transitions from Received to Executed');
select is(public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Executed',null,null),
  'Executed',
  'OpenRequest final command result is idempotent');
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Failed','ERR','forged')$$,
  '55000','COMMAND_RESULT_FINAL',
  'OpenRequest final command result cannot transition again');
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','wrong-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'wrong device cannot acknowledge a command');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000004","role":"authenticated"}',
  true);
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'different same-tenant user cannot acknowledge another participant command');

select set_config(
  'request.jwt.claims',
  '{"sub":"72000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000001','open-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'cross-tenant user cannot discover or acknowledge another tenant command');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000003","role":"authenticated"}',
  true);
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000003','pending-device','Received',null,null)$$,
  'P0002','DEVICE_COMMAND_NOT_FOUND',
  'Pending OpenRequest participant cannot acknowledge a command');

select set_config(
  'request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000002','open-device','Received',null,null)$$,
  '55000','DEVICE_COMMAND_EXPIRED',
  'expired command preserves the existing typed error');
select is(public.ack_public_device_command(
  '71600000-0000-0000-0000-000000000004','class-device','Received',null,null),
  'Received',
  'ClassMembersOnly member still acknowledges a command');

reset role;
select ok(not has_function_privilege(
  'anon',
  'public.report_public_violation(uuid,text,text,jsonb)',
  'EXECUTE')
  and not has_function_privilege(
    'anon',
    'public.ack_public_device_command(uuid,text,text,text,text)',
    'EXECUTE'),
  'anon cannot execute either supervision RPC');
select ok(not exists(
  select 1
  from pg_catalog.pg_proc p
  cross join lateral pg_catalog.aclexplode(
    coalesce(p.proacl, pg_catalog.acldefault('f',p.proowner))) acl
  where p.oid in (
      'public.report_public_violation(uuid,text,text,jsonb)'::regprocedure,
      'public.ack_public_device_command(uuid,text,text,text,text)'::regprocedure)
    and acl.grantee = 0
    and acl.privilege_type = 'EXECUTE'),
  'PUBLIC has no execute grant on either supervision RPC');
select is((
  select schema_version
  from public.examtransfer_cloud_meta
  where id=1),
  26,
  'OpenRequest supervision remains compatible at cloud schema 26');
select set_config('request.jwt.claims','{"role":"service_role"}',true);
select ok(public.get_examtransfer_cloud_capabilities()->'criticalRpcs'
  ? 'report_public_violation',
  'capability advertises report_public_violation');
select ok(public.get_examtransfer_cloud_capabilities()->'criticalRpcs'
  ? 'ack_public_device_command',
  'capability retains ack_public_device_command');

select * from finish();
rollback;
