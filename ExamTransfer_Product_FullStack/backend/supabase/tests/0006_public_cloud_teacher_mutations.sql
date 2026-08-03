begin;
select plan(36);

select is((select schema_version from public.examtransfer_cloud_meta where id=1), 26,
  'teacher mutations remain compatible at schema 26');
select has_function('public','approve_public_participant',array['uuid','uuid','uuid'],'approve participant RPC exists');
select has_function('public','reject_public_participant',array['uuid','uuid','text','uuid'],'reject participant RPC exists');
select has_function('public','bulk_approve_public_participants',array['uuid','uuid[]','uuid'],'bulk approve RPC exists');
select has_function('public','add_public_participant_extra_time',array['uuid','uuid','integer','text','uuid'],'extra time RPC exists');
select has_function('public','allow_public_resubmission',array['uuid','text','uuid'],'resubmission RPC exists');
select has_function('public','reject_public_submission',array['uuid','text','uuid'],'reject submission RPC exists');
select has_function('public','approve_public_enrollment_request',array['uuid','uuid'],'approve enrollment RPC exists');
select has_function('public','reject_public_enrollment_request',array['uuid','text','uuid'],'reject enrollment RPC exists');
select ok(not has_function_privilege('anon','public.approve_public_participant(uuid,uuid,uuid)','EXECUTE'),
  'anon cannot execute teacher RPC');
select ok(has_function_privilege('authenticated','public.approve_public_participant(uuid,uuid,uuid)','EXECUTE'),
  'authenticated role can execute guarded teacher RPC');

insert into auth.users(id,email) values
  ('31000000-0000-0000-0000-000000000001','teacher-owner@example.test'),
  ('31000000-0000-0000-0000-000000000002','student-one@example.test'),
  ('31000000-0000-0000-0000-000000000003','teacher-unmanaged@example.test'),
  ('32000000-0000-0000-0000-000000000001','teacher-other@example.test'),
  ('32000000-0000-0000-0000-000000000002','student-other@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('31000000-0000-0000-0000-000000000000','Teacher RPC Org One'),
  ('32000000-0000-0000-0000-000000000000','Teacher RPC Org Two');
insert into public.profiles(id,organization_id,display_name,role,username,student_code,is_active,date_of_birth) values
  ('31000000-0000-0000-0000-000000000001','31000000-0000-0000-0000-000000000000','Owner','Teacher','rpc-owner',null,true,null),
  ('31000000-0000-0000-0000-000000000002','31000000-0000-0000-0000-000000000000','Student','Student','RPC01','RPC01',true,'2008-01-01'),
  ('31000000-0000-0000-0000-000000000003','31000000-0000-0000-0000-000000000000','Unmanaged','Teacher','rpc-unmanaged',null,true,null),
  ('32000000-0000-0000-0000-000000000001','32000000-0000-0000-0000-000000000000','Other Teacher','Teacher','rpc-other',null,true,null),
  ('32000000-0000-0000-0000-000000000002','32000000-0000-0000-0000-000000000000','Other Student','Student','RPC02','RPC02',true,'2008-01-01');
insert into public.classes(id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at) values
  ('31100000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000000','RPC Class One','RPC1','2026','Active','Public','31000000-0000-0000-0000-000000000001',now(),now()),
  ('32100000-0000-0000-0000-000000000000','32000000-0000-0000-0000-000000000000','RPC Class Two','RPC2','2026','Active','Public','32000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,delivery_type,created_at,updated_at) values
  ('31200000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000000','31100000-0000-0000-0000-000000000000','RPC Exam One','IT',60,'Published',1,'31000000-0000-0000-0000-000000000001','FileSubmission',now(),now()),
  ('32200000-0000-0000-0000-000000000000','32000000-0000-0000-0000-000000000000','32100000-0000-0000-0000-000000000000','RPC Exam Two','IT',60,'Published',1,'32000000-0000-0000-0000-000000000001','FileSubmission',now(),now());
insert into public.exam_sessions(id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,auto_approve,accepting_participants,created_at,updated_at) values
  ('31300000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000000','31200000-0000-0000-0000-000000000000','31100000-0000-0000-0000-000000000000','RPCONE','Waiting',now(),'PublicCloud',false,true,now(),now()),
  ('32300000-0000-0000-0000-000000000000','32000000-0000-0000-0000-000000000000','32200000-0000-0000-0000-000000000000','32100000-0000-0000-0000-000000000000','RPCTWO','Waiting',now(),'PublicCloud',false,true,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('31400000-0000-0000-0000-000000000001','31000000-0000-0000-0000-000000000000','31300000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000002','RPC01','Student One','rpc-device-1','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('31400000-0000-0000-0000-000000000002','31000000-0000-0000-0000-000000000000','31300000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000002','RPC-BULK','Bulk Student','rpc-device-2','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('31400000-0000-0000-0000-000000000003','31000000-0000-0000-0000-000000000000','31300000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000002','RPC-REJ','Reject Student','rpc-device-3','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('32400000-0000-0000-0000-000000000001','32000000-0000-0000-0000-000000000000','32300000-0000-0000-0000-000000000000','32000000-0000-0000-0000-000000000002','RPC02','Student Other','rpc-device-other','Approved',now(),'NotStarted','Submitted',0,false,'PublicCloud',now(),now());
insert into public.submissions(
  id,organization_id,session_id,participant_id,attempt_number,status,
  deadline_at,is_late,is_official,idempotency_key,source_mode,created_at,updated_at)
values
  ('31500000-0000-0000-0000-000000000001','31000000-0000-0000-0000-000000000000','31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',1,'Submitted',now()+interval '1 hour',false,true,'rpc-submission-one','PublicCloud',now(),now()),
  ('32500000-0000-0000-0000-000000000001','32000000-0000-0000-0000-000000000000','32300000-0000-0000-0000-000000000000','32400000-0000-0000-0000-000000000001',1,'Submitted',now()+interval '1 hour',false,true,'rpc-submission-two','PublicCloud',now(),now());
insert into public.class_enrollment_requests(
  id,organization_id,class_id,student_user_id,student_code,status,requested_at,created_at,updated_at)
values
  ('31600000-0000-0000-0000-000000000001','31000000-0000-0000-0000-000000000000','31100000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000002','RPC01','Pending',now(),now(),now()),
  ('31600000-0000-0000-0000-000000000002','31000000-0000-0000-0000-000000000000','31100000-0000-0000-0000-000000000000','31000000-0000-0000-0000-000000000003','RPC03','Pending',now(),now(),now());

create temporary table teacher_rpc_versions(key text primary key, value bigint) on commit drop;
insert into teacher_rpc_versions values
  ('approve_before',(select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')),
  ('extra_before',0),('allow_before',0),('submission_before',0),('reject_before',0),
  ('enrollment_approve_before',(select cloud_version from public.class_enrollment_requests where id='31600000-0000-0000-0000-000000000001')),
  ('enrollment_reject_before',(select cloud_version from public.class_enrollment_requests where id='31600000-0000-0000-0000-000000000002'));
grant select, update on teacher_rpc_versions to authenticated;

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"31000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is(public.approve_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',
  '41000000-0000-0000-0000-000000000001')->>'status','Approved','owning teacher approves participant');
select ok((select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')
  > (select value from teacher_rpc_versions where key='approve_before'),'approve increases cloud_version');
select is((public.approve_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',
  '41000000-0000-0000-0000-000000000001')->>'cloudVersion')::bigint,
  (select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001'),
  'approve is idempotent for the same request ID');

select set_config('request.jwt.claims','{"sub":"32000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.approve_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000002',
  '41000000-0000-0000-0000-000000000002')$$,'42501','PUBLIC_SESSION_FORBIDDEN',
  'teacher from another organization is blocked');
select set_config('request.jwt.claims','{"sub":"31000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.approve_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000002',
  '41000000-0000-0000-0000-000000000003')$$,'42501','TEACHER_ROLE_REQUIRED',
  'student cannot call teacher RPC');
select set_config('request.jwt.claims','{"sub":"31000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select throws_ok($$select public.approve_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000002',
  '41000000-0000-0000-0000-000000000004')$$,'42501','PUBLIC_SESSION_FORBIDDEN',
  'teacher who does not manage the exam is blocked');

select set_config('request.jwt.claims','{"sub":"31000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.bulk_approve_public_participants(
  '31300000-0000-0000-0000-000000000000',
  array['31400000-0000-0000-0000-000000000002'::uuid,'ffffffff-ffff-ffff-ffff-ffffffffffff'::uuid],
  '41000000-0000-0000-0000-000000000005')$$,'55000','BULK_PARTICIPANT_SCOPE_INVALID',
  'bulk approve rejects an invalid ID atomically');
select is((select status from public.session_participants where id='31400000-0000-0000-0000-000000000002'),
  'PendingApproval','bulk failure leaves valid participant pending');
select throws_ok($$select public.add_public_participant_extra_time(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',-1,'invalid',
  '41000000-0000-0000-0000-000000000006')$$,'22023','EXTRA_TIME_INPUT_INVALID',
  'negative extra time is blocked');
select throws_ok($$select public.add_public_participant_extra_time(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',481,'invalid',
  '41000000-0000-0000-0000-000000000007')$$,'22023','EXTRA_TIME_INPUT_INVALID',
  'excessive extra time is blocked');

reset role;
update public.exam_sessions set status='InProgress' where id='31300000-0000-0000-0000-000000000000';
update public.session_participants set submission_status='Submitted'
where id='31400000-0000-0000-0000-000000000001';
update teacher_rpc_versions set value=(select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')
where key in ('extra_before','allow_before');
update teacher_rpc_versions set value=(select cloud_version from public.submissions where id='31500000-0000-0000-0000-000000000001')
where key='submission_before';
update teacher_rpc_versions set value=(select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000003')
where key='reject_before';
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"31000000-0000-0000-0000-000000000001","role":"authenticated"}',true);

select is((public.add_public_participant_extra_time(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',15,'approved accommodation',
  '41000000-0000-0000-0000-000000000008')->>'extraTimeMinutes')::integer,15,'extra time is added server-side');
select ok((select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')
  > (select value from teacher_rpc_versions where key='extra_before'),'extra time increases cloud_version');
update teacher_rpc_versions
set value=(select cloud_version from public.session_participants
           where id='31400000-0000-0000-0000-000000000001')
where key='extra_before';
select is((public.add_public_participant_extra_time(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000001',15,'approved accommodation',
  '41000000-0000-0000-0000-000000000008')->>'extraTimeMinutes')::integer,15,
  'extra time retry with the same request ID does not add twice');
select is((select cloud_version from public.session_participants
           where id='31400000-0000-0000-0000-000000000001'),
          (select value from teacher_rpc_versions where key='extra_before'),
  'extra time retry with the same request ID does not increment cloud_version');
update teacher_rpc_versions set value=(select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')
where key='allow_before';
select is(public.allow_public_resubmission(
  '31400000-0000-0000-0000-000000000001','teacher approved retry',
  '41000000-0000-0000-0000-000000000009')->>'resubmitAllowed','true','resubmission is enabled');
select ok((select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000001')
  > (select value from teacher_rpc_versions where key='allow_before'),'resubmission increases cloud_version');
select is(public.reject_public_submission(
  '31500000-0000-0000-0000-000000000001','archive is unreadable',
  '41000000-0000-0000-0000-000000000010')->>'status','Rejected','submission is rejected');
select ok((select cloud_version from public.submissions where id='31500000-0000-0000-0000-000000000001')
  > (select value from teacher_rpc_versions where key='submission_before'),'submission rejection increases cloud_version');
select throws_ok($$select public.reject_public_submission(
  '32500000-0000-0000-0000-000000000001','forged cross tenant rejection',
  '41000000-0000-0000-0000-000000000011')$$,'42501','PUBLIC_SESSION_FORBIDDEN',
  'submission in another organization is blocked');
select is(public.reject_public_participant(
  '31300000-0000-0000-0000-000000000000','31400000-0000-0000-0000-000000000003','not admitted',
  '41000000-0000-0000-0000-000000000012')->>'status','Rejected','pending participant can be rejected');
select ok((select cloud_version from public.session_participants where id='31400000-0000-0000-0000-000000000003')
  > (select value from teacher_rpc_versions where key='reject_before'),'participant rejection increases cloud_version');
select is(public.approve_public_enrollment_request(
  '31600000-0000-0000-0000-000000000001','41000000-0000-0000-0000-000000000013')->>'status',
  'Approved','enrollment can be approved');
select ok((select cloud_version from public.class_enrollment_requests where id='31600000-0000-0000-0000-000000000001')
  > (select value from teacher_rpc_versions where key='enrollment_approve_before'),'enrollment approval increases cloud_version');
select is(public.reject_public_enrollment_request(
  '31600000-0000-0000-0000-000000000002','student is not in this cohort',
  '41000000-0000-0000-0000-000000000014')->>'status','Rejected','enrollment can be rejected');
select ok((select cloud_version from public.class_enrollment_requests where id='31600000-0000-0000-0000-000000000002')
  > (select value from teacher_rpc_versions where key='enrollment_reject_before'),'enrollment rejection increases cloud_version');

select * from finish();
rollback;
