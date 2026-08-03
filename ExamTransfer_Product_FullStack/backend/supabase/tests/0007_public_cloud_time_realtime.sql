begin;
select plan(23);

select is((select schema_version from public.examtransfer_cloud_meta where id=1), 26,
  'ET-01 PublicCloud timeline remains available at schema version 26');
select has_function('public','get_public_student_timeline',array['uuid'],
  'authoritative student timeline RPC exists');
select ok(not has_function_privilege('anon','public.get_public_student_timeline(uuid)','EXECUTE'),
  'anon cannot read a student timeline');
select ok(has_function_privilege(
  'authenticated',
  'public.add_public_participant_extra_time(uuid,uuid,integer,text,uuid)',
  'EXECUTE'),
  'authenticated role can execute the ownership-guarded extra-time RPC');

insert into auth.users(id,email) values
  ('51000000-0000-0000-0000-000000000001','et01-owner@example.test'),
  ('51000000-0000-0000-0000-000000000002','et01-student-a@example.test'),
  ('51000000-0000-0000-0000-000000000003','et01-student-b@example.test'),
  ('51000000-0000-0000-0000-000000000004','et01-student-c@example.test'),
  ('51000000-0000-0000-0000-000000000005','et01-nonowner@example.test'),
  ('52000000-0000-0000-0000-000000000001','et01-other-owner@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name) values
  ('51000000-0000-0000-0000-000000000000','ET-01 Org'),
  ('52000000-0000-0000-0000-000000000000','ET-01 Other Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('51000000-0000-0000-0000-000000000001','51000000-0000-0000-0000-000000000000','Owner','Teacher','et01-owner',null,true,null),
  ('51000000-0000-0000-0000-000000000002','51000000-0000-0000-0000-000000000000','Student A','Student','ET01A','ET01A',true,'2008-01-01'),
  ('51000000-0000-0000-0000-000000000003','51000000-0000-0000-0000-000000000000','Student B','Student','ET01B','ET01B',true,'2008-01-01'),
  ('51000000-0000-0000-0000-000000000004','51000000-0000-0000-0000-000000000000','Student C','Student','ET01C','ET01C',true,'2008-01-01'),
  ('51000000-0000-0000-0000-000000000005','51000000-0000-0000-0000-000000000000','Nonowner','Teacher','et01-nonowner',null,true,null),
  ('52000000-0000-0000-0000-000000000001','52000000-0000-0000-0000-000000000000','Other Owner','Teacher','et01-other',null,true,null);

insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values
  ('51100000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000000','ET-01 Class','ET01','2026','Active','Public','51000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at)
values
  ('51200000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000000','51100000-0000-0000-0000-000000000000','ET-01 Quiz','IT',60,'Published',1,'51000000-0000-0000-0000-000000000001','MultipleChoice','Standard','Hidden',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,created_at,updated_at)
values
  ('51300000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000000','51200000-0000-0000-0000-000000000000','51100000-0000-0000-0000-000000000000','ET01PC','InProgress','2026-07-25 01:00:00+00','PublicCloud',true,true,'MultipleChoice','Standard','Hidden',1,now(),now());

insert into public.class_members(
  id,organization_id,class_id,user_id,student_code,display_name,created_at,updated_at)
values
  ('51400000-0000-0000-0000-000000000001','51000000-0000-0000-0000-000000000000','51100000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000002','ET01A','Student A',now(),now()),
  ('51400000-0000-0000-0000-000000000002','51000000-0000-0000-0000-000000000000','51100000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000003','ET01B','Student B',now(),now()),
  ('51400000-0000-0000-0000-000000000003','51000000-0000-0000-0000-000000000000','51100000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000004','ET01C','Student C',now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('51500000-0000-0000-0000-000000000001','51000000-0000-0000-0000-000000000000','51300000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000002','ET01A','Student A','et01-device-a','Approved',now(),'Completed','NotStarted',0,false,'PublicCloud',now(),now()),
  ('51500000-0000-0000-0000-000000000002','51000000-0000-0000-0000-000000000000','51300000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000003','ET01B','Student B','et01-device-b','Approved',now(),'Completed','NotStarted',0,false,'PublicCloud',now(),now()),
  ('51500000-0000-0000-0000-000000000003','51000000-0000-0000-0000-000000000000','51300000-0000-0000-0000-000000000000','51000000-0000-0000-0000-000000000004','ET01C','Student C','et01-device-c','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,status,started_at,
  deadline_at,finalized_at,score,max_score,snapshot_json,source_mode,created_at,updated_at)
values
  ('51600000-0000-0000-0000-000000000002','51000000-0000-0000-0000-000000000000','51300000-0000-0000-0000-000000000000','51500000-0000-0000-0000-000000000002',1,'InProgress','2026-07-25 01:00:00+00','2026-07-25 02:00:00+00',null,null,10,'[]','PublicCloud',now(),now()),
  ('51600000-0000-0000-0000-000000000003','51000000-0000-0000-0000-000000000000','51300000-0000-0000-0000-000000000000','51500000-0000-0000-0000-000000000003',1,'Finalized','2026-07-25 01:00:00+00','2026-07-25 02:00:00+00','2026-07-25 01:30:00+00',8,10,'[]','PublicCloud',now(),now());

create temporary table et01_results(
  key text primary key,
  value jsonb,
  number bigint,
  moment timestamptz)
on commit drop;
grant select, insert, update on et01_results to authenticated;

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"51000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
insert into et01_results(key,value) values (
  'no_attempt',
  public.add_public_participant_extra_time(
    '51300000-0000-0000-0000-000000000000',
    '51500000-0000-0000-0000-000000000001',
    10,
    'no attempt accommodation',
    '51700000-0000-0000-0000-000000000001'));
select is((select (value->>'extraTimeMinutes')::integer from et01_results where key='no_attempt'),10,
  'owning teacher applies delta extra time without an attempt');
select is((select count(*)::integer from public.quiz_attempts where participant_id='51500000-0000-0000-0000-000000000001'),0,
  'extra time does not create a quiz attempt');
select ok((select (value->>'serverNowUtc')::timestamptz is not null from et01_results where key='no_attempt'),
  'extra-time RPC returns database server time');
select is((select (value->>'effectiveDeadline')::timestamptz from et01_results where key='no_attempt'),
  '2026-07-25 02:10:00+00'::timestamptz,
  'extra-time RPC returns the LAN-equivalent absolute deadline');

select set_config('request.jwt.claims','{"sub":"51000000-0000-0000-0000-000000000005","role":"authenticated"}',true);
select throws_ok($$select public.add_public_participant_extra_time(
  '51300000-0000-0000-0000-000000000000',
  '51500000-0000-0000-0000-000000000001',
  5,'forged nonowner extension','51700000-0000-0000-0000-000000000002')$$,
  '42501','PUBLIC_SESSION_FORBIDDEN','same-tenant nonowner teacher is rejected');
select set_config('request.jwt.claims','{"sub":"52000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.add_public_participant_extra_time(
  '51300000-0000-0000-0000-000000000000',
  '51500000-0000-0000-0000-000000000001',
  5,'forged cross tenant extension','51700000-0000-0000-0000-000000000003')$$,
  '42501','PUBLIC_SESSION_FORBIDDEN','cross-tenant teacher is rejected');

select set_config('request.jwt.claims','{"sub":"51000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
insert into et01_results(key,value) values (
  'active_first',
  public.add_public_participant_extra_time(
    '51300000-0000-0000-0000-000000000000',
    '51500000-0000-0000-0000-000000000002',
    15,
    'active attempt accommodation',
    '51700000-0000-0000-0000-000000000004'));
select is((select deadline_at from public.quiz_attempts where id='51600000-0000-0000-0000-000000000002'),
  '2026-07-25 02:15:00+00'::timestamptz,
  'active quiz attempt receives the absolute effective deadline');
select is((select value->>'attemptId' from et01_results where key='active_first'),
  '51600000-0000-0000-0000-000000000002',
  'extra-time result identifies the active attempt');
insert into et01_results(key,number,moment)
select 'active_version',cloud_version,deadline_at
from public.quiz_attempts where id='51600000-0000-0000-0000-000000000002';
select public.add_public_participant_extra_time(
  '51300000-0000-0000-0000-000000000000',
  '51500000-0000-0000-0000-000000000002',
  15,
  'active attempt accommodation',
  '51700000-0000-0000-0000-000000000004');
select is((select extra_time_minutes from public.session_participants where id='51500000-0000-0000-0000-000000000002'),15,
  'same operation ID does not add the delta twice');
select is((select cloud_version from public.quiz_attempts where id='51600000-0000-0000-0000-000000000002'),
  (select number from et01_results where key='active_version'),
  'same operation ID does not mutate the active attempt twice');
select public.add_public_participant_extra_time(
  '51300000-0000-0000-0000-000000000000',
  '51500000-0000-0000-0000-000000000002',
  5,
  'second valid accommodation',
  '51700000-0000-0000-0000-000000000005');
select is((select deadline_at from public.quiz_attempts where id='51600000-0000-0000-0000-000000000002'),
  '2026-07-25 02:20:00+00'::timestamptz,
  'a second operation ID is applied in server order');

select public.add_public_participant_extra_time(
  '51300000-0000-0000-0000-000000000000',
  '51500000-0000-0000-0000-000000000003',
  20,
  'post-finalization accommodation record',
  '51700000-0000-0000-0000-000000000006');
select is((select deadline_at from public.quiz_attempts where id='51600000-0000-0000-0000-000000000003'),
  '2026-07-25 02:00:00+00'::timestamptz,
  'finalized attempt deadline is not changed');

select set_config('request.jwt.claims','{"sub":"51000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select results_eq($$
  with changed as (
    update public.quiz_attempts
    set deadline_at = deadline_at + interval '1 hour'
    where id='51600000-0000-0000-0000-000000000002'
    returning 1)
  select count(*)::bigint from changed$$,
  array[0::bigint],
  'student RLS cannot update own attempt deadline directly');
select results_eq($$
  with changed as (
    update public.session_participants
    set extra_time_minutes = 480
    where id='51500000-0000-0000-0000-000000000002'
    returning 1)
  select count(*)::bigint from changed$$,
  array[0::bigint],
  'student RLS cannot update own extra time directly');

insert into et01_results(key,value) values (
  'timeline',
  public.get_public_student_timeline('51300000-0000-0000-0000-000000000000'));
select is((select (value->>'attemptDeadlineUtc')::timestamptz from et01_results where key='timeline'),
  '2026-07-25 02:20:00+00'::timestamptz,
  'student reconnect snapshot returns the latest attempt deadline');
select ok((select (value->>'serverNowUtc')::timestamptz is not null
           and (value->>'revision')::bigint > 0
           from et01_results where key='timeline'),
  'student reconnect snapshot carries database time and revision');
select is((select value->>'submissionStatus' from et01_results where key='timeline'),
  'NotStarted',
  'student reconnect snapshot carries the safe submission state used by the shared coordinator');

select set_config('request.jwt.claims','{"sub":"51000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((public.get_public_student_timeline(
  '51300000-0000-0000-0000-000000000000')->>'participantId'),
  '51500000-0000-0000-0000-000000000001',
  'timeline is scoped to the authenticated student participant');
select throws_ok($$select public.get_public_student_timeline(
  '52300000-0000-0000-0000-000000000000')$$,
  'P0002','PUBLIC_STUDENT_TIMELINE_NOT_FOUND',
  'student cannot read a timeline outside the scoped session');

select * from finish();
rollback;
