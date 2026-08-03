begin;
select plan(47);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),27,
  'A-09 Quiz grading remains available at schema 27');
select has_function('private','calculate_public_quiz_grade',array['uuid'],
  'authoritative Quiz calculator exists');
select has_function('public','save_public_quiz_grade',array['uuid','numeric','text','bigint','uuid'],
  'canonical Save RPC remains');
select has_function('public','return_public_quiz_grade',array['uuid','text','bigint','uuid'],
  'Return RPC remains');
select has_function('public','reopen_public_quiz_grade',array['uuid','text','bigint','uuid'],
  'Reopen RPC remains');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)'::regprocedure),array[]::text[])),
  'Save RPC has empty search_path');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.return_public_quiz_grade(uuid,text,bigint,uuid)'::regprocedure),array[]::text[])),
  'Return RPC has empty search_path');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)'::regprocedure),array[]::text[])),
  'Reopen RPC has empty search_path');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'private.calculate_public_quiz_grade(uuid)'::regprocedure),array[]::text[])),
  'calculator has empty search_path');
select ok(not has_function_privilege('anon',
  'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)','EXECUTE'),
  'anon cannot Save Quiz grade');
select ok(has_function_privilege('authenticated',
  'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)','EXECUTE'),
  'authenticated can call guarded Save RPC');
select ok(has_function_privilege('authenticated',
  'public.return_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'authenticated can call guarded Return RPC');
select ok(has_function_privilege('authenticated',
  'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'authenticated can call guarded Reopen RPC');
select ok(position('grading_status = ''Returned''' in (
  select pg_get_expr(polqual,polrelid) from pg_policy
  where polname='quiz_attempts_student_own')) > 0,
  'student direct-read policy requires Returned');
select ok(position('true' in lower((
  select pg_get_expr(polqual,polrelid) from pg_policy
  where polname='quiz_attempts_student_own'))) = 0,
  'student Quiz policy has no broad USING true');
select ok(position('new.grading_status = ''Graded''' in pg_get_functiondef(
  'private.capture_public_quiz_grade_notification()'::regprocedure)) > 0,
  'reopen notification transition is Returned to Graded');
select is((select count(*)::integer from pg_proc p join pg_namespace n on n.oid=p.pronamespace
  where n.nspname='public' and p.proname='save_public_quiz_grade'),1,
  'no ambiguous Save overload exists');

insert into auth.users(id,email) values
  ('71000000-0000-0000-0000-000000000001','a09-teacher@example.test'),
  ('71000000-0000-0000-0000-000000000002','a09-student@example.test'),
  ('71000000-0000-0000-0000-000000000003','a09-peer@example.test'),
  ('71000000-0000-0000-0000-000000000004','a09-other-teacher@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('71100000-0000-0000-0000-000000000001','A09 Org'),
  ('71100000-0000-0000-0000-000000000002','A09 Other Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('71000000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
   'A09 Teacher','Teacher','a09-teacher',null,true,null),
  ('71000000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000001',
   'A09 Student','Student','A09-S1','A09-S1',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000003','71100000-0000-0000-0000-000000000001',
   'A09 Peer','Student','A09-S2','A09-S2',true,'2008-01-01'),
  ('71000000-0000-0000-0000-000000000004','71100000-0000-0000-0000-000000000002',
   'A09 Other Teacher','Teacher','a09-other-teacher',null,true,null);
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '71200000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
  'A09 Class','A09CLASS','2026','Active','Public',
  '71000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,
  created_by,delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  '71300000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
  '71200000-0000-0000-0000-000000000001','A09 Quiz','Test',30,'Published',1,
  '71000000-0000-0000-0000-000000000001','MultipleChoice','ShowAfterSubmission','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,created_at,updated_at)
values (
  '71400000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
  '71300000-0000-0000-0000-000000000001','71200000-0000-0000-0000-000000000001',
  'A09QUIZ','Finished',now()-interval '1 hour','PublicCloud',false,false,
  'MultipleChoice','Standard','ShowAfterSubmission',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('71500000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
   '71400000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000002',
   'A09-S1','A09 Student','a09-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now()),
  ('71500000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000001',
   '71400000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000003',
   'A09-S2','A09 Peer','a09-peer-device','Approved',now(),'Completed','Submitted',0,false,
   'PublicCloud',now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple,created_at,updated_at)
values
  ('71600000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
   '71300000-0000-0000-0000-000000000001',1,1,'A09 Q1',2.50,false,now(),now()),
  ('71600000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000001',
   '71300000-0000-0000-0000-000000000001',1,2,'A09 Q2',3.25,false,now(),now()),
  ('71600000-0000-0000-0000-000000000003','71100000-0000-0000-0000-000000000001',
   '71300000-0000-0000-0000-000000000001',1,3,'A09 Q3',4.25,false,now(),now());
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct,created_at,updated_at)
values
  ('71700000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000001',1,'Q1 correct',true,now(),now()),
  ('71700000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000001',2,'Q1 wrong',false,now(),now()),
  ('71700000-0000-0000-0000-000000000003','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000002',1,'Q2 correct',true,now(),now()),
  ('71700000-0000-0000-0000-000000000004','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000002',2,'Q2 wrong',false,now(),now()),
  ('71700000-0000-0000-0000-000000000005','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000003',1,'Q3 correct',true,now(),now()),
  ('71700000-0000-0000-0000-000000000006','71100000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000003',2,'Q3 wrong',false,now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,
  graded_at,snapshot_json,source_mode,created_at,updated_at)
values (
  '71800000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
  '71400000-0000-0000-0000-000000000001','71500000-0000-0000-0000-000000000001',
  1,'ShowAfterSubmission','Finalized',now()-interval '30 minutes',now(),now()-interval '20 minutes',
  9.00,9.00,10.00,'Graded',now()-interval '20 minutes',
  '[{"id":"71600000-0000-0000-0000-000000000001","points":2.50,"multiple":false,"choices":[{"id":"71700000-0000-0000-0000-000000000001"},{"id":"71700000-0000-0000-0000-000000000002"}]},{"id":"71600000-0000-0000-0000-000000000002","points":3.25,"multiple":false,"choices":[{"id":"71700000-0000-0000-0000-000000000003"},{"id":"71700000-0000-0000-0000-000000000004"}]},{"id":"71600000-0000-0000-0000-000000000003","points":4.25,"multiple":false,"choices":[{"id":"71700000-0000-0000-0000-000000000005"},{"id":"71700000-0000-0000-0000-000000000006"}]}]'::jsonb,
  'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values
  ('71900000-0000-0000-0000-000000000001','71100000-0000-0000-0000-000000000001',
   '71800000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000001',
   '["71700000-0000-0000-0000-000000000001"]'::jsonb,1,now(),'PublicCloud',now(),now()),
  ('71900000-0000-0000-0000-000000000002','71100000-0000-0000-0000-000000000001',
   '71800000-0000-0000-0000-000000000001','71600000-0000-0000-0000-000000000002',
   '["71700000-0000-0000-0000-000000000004"]'::jsonb,1,now(),'PublicCloud',now(),now());

create temporary table a09_state(
  key text primary key, initial_version bigint, save_version bigint,
  return_version bigint, reopen_version bigint) on commit drop;
insert into a09_state values ('attempt',
  (select cloud_version from public.quiz_attempts where id='71800000-0000-0000-0000-000000000001'),
  null,null,null);
grant select,update on a09_state to authenticated;

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),0,
  'student direct read hides Graded attempt even with ShowAfterSubmission policy');
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',2.50,null,
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000001')$$,
  '42501','TEACHER_ROLE_REQUIRED','student cannot mutate Quiz grade');

select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000004","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',2.50,null,
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000002')$$,
  '42501','PUBLIC_SESSION_FORBIDDEN','other organization teacher is denied');

select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',9.99,'forged',
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000003')$$,
  '22023','QUIZ_GRADE_CLIENT_SCORE_MISMATCH','forged client score is rejected');
reset role;
select is((select count(*)::integer from private.public_teacher_mutation_requests
  where request_id='72000000-0000-0000-0000-000000000003'),0,
  'failed Save rolls back mutation receipt');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',null,'Authoritative',
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000004')->>'score')::numeric,2.50::numeric,
  'Save calculates weighted authoritative score');
reset role;
select is((select auto_score from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),2.50::numeric,
  'Save persists authoritative auto score');
select is((select max_score from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),10.00::numeric,
  'Save persists authoritative max score');
update a09_state set save_version=(select cloud_version from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001') where key='attempt';
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',null,'Authoritative',
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000004')->>'cloudVersion')::bigint,
  (select save_version from a09_state where key='attempt'),
  'Save retry returns stable cloud version');
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',null,'stale',
  (select initial_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000005')$$,
  '40001','QUIZ_GRADE_VERSION_CONFLICT','stale Save cannot overwrite');

select is(public.return_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001','Published',
  (select save_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000006')->>'gradingStatus','Returned',
  'Return changes Graded to Returned');
reset role;
select ok((select returned_at is not null from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),
  'Return stamps returned_at');
update a09_state set return_version=(select cloud_version from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001') where key='attempt';
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='72000000-0000-0000-0000-000000000006'
    and event_type='QuizGradeReturned'),1,
  'Return creates exactly one A-07 event');
select ok((select payload->>'attemptId'='71800000-0000-0000-0000-000000000001'
    and payload->>'submissionId' is null and (payload->>'score')::numeric=2.50
  from public.student_notification_events
  where mutation_request_id='72000000-0000-0000-0000-000000000006'
    and event_type='QuizGradeReturned'),
  'Returned event uses AttemptId, null SubmissionId and authoritative score');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.return_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001','Published',
  (select save_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000006')->>'cloudVersion')::bigint,
  (select return_version from a09_state where key='attempt'),
  'Return retry returns stable revision');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='72000000-0000-0000-0000-000000000006'),1,
  'Return retry does not duplicate event');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),1,
  'owner can directly read only the Returned attempt');
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is((select count(*)::integer from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),0,
  'peer cannot read another participant result');
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is(public.get_public_quiz_attempt_review(
  '71800000-0000-0000-0000-000000000001')->>'scoreVisible','true',
  'Returned owner review exposes score');
select is(public.get_public_quiz_attempt_review(
  '71800000-0000-0000-0000-000000000001')->>'correctAnswersVisible','true',
  'existing returned-only detailed review policy is preserved');

select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is(public.reopen_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001','Recheck',
  (select return_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000007')->>'gradingStatus','Graded',
  'Reopen changes Returned back to Graded');
reset role;
select ok((select returned_at is null and score=2.50 and general_comment='Authoritative'
  from public.quiz_attempts where id='71800000-0000-0000-0000-000000000001'),
  'Reopen hides result and preserves score/comment');
update a09_state set reopen_version=(select cloud_version from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001') where key='attempt';
select ok((select count(*)=1 and bool_and(payload->>'attemptId'=
    '71800000-0000-0000-0000-000000000001' and payload->>'submissionId' is null)
  from public.student_notification_events
  where mutation_request_id='72000000-0000-0000-0000-000000000007'
    and event_type='QuizGradeReopened'),
  'Reopen creates one AttemptId-only A-07 event');
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is((public.reopen_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001','Recheck',
  (select return_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000007')->>'cloudVersion')::bigint,
  (select reopen_version from a09_state where key='attempt'),
  'Reopen retry keeps revision stable');
reset role;
select is((select count(*)::integer from public.student_notification_events
  where mutation_request_id='72000000-0000-0000-0000-000000000007'),1,
  'Reopen retry keeps event identity stable');

set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is((select count(*)::integer from public.quiz_attempts
  where id='71800000-0000-0000-0000-000000000001'),0,
  'student direct read is hidden again after reopen');

reset role;
update public.quiz_answers set choice_ids='["71700000-0000-0000-0000-000000000003"]'::jsonb
where id='71900000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',null,null,
  (select reopen_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000008')$$,
  '22023','PUBLIC_QUIZ_ANSWER_CHOICE_MISMATCH','cross-question selected option is rejected');
reset role;
select ok((select count(*)=0 from private.public_teacher_mutation_requests
    where request_id='72000000-0000-0000-0000-000000000008')
  and (select cloud_version=(select reopen_version from a09_state where key='attempt')
       from public.quiz_attempts where id='71800000-0000-0000-0000-000000000001'),
  'invalid answer rollback leaves no receipt or version change');

update public.exam_sessions set access_mode='LanOnly'
where id='71400000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims',
  '{"sub":"71000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.save_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001',null,null,
  (select reopen_version from a09_state where key='attempt'),
  '72000000-0000-0000-0000-000000000009')$$,
  'P0002','PUBLIC_SESSION_NOT_FOUND','OnlyLAN session is rejected by PublicCloud grading RPC');
reset role;
update public.exam_sessions set access_mode='PublicCloud'
where id='71400000-0000-0000-0000-000000000001';
update public.quiz_answers set choice_ids='["71700000-0000-0000-0000-000000000001"]'::jsonb
where id='71900000-0000-0000-0000-000000000001';
select ok(not (private.calculate_public_quiz_grade(
  '71800000-0000-0000-0000-000000000001') ?| array[
    'correctOptionId','answerKey','correctAnswers','selectedAnswers']),
  'calculator result exposes aggregate only, not correct-answer details');

select * from finish();
rollback;
