begin;
select plan(40);

select has_table('public','student_notification_events','A-07 durable event table exists');
select has_function('public','send_public_teacher_message',array['uuid','uuid','text','text','uuid'],
  'teacher message mutation RPC exists');
select has_function('public','get_public_student_notification_events',array['uuid','bigint','uuid','integer'],
  'student notification catch-up RPC exists');
select ok((select relrowsecurity from pg_catalog.pg_class
  where oid='public.student_notification_events'::regclass),'event table has RLS enabled');
select ok((select bool_and(pg_get_constraintdef(oid) like '%' || event_type || '%')
  from pg_catalog.pg_constraint
  cross join (values
    ('ParticipantApproved'),('ParticipantAdmissionRejected'),('TeacherMessageReceived'),
    ('SubmissionRejected'),('ResubmitAllowed'),('GradeReturned'),('QuizGradeReturned'),
    ('GradeReopened'),('QuizGradeReopened')) expected(event_type)
  where conrelid='public.student_notification_events'::regclass
    and conname='ck_student_notification_event_type'),
  'event type constraint contains exactly the nine A-05 event names');
select ok(has_table_privilege('authenticated','public.student_notification_events','SELECT'),
  'authenticated can reach the event table through Data API');
select ok(not has_table_privilege('authenticated','public.student_notification_events','INSERT'),
  'authenticated has no direct event insert privilege');
select ok(not has_table_privilege('authenticated','public.student_notification_events','UPDATE'),
  'authenticated has no direct event update privilege');
select ok(not has_table_privilege('authenticated','public.student_notification_events','DELETE'),
  'authenticated has no direct event delete privilege');
select ok((select prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.send_public_teacher_message(uuid,uuid,text,text,uuid)'::regprocedure),
  'teacher message SECURITY DEFINER uses an empty search_path');
select ok((select not prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.get_public_student_notification_events(uuid,bigint,uuid,integer)'::regprocedure),
  'catch-up RPC is SECURITY INVOKER with an empty search_path');
select is((select count(*)::integer from pg_catalog.pg_publication_tables
  where pubname='supabase_realtime' and schemaname='public'
    and tablename='student_notification_events'),1,
  'Realtime publication contains the A-07 event table exactly once');

insert into auth.users(id,email) values
  ('71000000-0000-0000-0000-000000000001','a07-teacher@example.test'),
  ('71000000-0000-0000-0000-000000000002','a07-student-a@example.test'),
  ('71000000-0000-0000-0000-000000000003','a07-student-b@example.test'),
  ('71000000-0000-0000-0000-000000000004','a07-student-session-two@example.test'),
  ('71000000-0000-0000-0000-000000000005','a07-student-rollback@example.test'),
  ('72000000-0000-0000-0000-000000000001','a07-other-teacher@example.test'),
  ('72000000-0000-0000-0000-000000000002','a07-other-student@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('71000000-0000-0000-0000-000000000000','A-07 Organization'),
  ('72000000-0000-0000-0000-000000000000','A-07 Other Organization');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('71000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','Teacher','Teacher','a07-teacher',null,true,null),
  ('71000000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','Student A','Student','A07A','A07A',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','Student B','Student','A07B','A07B',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000004','71000000-0000-0000-0000-000000000000','Student Session Two','Student','A07C','A07C',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000005','71000000-0000-0000-0000-000000000000','Student Rollback','Student','A07D','A07D',true,'2008-01-01'),
  ('72000000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000000','Other Teacher','Teacher','a07-other',null,true,null),
  ('72000000-0000-0000-0000-000000000002','72000000-0000-0000-0000-000000000000','Other Student','Student','A07X','A07X',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values
  ('71100000-0000-0000-0000-000000000000','71000000-0000-0000-0000-000000000000','A-07 Class','A07','2026','Active','Public','71000000-0000-0000-0000-000000000001',now(),now()),
  ('72100000-0000-0000-0000-000000000000','72000000-0000-0000-0000-000000000000','A-07 Other Class','A07X','2026','Active','Public','72000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,created_at,updated_at)
values
  ('71200000-0000-0000-0000-000000000000','71000000-0000-0000-0000-000000000000','71100000-0000-0000-0000-000000000000','A-07 Exam','IT',60,'Published',1,'71000000-0000-0000-0000-000000000001','FileSubmission',now(),now()),
  ('72200000-0000-0000-0000-000000000000','72000000-0000-0000-0000-000000000000','72100000-0000-0000-0000-000000000000','A-07 Other Exam','IT',60,'Published',1,'72000000-0000-0000-0000-000000000001','FileSubmission',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,access_mode,auto_approve,
  accepting_participants,created_at,updated_at)
values
  ('71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000000','71100000-0000-0000-0000-000000000000','A07ONE','Waiting','PublicCloud',false,true,now(),now()),
  ('71300000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000000','71100000-0000-0000-0000-000000000000','A07TWO','Waiting','PublicCloud',false,true,now(),now()),
  ('71300000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71200000-0000-0000-0000-000000000000','71100000-0000-0000-0000-000000000000','A07LAN','Waiting','LanOnly',false,true,now(),now()),
  ('72300000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000000','72200000-0000-0000-0000-000000000000','72100000-0000-0000-0000-000000000000','A07OTH','Waiting','PublicCloud',false,true,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('71400000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000002','A07A','Student A','a07-a','PendingApproval',now(),'NotStarted','Submitted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000003','A07B','Student B','a07-b','PendingApproval',now(),'NotStarted','Submitted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000004','A07C','Student Session Two','a07-c','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000004','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000005','A07D','Student Rollback','a07-d','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now()),
  ('71400000-0000-0000-0000-000000000005','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000005','A07L','Student LAN','a07-l','PendingApproval',now(),'NotStarted','NotStarted',0,false,'Lan',now(),now()),
  ('72400000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000000','72300000-0000-0000-0000-000000000001','72000000-0000-0000-0000-000000000002','A07X','Other Student','a07-x','PendingApproval',now(),'NotStarted','NotStarted',0,false,'PublicCloud',now(),now());
insert into public.submissions(
  id,organization_id,session_id,participant_id,attempt_number,status,deadline_at,
  is_late,is_official,idempotency_key,source_mode,created_at,updated_at)
values
  ('71500000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000001',1,'Submitted',now()+interval '1 hour',false,true,'a07-submission-a','PublicCloud',now(),now()),
  ('71500000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000000','71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000002',1,'Submitted',now()+interval '1 hour',false,true,'a07-submission-b','PublicCloud',now(),now());

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select public.approve_public_participant(
  '71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000001',
  '71600000-0000-0000-0000-000000000001');
reset role;
create temporary table a07_saved_event as
select id,revision from public.student_notification_events
where mutation_request_id='71600000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select public.approve_public_participant(
  '71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000001',
  '71600000-0000-0000-0000-000000000001');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='71600000-0000-0000-0000-000000000001'),1,
  'valid PublicCloud approval creates exactly one event');
select results_eq($$
  select id,revision from public.student_notification_events
  where mutation_request_id='71600000-0000-0000-0000-000000000001'$$,
  $$select id,revision from a07_saved_event$$,
  'same MutationRequestId preserves EventId and revision');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
do $block$
begin
  begin
    perform public.approve_public_participant(
      '71300000-0000-0000-0000-000000000001',
      '71400000-0000-0000-0000-000000000004',
      '71600000-0000-0000-0000-000000000002');
    raise exception 'FORCED_A07_ROLLBACK';
  exception when raise_exception then
    null;
  end;
end
$block$;
reset role;
select is((select status from public.session_participants
  where id='71400000-0000-0000-0000-000000000004'),'PendingApproval',
  'mutation rollback restores participant state');
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='71600000-0000-0000-0000-000000000002'),0,
  'mutation rollback leaves no event');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.approve_public_participant(
  '71300000-0000-0000-0000-000000000003','71400000-0000-0000-0000-000000000005',
  '71600000-0000-0000-0000-000000000003')$$,
  'P0002','PUBLIC_SESSION_NOT_FOUND','PublicCloud RPC rejects an OnlyLAN session');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where session_id='71300000-0000-0000-0000-000000000003'),0,
  'OnlyLAN mutation attempt creates no cloud event');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select public.approve_public_participant(
  '71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000002',
  '71600000-0000-0000-0000-000000000004');
select public.approve_public_participant(
  '71300000-0000-0000-0000-000000000002','71400000-0000-0000-0000-000000000003',
  '71600000-0000-0000-0000-000000000005');
select public.send_public_teacher_message(
  '71300000-0000-0000-0000-000000000001',null,'Information','Broadcast A-07',
  '71600000-0000-0000-0000-000000000006');
select public.send_public_teacher_message(
  '71300000-0000-0000-0000-000000000001','71400000-0000-0000-0000-000000000002',
  'Warning','Private B','71600000-0000-0000-0000-000000000007');
select public.allow_public_resubmission(
  '71400000-0000-0000-0000-000000000001','Retry allowed',
  '71600000-0000-0000-0000-000000000008');
select public.reject_public_submission(
  '71500000-0000-0000-0000-000000000002','Archive unreadable',
  '71600000-0000-0000-0000-000000000009');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where event_type='TeacherMessageReceived' and participant_id is null),1,
  'session broadcast teacher message creates one durable event');
select is((select count(*)::integer from public.student_notification_events
  where event_type='TeacherMessageReceived'
    and participant_id='71400000-0000-0000-0000-000000000002'),1,
  'participant teacher message creates one private event');
select is((select payload->>'submissionId' from public.student_notification_events
  where event_type='ResubmitAllowed'),'71500000-0000-0000-0000-000000000001',
  'resubmit event identifies the authoritative submission');
select is((select payload->>'reason' from public.student_notification_events
  where event_type='SubmissionRejected'),'Archive unreadable',
  'submission rejection event contains the controlled reason');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"72000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select public.approve_public_participant(
  '72300000-0000-0000-0000-000000000001','72400000-0000-0000-0000-000000000001',
  '72600000-0000-0000-0000-000000000001');

select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.student_notification_events
  where participant_id='71400000-0000-0000-0000-000000000001'),2,
  'student A reads participant-specific events for A');
select is((select count(*)::integer from public.student_notification_events
  where participant_id='71400000-0000-0000-0000-000000000002'),0,
  'student A cannot read participant B events');
select is((select count(*)::integer from public.student_notification_events
  where session_id='71300000-0000-0000-0000-000000000002'),0,
  'student A cannot read another session');
select is((select count(*)::integer from public.student_notification_events
  where session_id='71300000-0000-0000-0000-000000000001'
    and participant_id is null),1,
  'student A reads only the broadcast for a joined session');
select is((select count(*)::integer from public.student_notification_events
  where organization_id='72000000-0000-0000-0000-000000000000'),0,
  'student A cannot read another organization');
select is((select count(*)::integer from public.get_public_student_notification_events(
  '71300000-0000-0000-0000-000000000001',0,null,100)),3,
  'RLS-protected catch-up returns all and only student A visible events');
select throws_ok($$insert into public.student_notification_events
  select gen_random_uuid(),organization_id,session_id,participant_id,event_type,payload,
    revision+100,gen_random_uuid(),gen_random_uuid(),occurred_at,created_at
  from public.student_notification_events limit 1$$,
  '42501','permission denied for table student_notification_events',
  'student cannot insert an event directly');

select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is((select count(*)::integer from public.student_notification_events
  where participant_id='71400000-0000-0000-0000-000000000002'),3,
  'student B reads own participant-specific events');
select is((select count(*)::integer from public.student_notification_events
  where participant_id is null),1,
  'broadcast is visible to another participant in the same session');

select set_config('request.jwt.claims','{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$insert into public.student_notification_events
  select gen_random_uuid(),organization_id,session_id,participant_id,event_type,payload,
    revision+200,gen_random_uuid(),gen_random_uuid(),occurred_at,created_at
  from public.student_notification_events limit 1$$,
  '42501','permission denied for table student_notification_events',
  'teacher client cannot insert an event directly');

reset role;
set local role anon;
select set_config('request.jwt.claims','{"role":"anon"}',true);
select throws_ok($$select * from public.student_notification_events$$,
  '42501','permission denied for table student_notification_events',
  'anonymous cannot read notification events');
reset role;

select ok(not exists (
  select 1 from pg_catalog.pg_policies
  where schemaname='public' and tablename='student_notification_events'
    and coalesce(qual,'') ~* '^\\s*true\\s*$'),
  'event RLS policy never uses USING true');
select ok(not exists (
  select 1 from public.student_notification_events event
  where event.payload->>'eventId' <> event.id::text
     or event.payload->>'eventType' <> event.event_type
     or event.payload->>'sessionId' <> event.session_id::text
     or (event.payload->>'revision')::bigint <> event.revision),
  'all stored payloads match the authoritative A-05 envelope columns');
select is((select count(*)::integer from public.student_notification_events),
  (select count(distinct (session_id,revision))::integer
   from public.student_notification_events),
  'persisted revisions are unique inside each session');
select is((select count(*)::integer from public.student_notification_events
  where session_id='71300000-0000-0000-0000-000000000003'),0,
  'event storage remains isolated from OnlyLAN data');
select ok((select count(*) from public.student_notification_events) >= 8,
  'A-07 fixtures exercised participant, broadcast, submission, and cross-tenant storage');
select ok((select bool_and(occurred_at <= created_at) from public.student_notification_events),
  'occurred and created timestamps are database-authored');
select ok((select bool_and(revision > 0) from public.student_notification_events),
  'all event revisions are positive persisted values');

select * from finish();
rollback;
