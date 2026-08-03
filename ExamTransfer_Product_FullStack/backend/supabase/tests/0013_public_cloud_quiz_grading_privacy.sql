begin;
select plan(30);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),27,
  'PublicCloud grading privacy remains available at schema version 27');
select has_function('public','save_public_quiz_grade',
  array['uuid','numeric','text','bigint','uuid'],'save grade RPC exists');
select has_function('public','return_public_quiz_grade',
  array['uuid','text','bigint','uuid'],'return grade RPC exists');
select has_function('public','reopen_public_quiz_grade',
  array['uuid','text','bigint','uuid'],'reopen grade RPC exists');
select ok(position(
  '''save_public_quiz_grade'''
  in lower(pg_get_functiondef(
    'public.get_examtransfer_cloud_capabilities()'::regprocedure))) > 0,
  'schema 27 capabilities advertise teacher grading RPCs');
select ok(not has_function_privilege('anon',
  'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)','EXECUTE'),
  'anon cannot save a quiz grade');
select ok(not has_function_privilege('anon',
  'public.return_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'anon cannot return a quiz grade');
select ok(not has_function_privilege('anon',
  'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'anon cannot reopen a quiz grade');
select ok(has_function_privilege('authenticated',
  'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)','EXECUTE'),
  'authenticated role may call guarded save RPC');
select ok(has_function_privilege('authenticated',
  'public.return_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'authenticated role may call guarded return RPC');
select ok(has_function_privilege('authenticated',
  'public.reopen_public_quiz_grade(uuid,text,bigint,uuid)','EXECUTE'),
  'authenticated role may call guarded reopen RPC');
select ok(position(
  '''exam-session:'' || new.session_id::text || '':device:'''
  in lower(pg_get_functiondef(
    'private.notify_public_quiz_grade_returned()'::regprocedure))) > 0,
  'grade visibility signal uses a device-target topic');
select ok(position(
  '''score'''
  in lower(pg_get_functiondef(
    'private.notify_public_quiz_grade_returned()'::regprocedure))) = 0,
  'grade visibility trigger does not serialize score');
select ok(position(
  '''maxscore'''
  in lower(pg_get_functiondef(
    'private.notify_public_quiz_grade_returned()'::regprocedure))) = 0,
  'grade visibility trigger does not serialize maxScore');

insert into auth.users(id,email) values
  ('51000000-0000-0000-0000-000000000001','phase2-teacher@example.test'),
  ('51000000-0000-0000-0000-000000000002','phase2-owner@example.test'),
  ('51000000-0000-0000-0000-000000000003','phase2-peer@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('51000000-0000-0000-0000-000000000000','Phase 2 Grading Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('51000000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000000',
   'Phase 2 Teacher','Teacher','phase2-teacher',null,true,null),
  ('51000000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000000',
   'Phase 2 Owner','Student','P2-OWNER','P2-OWNER',true,'2008-01-01'),
  ('51000000-0000-0000-0000-000000000003',
   '51000000-0000-0000-0000-000000000000',
   'Phase 2 Peer','Student','P2-PEER','P2-PEER',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values (
  '51100000-0000-0000-0000-000000000000',
  '51000000-0000-0000-0000-000000000000',
  'Phase 2 Class','P2GRADE','2026','Active','Public',
  '51000000-0000-0000-0000-000000000001',now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,
  created_by,delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values (
  '51200000-0000-0000-0000-000000000000',
  '51000000-0000-0000-0000-000000000000',
  '51100000-0000-0000-0000-000000000000',
  'Phase 2 Quiz','IT',60,'Published',1,
  '51000000-0000-0000-0000-000000000001',
  'MultipleChoice','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,
  auto_approve,accepting_participants,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,created_at,updated_at)
values (
  '51300000-0000-0000-0000-000000000000',
  '51000000-0000-0000-0000-000000000000',
  '51200000-0000-0000-0000-000000000000',
  '51100000-0000-0000-0000-000000000000',
  'P2GRADE','Finished',now()-interval '1 hour','PublicCloud',
  false,false,'MultipleChoice','Standard','Hidden',1,now(),now());
insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple,created_at,updated_at)
values
  ('51210000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000000',
   '51200000-0000-0000-0000-000000000000',1,1,'Weighted correct',7.50,false,now(),now()),
  ('51210000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000000',
   '51200000-0000-0000-0000-000000000000',1,2,'Weighted unanswered',2.50,false,now(),now());
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct,created_at,updated_at)
values
  ('51220000-0000-0000-0000-000000000001','51000000-0000-0000-0000-000000000000',
   '51210000-0000-0000-0000-000000000001',1,'Correct 1',true,now(),now()),
  ('51220000-0000-0000-0000-000000000002','51000000-0000-0000-0000-000000000000',
   '51210000-0000-0000-0000-000000000001',2,'Wrong 1',false,now(),now()),
  ('51220000-0000-0000-0000-000000000003','51000000-0000-0000-0000-000000000000',
   '51210000-0000-0000-0000-000000000002',1,'Correct 2',true,now(),now()),
  ('51220000-0000-0000-0000-000000000004','51000000-0000-0000-0000-000000000000',
   '51210000-0000-0000-0000-000000000002',2,'Wrong 2',false,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('51400000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51000000-0000-0000-0000-000000000002',
   'P2-OWNER','Phase 2 Owner','phase2-owner-a','Approved',
   now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('51400000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51000000-0000-0000-0000-000000000003',
   'P2-PEER','Phase 2 Peer','phase2-peer','Approved',
   now(),'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.public_device_connections(
  id,organization_id,session_id,participant_id,user_id,device_id,
  connection_state,heartbeat_at,source_mode,created_at,updated_at)
values
  ('51500000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51400000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000002',
   'phase2-owner-a','Online',now(),'PublicCloud',now(),now()),
  ('51500000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51400000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000002',
   'phase2-owner-b','Degraded',now(),'PublicCloud',now(),now()),
  ('51500000-0000-0000-0000-000000000003',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51400000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000003',
   'phase2-peer','Online',now(),'PublicCloud',now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,
  graded_at,snapshot_json,source_mode,created_at,updated_at)
values
  ('51600000-0000-0000-0000-000000000001',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51400000-0000-0000-0000-000000000001',
   1,'Hidden','Finalized',now()-interval '30 minutes',now(),
   now()-interval '20 minutes',8.00,8.00,10.00,'Graded',
   now()-interval '20 minutes',
   '[{"id":"51210000-0000-0000-0000-000000000001","points":7.50,"multiple":false,"choices":[{"id":"51220000-0000-0000-0000-000000000001"},{"id":"51220000-0000-0000-0000-000000000002"}]},{"id":"51210000-0000-0000-0000-000000000002","points":2.50,"multiple":false,"choices":[{"id":"51220000-0000-0000-0000-000000000003"},{"id":"51220000-0000-0000-0000-000000000004"}]}]'::jsonb,
   'PublicCloud',now(),now()),
  ('51600000-0000-0000-0000-000000000002',
   '51000000-0000-0000-0000-000000000000',
   '51300000-0000-0000-0000-000000000000',
   '51400000-0000-0000-0000-000000000002',
   1,'Hidden','Finalized',now()-interval '30 minutes',now(),
   now()-interval '20 minutes',9.00,9.00,10.00,'Graded',
   now()-interval '20 minutes',
   '[{"id":"51210000-0000-0000-0000-000000000001","points":7.50,"multiple":false,"choices":[{"id":"51220000-0000-0000-0000-000000000001"},{"id":"51220000-0000-0000-0000-000000000002"}]},{"id":"51210000-0000-0000-0000-000000000002","points":2.50,"multiple":false,"choices":[{"id":"51220000-0000-0000-0000-000000000003"},{"id":"51220000-0000-0000-0000-000000000004"}]}]'::jsonb,
   'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,
  source_mode,created_at,updated_at)
values (
  '51610000-0000-0000-0000-000000000001',
  '51000000-0000-0000-0000-000000000000',
  '51600000-0000-0000-0000-000000000001',
  '51210000-0000-0000-0000-000000000001',
  '["51220000-0000-0000-0000-000000000001"]'::jsonb,
  1,now()-interval '20 minutes','PublicCloud',now(),now());

create temporary table phase2_grade_state(
  key text primary key,
  value bigint,
  initial_value bigint,
  return_initial_value bigint) on commit drop;
insert into phase2_grade_state(key,value,initial_value,return_initial_value)
values (
  'owner',
  (select cloud_version from public.quiz_attempts
   where id='51600000-0000-0000-0000-000000000001'),
  (select cloud_version from public.quiz_attempts
   where id='51600000-0000-0000-0000-000000000001'),
  null);
grant select, update on phase2_grade_state to authenticated;

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"51000000-0000-0000-0000-000000000003","role":"authenticated"}',
  true);
select throws_ok(
  $$select public.save_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001',7.5,'forged',
    (select value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000001')$$,
  '42501','TEACHER_ROLE_REQUIRED',
  'student cannot call teacher grading RPC');

select set_config(
  'request.jwt.claims',
  '{"sub":"51000000-0000-0000-0000-000000000001","role":"authenticated"}',
  true);
select is(
  (public.save_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001',7.5,'Reviewed',
    (select value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000002')->>'score')::numeric,
  7.5::numeric,
  'owning teacher saves PublicCloud grade');
update phase2_grade_state
set value=(select cloud_version from public.quiz_attempts
           where id='51600000-0000-0000-0000-000000000001')
where key='owner';
select is(
  (public.save_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001',7.5,'Reviewed',
    (select initial_value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000002')->>'cloudVersion')::bigint,
  (select value from phase2_grade_state where key='owner'),
  'same request ID returns cached save result without a second mutation');
select throws_ok(
  $$select public.save_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001',7.0,'Stale write',
    (select initial_value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000003')$$,
  '40001','QUIZ_GRADE_VERSION_CONFLICT',
  'stale cloud version is denied');
update phase2_grade_state
set return_initial_value=value
where key='owner';
select is(
  public.return_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001','Published',
    (select value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000004')->>'gradingStatus',
  'Returned',
  'teacher returns the grade');
update phase2_grade_state
set value=(select cloud_version from public.quiz_attempts
           where id='51600000-0000-0000-0000-000000000001')
where key='owner';
select is(
  (public.return_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001','Published',
    (select return_initial_value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000004')->>'cloudVersion')::bigint,
  (select value from phase2_grade_state where key='owner'),
  'same request ID returns cached return result without a second mutation');

reset role;
select is(
  (select count(*)::integer
   from realtime.messages
   where event='QuizGradeReturned'
     and topic in (
       'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-a',
       'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-b')
     and payload->>'eventType'='QuizGradeReturned'
     and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
     and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'),
  2,
  'all devices owned by the participant receive the return signal');
select is(
  (select count(*)::integer
   from realtime.messages
   where event='QuizGradeReturned'
     and topic='exam-session:51300000-0000-0000-0000-000000000000'
     and payload->>'eventType'='QuizGradeReturned'
     and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
     and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'),
  0,
  'session-wide topic receives no grade signal');
select is(
  (select count(*)::integer
   from realtime.messages
   where event='QuizGradeReturned'
     and topic='exam-session:51300000-0000-0000-0000-000000000000:device:phase2-peer'
     and payload->>'eventType'='QuizGradeReturned'
     and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
     and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'),
  0,
  'peer participant device receives no grade signal');
select ok(
  (select array_agg(key order by key)
   from (
     select distinct jsonb_object_keys(payload) as key
     from realtime.messages
     where event='QuizGradeReturned'
       and topic in (
         'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-a',
         'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-b')
       and payload->>'eventType'='QuizGradeReturned'
       and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
       and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'
    ) keys) = array['attemptId','eventType','id','sessionId']::text[]
  and not exists(
    select 1
    from realtime.messages
    where event='QuizGradeReturned'
      and topic in (
        'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-a',
        'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-b')
      and payload->>'eventType'='QuizGradeReturned'
      and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
      and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'
      and (payload->>'id')::uuid is distinct from id),
  'broadcast physical payload has exact keys and a valid transport UUID');
select ok(not exists(
  select 1
  from realtime.messages
  where event='QuizGradeReturned'
    and topic in (
      'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-a',
      'exam-session:51300000-0000-0000-0000-000000000000:device:phase2-owner-b')
    and payload->>'eventType'='QuizGradeReturned'
    and payload->>'attemptId'='51600000-0000-0000-0000-000000000001'
    and payload->>'sessionId'='51300000-0000-0000-0000-000000000000'
    and payload ?| array[
      'score','maxScore','selectedAnswers','correctAnswers','answerKey',
      'perQuestionCorrectness','teacherComments','questionContent','participantProfile']),
  'broadcast payload contains no grading or answer data');

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"51000000-0000-0000-0000-000000000001","role":"authenticated"}',
  true);
select is(
  public.reopen_public_quiz_grade(
    '51600000-0000-0000-0000-000000000001','Recheck rubric',
    (select value from phase2_grade_state where key='owner'),
    '61000000-0000-0000-0000-000000000005')->>'gradingStatus',
  'Graded',
  'teacher reopens the returned grade');

select set_config(
  'request.jwt.claims',
  '{"sub":"51000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select is(
  public.get_public_quiz_attempt_review(
    '51600000-0000-0000-0000-000000000001')->>'scoreVisible',
  'false',
  'owner review is masked again after reopen');
select is(
  public.get_public_quiz_attempt_review(
    '51600000-0000-0000-0000-000000000001')->>'correctAnswersVisible',
  'false',
  'correct answers are masked again after reopen');
select lives_ok(
  $$select public.get_public_quiz_attempt_review(
    '51600000-0000-0000-0000-000000000001')$$,
  'student can fetch the owned authoritative review');

select set_config(
  'request.jwt.claims',
  '{"sub":"51000000-0000-0000-0000-000000000003","role":"authenticated"}',
  true);
select throws_ok(
  $$select public.get_public_quiz_attempt_review(
    '51600000-0000-0000-0000-000000000001')$$,
  'P0002','PUBLIC_QUIZ_ATTEMPT_NOT_FOUND',
  'cross-student review is denied');

select * from finish();
rollback;
