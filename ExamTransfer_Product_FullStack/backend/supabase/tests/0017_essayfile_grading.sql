begin;
create extension if not exists pgtap with schema extensions;
set local search_path = public, extensions;
select plan(55);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),26,
  'EssayFile grading remains available at schema 26');
select has_function('public','get_public_essay_grade',array['uuid'],
  'teacher essay grade read RPC exists');
select has_function('public','save_public_essay_grade',
  array['uuid','numeric','jsonb','text','bigint','uuid'],
  'save essay grade RPC exists');
select has_function('public','return_public_essay_grade',
  array['uuid','text','bigint','uuid'],
  'return essay grade RPC exists');
select has_function('public','reopen_public_essay_grade',
  array['uuid','text','bigint','uuid'],
  'reopen essay grade RPC exists');
select ok((select prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.get_public_essay_grade(uuid)'::regprocedure),
  'essay grade read RPC is SECURITY DEFINER with empty search_path');
select ok((select prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.save_public_essay_grade(uuid,numeric,jsonb,text,bigint,uuid)'::regprocedure),
  'save RPC is SECURITY DEFINER with empty search_path');
select ok((select prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.return_public_essay_grade(uuid,text,bigint,uuid)'::regprocedure),
  'return RPC is SECURITY DEFINER with empty search_path');
select ok((select prosecdef and proconfig=array['search_path=""']::text[]
  from pg_catalog.pg_proc
  where oid='public.reopen_public_essay_grade(uuid,text,bigint,uuid)'::regprocedure),
  'reopen RPC is SECURITY DEFINER with empty search_path');
select ok(not has_function_privilege('anon',
  'public.save_public_essay_grade(uuid,numeric,jsonb,text,bigint,uuid)','EXECUTE'),
  'anon cannot save an essay grade');
select ok(has_function_privilege('authenticated',
  'public.save_public_essay_grade(uuid,numeric,jsonb,text,bigint,uuid)','EXECUTE'),
  'authenticated may call the guarded save RPC');
select is((select count(*)::integer from pg_catalog.pg_indexes
  where schemaname='public' and tablename='grades'
    and indexname='ux_grades_submission_authoritative'
    and indexdef ilike '%unique%submission_id%'),1,
  'one authoritative grade per submission is enforced');
select is((select count(*)::integer from pg_catalog.pg_policies
  where schemaname='public' and tablename in ('grades','rubric_scores','graded_attachments')
    and cmd in ('INSERT','UPDATE','DELETE','ALL')),0,
  'grade tables expose no direct DML policy');
select is((select count(*)::integer from pg_catalog.pg_policies
  where schemaname='public' and tablename in ('grades','rubric_scores','graded_attachments')
    and (qual is null or lower(btrim(qual)) in ('true','(true)'))),0,
  'grade read policies are not broad true policies');

insert into auth.users(id,email) values
  ('81000000-0000-0000-0000-000000000001','a08-teacher@example.test'),
  ('81000000-0000-0000-0000-000000000002','a08-owner@example.test'),
  ('81000000-0000-0000-0000-000000000003','a08-peer@example.test'),
  ('82000000-0000-0000-0000-000000000001','a08-other-teacher@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('81000000-0000-0000-0000-000000000000','A-08 Organization'),
  ('82000000-0000-0000-0000-000000000000','A-08 Other Organization');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('81000000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','A08 Teacher','Teacher','a08-teacher',null,true,null),
  ('81000000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','A08 Owner','Student','A08-OWNER','A08-OWNER',true,'2008-01-01'),
  ('81000000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','A08 Peer','Student','A08-PEER','A08-PEER',true,'2008-01-01'),
  ('82000000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','A08 Other','Teacher','a08-other',null,true,null);
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values
  ('81100000-0000-0000-0000-000000000000','81000000-0000-0000-0000-000000000000','A08 Class','A08','2026','Active','Public','81000000-0000-0000-0000-000000000001',now(),now()),
  ('82100000-0000-0000-0000-000000000000','82000000-0000-0000-0000-000000000000','A08 Other Class','A08X','2026','Active','Public','82000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,
  created_by,delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values
  ('81200000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81100000-0000-0000-0000-000000000000','A08 Essay','IT',60,'Published',1,'81000000-0000-0000-0000-000000000001','FileSubmission','Hidden','Standard',now(),now()),
  ('81200000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81100000-0000-0000-0000-000000000000','A08 Quiz','IT',60,'Published',1,'81000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now()),
  ('82200000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','82100000-0000-0000-0000-000000000000','A08 Other Essay','IT',60,'Published',1,'82000000-0000-0000-0000-000000000001','FileSubmission','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,created_at,updated_at)
values
  ('81300000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81200000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000000','A08ESS','Finished',now()-interval '1 hour','PublicCloud',false,false,'FileSubmission','Standard','Hidden',1,now(),now()),
  ('81300000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81200000-0000-0000-0000-000000000002','81100000-0000-0000-0000-000000000000','A08QUI','Finished',now()-interval '1 hour','PublicCloud',false,false,'MultipleChoice','Standard','Hidden',1,now(),now()),
  ('81300000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','81200000-0000-0000-0000-000000000001','81100000-0000-0000-0000-000000000000','A08LAN','Finished',now()-interval '1 hour','LanOnly',false,false,'FileSubmission','Standard','Hidden',1,now(),now()),
  ('82300000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','82200000-0000-0000-0000-000000000001','82100000-0000-0000-0000-000000000000','A08OTH','Finished',now()-interval '1 hour','PublicCloud',false,false,'FileSubmission','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('81400000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000002','A08-OWNER','A08 Owner','a08-owner','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('81400000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000003','A08-PEER','A08 Peer','a08-peer','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('81400000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000002','A08-QUIZ','A08 Owner','a08-quiz','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('81400000-0000-0000-0000-000000000004','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000002','A08-LAN','A08 Owner','a08-lan','Approved',now(),'Completed','Submitted',0,false,'Lan',now(),now()),
  ('82400000-0000-0000-0000-000000000001','82000000-0000-0000-0000-000000000000','82300000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000002','A08-X','A08 Owner','a08-x','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.submissions(
  id,organization_id,session_id,participant_id,attempt_number,status,deadline_at,
  is_late,is_official,idempotency_key,source_mode,created_at,updated_at)
values
  ('81500000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000001','81400000-0000-0000-0000-000000000001',1,'Submitted',now(),false,true,'a08-owner-1','PublicCloud',now(),now()),
  ('81500000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000001','81400000-0000-0000-0000-000000000002',1,'LateSubmitted',now(),true,true,'a08-peer-1','PublicCloud',now(),now()),
  ('81500000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000002','81400000-0000-0000-0000-000000000003',1,'Submitted',now(),false,true,'a08-quiz-1','PublicCloud',now(),now()),
  ('81500000-0000-0000-0000-000000000004','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000003','81400000-0000-0000-0000-000000000004',1,'Submitted',now(),false,true,'a08-lan-1','Lan',now(),now()),
  ('81500000-0000-0000-0000-000000000005','81000000-0000-0000-0000-000000000000','81300000-0000-0000-0000-000000000001','81400000-0000-0000-0000-000000000001',2,'Submitted',now(),false,true,'a08-owner-2','PublicCloud',now(),now());
insert into public.submission_files(
  id,organization_id,submission_id,client_file_id,name,mime_type,size_bytes,sha256,
  cloud_object_path,archive_signature_verified,source_mode,created_at,updated_at)
values
  ('81600000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000','81500000-0000-0000-0000-000000000001','a08-file-1','answer.zip','application/zip',128,'a08-sha-1','private/a08-1',true,'PublicCloud',now(),now()),
  ('81600000-0000-0000-0000-000000000002','81000000-0000-0000-0000-000000000000','81500000-0000-0000-0000-000000000002','a08-file-2','peer.zip','application/zip',128,'a08-sha-2','private/a08-2',true,'PublicCloud',now(),now()),
  ('81600000-0000-0000-0000-000000000003','81000000-0000-0000-0000-000000000000','81500000-0000-0000-0000-000000000003','a08-file-3','quiz.zip','application/zip',128,'a08-sha-3','private/a08-3',true,'PublicCloud',now(),now()),
  ('81600000-0000-0000-0000-000000000004','81000000-0000-0000-0000-000000000000','81500000-0000-0000-0000-000000000004','a08-file-4','lan.zip','application/zip',128,'a08-sha-4','private/a08-4',true,'Lan',now(),now()),
  ('81600000-0000-0000-0000-000000000005','81000000-0000-0000-0000-000000000000','81500000-0000-0000-0000-000000000005','a08-file-5','rollback.zip','application/zip',128,'a08-sha-5','private/a08-5',true,'PublicCloud',now(),now());

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',8,'[]','forged',0,
  '81700000-0000-0000-0000-000000000001')$$,
  '42501','TEACHER_ROLE_REQUIRED','student cannot mutate an essay grade');

select set_config('request.jwt.claims','{"sub":"82000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',8,'[]','forged',0,
  '81700000-0000-0000-0000-000000000002')$$,
  '42501','PUBLIC_SESSION_FORBIDDEN','another organization teacher is denied');

select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000004',8,'[]','lan',0,
  '81700000-0000-0000-0000-000000000003')$$,
  'P0002','PUBLIC_ESSAY_SUBMISSION_NOT_FOUND','OnlyLAN submission is denied by cloud RPC');
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000003',8,'[]','quiz',0,
  '81700000-0000-0000-0000-000000000004')$$,
  '42501','PUBLIC_ESSAY_SUBMISSION_SCOPE_INVALID','quiz submission is denied by essay RPC');
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',-0.01,'[]','negative',0,
  '81700000-0000-0000-0000-000000000005')$$,
  '22023','ESSAY_GRADE_SCORE_INVALID','negative essay score is rejected');
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',10.01,'[]','too high',0,
  '81700000-0000-0000-0000-000000000006')$$,
  '22023','ESSAY_GRADE_SCORE_INVALID','essay score over server max is rejected');

create temporary table a08_state(
  key text primary key, value bigint, initial_value bigint, return_initial bigint,
  reopen_initial bigint,
  saved_score numeric, saved_comment text) on commit drop;
insert into a08_state values ('owner',0,0,null,null,null,null);
grant select,update on a08_state to authenticated;
select is((public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',8.25,
  '[{"criterionKey":"content","title":"Content","score":5.25,"maxScore":6,"comment":"Good","order":1},{"criterionKey":"style","title":"Style","score":3,"maxScore":4,"order":2}]',
  'Reviewed',0,'81700000-0000-0000-0000-000000000010')->>'maxScore')::numeric,
  10.00::numeric,'save uses the authoritative server max score');
update a08_state set
  value=(select cloud_version from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),
  initial_value=0,
  saved_score=(select score from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),
  saved_comment=(select general_comment from public.grades where submission_id='81500000-0000-0000-0000-000000000001')
where key='owner';
select is((select status from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),'Graded',
  'save creates a Graded essay result');
select is((select count(*)::integer from public.rubric_scores rubric join public.grades grade on grade.id=rubric.grade_id where grade.submission_id='81500000-0000-0000-0000-000000000001'),2,
  'save persists rubric details');
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),1,
  'save creates exactly one authoritative grade');
select is((public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',8.25,
  '[{"criterionKey":"content","title":"Content","score":5.25,"maxScore":6,"comment":"Good","order":1},{"criterionKey":"style","title":"Style","score":3,"maxScore":4,"order":2}]',
  'Reviewed',0,'81700000-0000-0000-0000-000000000010')->>'cloudVersion')::bigint,
  (select value from a08_state where key='owner'),
  'save retry returns the original cloud version');
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),1,
  'save retry does not duplicate the grade');
select throws_ok($$select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000001',7,'[]','stale',0,
  '81700000-0000-0000-0000-000000000011')$$,
  '40001','ESSAY_GRADE_VERSION_CONFLICT','stale cloud version is rejected');

reset role;
insert into public.graded_attachments(
  id,organization_id,grade_id,name,size_bytes,sha256,mime_type,cloud_object_path,created_at,updated_at)
select '81800000-0000-0000-0000-000000000001',organization_id,id,
  'feedback.pdf',256,'feedback-sha','application/pdf','private/feedback-a08',now(),now()
from public.grades where submission_id='81500000-0000-0000-0000-000000000001';

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),0,
  'student cannot read a Graded result');
select is((select count(*)::integer from public.rubric_scores),0,
  'student cannot read rubric before return');
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),0,
  'peer cannot read another participant grade');

select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
update a08_state set return_initial=value where key='owner';
select is(public.return_public_essay_grade(
  '81500000-0000-0000-0000-000000000001','Published',
  (select value from a08_state where key='owner'),
  '81700000-0000-0000-0000-000000000020')->>'status','Returned',
  'return transitions Graded to Returned');
update a08_state set value=(select cloud_version from public.grades where submission_id='81500000-0000-0000-0000-000000000001') where key='owner';
select ok((select returned_at is not null from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),
  'Returned result has a return timestamp');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000020' and event_type='GradeReturned'),1,
  'return creates exactly one GradeReturned event');
select is((select payload->>'submissionId' from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000020'),'81500000-0000-0000-0000-000000000001',
  'GradeReturned identifies the submission');
select is((select payload->>'attemptId' from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000020'),null,
  'GradeReturned has no quiz attempt identity');
create temporary table a08_return_event on commit drop as
select id,revision from public.student_notification_events
where mutation_request_id='81700000-0000-0000-0000-000000000020';

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.return_public_essay_grade(
  '81500000-0000-0000-0000-000000000001','Published',
  (select return_initial from a08_state where key='owner'),
  '81700000-0000-0000-0000-000000000020')->>'cloudVersion')::bigint,
  (select value from a08_state where key='owner'),
  'return retry preserves the completed revision');
reset role;
select results_eq($$select id,revision from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000020'$$,
  $$select id,revision from a08_return_event$$,
  'return retry preserves EventId and event revision');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),1,
  'owner reads the Returned grade');
select is((select count(*)::integer from public.rubric_scores),2,
  'owner reads only returned rubric details');
select is((select count(*)::integer from public.graded_attachments),1,
  'owner reads returned feedback attachment metadata');
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),0,
  'peer still cannot read owner Returned grade');

set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
update a08_state set reopen_initial=value where key='owner';
select is(public.reopen_public_essay_grade(
  '81500000-0000-0000-0000-000000000001','Recheck rubric',
  (select value from a08_state where key='owner'),
  '81700000-0000-0000-0000-000000000030')->>'status','Graded',
  'reopen transitions Returned to Graded');
update a08_state set value=(select cloud_version from public.grades where submission_id='81500000-0000-0000-0000-000000000001') where key='owner';
select ok((select score=saved_score and general_comment=saved_comment from public.grades cross join a08_state where submission_id='81500000-0000-0000-0000-000000000001' and key='owner'),
  'reopen preserves score and general comment');
select is((select count(*)::integer from public.rubric_scores rubric join public.grades grade on grade.id=rubric.grade_id where grade.submission_id='81500000-0000-0000-0000-000000000001'),2,
  'reopen preserves rubric details');
select is((select count(*)::integer from public.graded_attachments attachment join public.grades grade on grade.id=attachment.grade_id where grade.submission_id='81500000-0000-0000-0000-000000000001'),1,
  'reopen preserves feedback attachments');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000030' and event_type='GradeReopened'),1,
  'reopen creates exactly one GradeReopened event');
select ok((select participant_id='81400000-0000-0000-0000-000000000001'
  and payload->>'submissionId'='81500000-0000-0000-0000-000000000001'
  and payload->>'attemptId' is null from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000030'),
  'GradeReopened routes to the participant with SubmissionId and no AttemptId');
create temporary table a08_reopen_event on commit drop as
select id,revision from public.student_notification_events
where mutation_request_id='81700000-0000-0000-0000-000000000030';
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.reopen_public_essay_grade(
  '81500000-0000-0000-0000-000000000001','Recheck rubric',
  (select reopen_initial from a08_state where key='owner'),
  '81700000-0000-0000-0000-000000000030')->>'cloudVersion')::bigint,
  (select value from a08_state where key='owner'),
  'reopen retry preserves the completed revision');
reset role;
select results_eq($$select id,revision from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000030'$$,
  $$select id,revision from a08_reopen_event$$,
  'reopen retry preserves EventId and event revision');
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.grades where submission_id='81500000-0000-0000-0000-000000000001'),0,
  'owner visibility is masked again after reopen');
select throws_ok($$insert into public.grades(
  id,organization_id,submission_id,status,score,max_score,created_at,updated_at,revision)
  values ('81900000-0000-0000-0000-000000000001','81000000-0000-0000-0000-000000000000',
  '81500000-0000-0000-0000-000000000002','Graded',7,10,now(),now(),1)$$,
  '42501','new row violates row-level security policy for table "grades"',
  'student cannot directly insert a grade');

select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select public.save_public_essay_grade(
  '81500000-0000-0000-0000-000000000005',7.5,'[]','Rollback target',0,
  '81700000-0000-0000-0000-000000000040');
do $block$
begin
  begin
    perform public.return_public_essay_grade(
      '81500000-0000-0000-0000-000000000005','Must roll back',
      (select cloud_version from public.grades where submission_id='81500000-0000-0000-0000-000000000005'),
      '81700000-0000-0000-0000-000000000041');
    raise exception 'FORCED_A08_ROLLBACK';
  exception when others then
    if sqlerrm <> 'FORCED_A08_ROLLBACK' then
      raise;
    end if;
  end;
end
$block$;
select is((select status from public.grades where submission_id='81500000-0000-0000-0000-000000000005'),'Graded',
  'transaction rollback restores grade state');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='81700000-0000-0000-0000-000000000041'),0,
  'transaction rollback leaves no grade event');
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"81000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.reopen_public_essay_grade(
  '81500000-0000-0000-0000-000000000001','Already graded',
  (select value from a08_state where key='owner'),
  '81700000-0000-0000-0000-000000000050')$$,
  '55000','ESSAY_GRADE_NOT_REOPENABLE','reopen rejects a grade that is not Returned');
select ok(position('''save_public_essay_grade''' in lower(pg_get_functiondef(
  'public.get_examtransfer_cloud_capabilities()'::regprocedure))) > 0,
  'cloud capabilities advertise essay grading RPCs');

select * from finish();
rollback;
