begin;
select plan(41);

select is((select schema_version from public.examtransfer_cloud_meta where id=1),26,
  'A-10 advances PublicCloud schema to 26');
select has_function('public','get_student_results',array['integer','timestamp with time zone','text','uuid'],
  'typed student results RPC exists');
select ok('search_path=""' = any(coalesce((select proconfig from pg_proc where oid=
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure),array[]::text[])),
  'student results RPC has empty search_path');
select ok((select prosecdef from pg_proc where oid=
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure),
  'student results RPC is security definer for authoritative Quiz aggregation');
select ok(not has_function_privilege('anon',
  'public.get_student_results(integer,timestamptz,text,uuid)','EXECUTE'),
  'anon cannot execute student results RPC');
select ok(has_function_privilege('authenticated',
  'public.get_student_results(integer,timestamptz,text,uuid)','EXECUTE'),
  'authenticated may execute the guarded RPC');
select ok(not has_function_privilege('service_role',
  'public.get_student_results(integer,timestamptz,text,uuid)','EXECUTE'),
  'service role has no direct student results RPC grant');
select ok(position('private.require_active_student()' in pg_get_functiondef(
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure)) > 0,
  'RPC derives and validates the actor from auth.uid');
select ok(position('p_student' in lower(pg_get_functiondef(
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure))) = 0
  and position('p_participant' in lower(pg_get_functiondef(
  'public.get_student_results(integer,timestamptz,text,uuid)'::regprocedure))) = 0,
  'RPC accepts no caller-supplied student or participant identity');
select has_column('public','quiz_attempts','attempt_number',
  'Quiz attempt number is persisted authoritatively');
select ok(exists(select 1 from pg_constraint where conname='ck_quiz_attempts_attempt_number'),
  'Quiz attempt number is constrained positive');
select ok(exists(
  select 1 from pg_indexes
  where schemaname='public'
    and tablename='quiz_attempts'
    and indexname='ux_quiz_attempts_participant_attempt_number'
    and replace(indexdef, ' ', '') like '%(organization_id,session_id,participant_id,attempt_number)%'),
  'Quiz attempt number uniqueness is scoped to its session and participant');
select ok(position('status = ''Returned''' in (
  select pg_get_expr(polqual,polrelid) from pg_policy
  where polname='grades_staff_or_returned_owner_select')) > 0,
  'Essay direct-read policy requires Returned for students');
select ok(position('grading_status = ''Returned''' in (
  select pg_get_expr(polqual,polrelid) from pg_policy
  where polname='quiz_attempts_student_own')) > 0,
  'Quiz direct-read policy requires Returned');
select is((select count(*)::integer from pg_policies
  where schemaname='public' and tablename in ('grades','graded_attachments','quiz_attempts')
    and (qual is null or lower(btrim(qual)) in ('true','(true)'))),0,
  'student result policies contain no broad true predicate');
select ok(position('get_student_results' in pg_get_functiondef(
  'public.get_examtransfer_cloud_capabilities()'::regprocedure)) > 0,
  'cloud capabilities advertise the A-10 RPC');

insert into auth.users(id,email) values
  ('91000000-0000-0000-0000-000000000001','a10-teacher@example.test'),
  ('91000000-0000-0000-0000-000000000002','a10-owner@example.test'),
  ('91000000-0000-0000-0000-000000000003','a10-peer@example.test'),
  ('91000000-0000-0000-0000-000000000004','a10-empty@example.test'),
  ('92000000-0000-0000-0000-000000000001','a10-other@example.test')
on conflict (id) do nothing;
insert into public.organizations(id,name) values
  ('91100000-0000-0000-0000-000000000001','A10 Org'),
  ('92100000-0000-0000-0000-000000000001','A10 Other Org');
insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('91000000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','A10 Teacher','Teacher','a10-teacher',null,true,null),
  ('91000000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','A10 Owner','Student','A10-S1','A10-S1',true,'2008-01-01'),
  ('91000000-0000-0000-0000-000000000003','91100000-0000-0000-0000-000000000001','A10 Peer','Student','A10-S2','A10-S2',true,'2008-01-01'),
  ('91000000-0000-0000-0000-000000000004','91100000-0000-0000-0000-000000000001','A10 Empty','Student','A10-S3','A10-S3',true,'2008-01-01'),
  ('92000000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','A10 Other','Student','A10-X','A10-X',true,'2008-01-01');
insert into public.classes(
  id,organization_id,name,code,school_year,status,access_mode,created_by,created_at,updated_at)
values
  ('91100000-0000-0000-0000-000000000010','91100000-0000-0000-0000-000000000001','A10 Class','A10CLASS','2026','Active','Public','91000000-0000-0000-0000-000000000001',now(),now()),
  ('92100000-0000-0000-0000-000000000010','92100000-0000-0000-0000-000000000001','A10 Other Class','A10OTHER','2026','Active','Public',null,now(),now());
insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,quiz_result_policy,supervision_mode,created_at,updated_at)
values
  ('91200000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000010','A10 Essay','Test',60,'Published',1,'91000000-0000-0000-0000-000000000001','FileSubmission','Hidden','Standard',now(),now()),
  ('91200000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000010','A10 Quiz','Test',60,'Published',1,'91000000-0000-0000-0000-000000000001','MultipleChoice','Hidden','Standard',now(),now()),
  ('92200000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000010','A10 Other Essay','Test',60,'Published',1,null,'FileSubmission','Hidden','Standard',now(),now());
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,started_at,access_mode,auto_approve,
  accepting_participants,delivery_type,supervision_mode,quiz_result_policy,exam_version,
  created_at,updated_at)
values
  ('91300000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91200000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000010','A10ESS1','Finished',now()-interval '1 hour','PublicCloud',false,false,'FileSubmission','Standard','Hidden',1,now(),now()),
  ('91300000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91200000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000010','A10QUI1','Finished',now()-interval '1 hour','PublicCloud',false,false,'MultipleChoice','Standard','Hidden',1,now(),now()),
  ('91300000-0000-0000-0000-000000000003','91100000-0000-0000-0000-000000000001','91200000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000010','A10LAN1','Finished',now()-interval '1 hour','LanOnly',false,false,'FileSubmission','Standard','Hidden',1,now(),now()),
  ('92300000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','92200000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000010','A10OTH1','Finished',now()-interval '1 hour','PublicCloud',false,false,'FileSubmission','Standard','Hidden',1,now(),now());
insert into public.session_participants(
  id,organization_id,session_id,user_id,student_code,display_name,device_id,status,
  joined_at,download_status,submission_status,extra_time_minutes,resubmit_allowed,
  source_mode,created_at,updated_at)
values
  ('91400000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000001','91000000-0000-0000-0000-000000000002','A10-S1','A10 Owner','a10-owner-essay','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('91400000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000001','91000000-0000-0000-0000-000000000003','A10-S2','A10 Peer','a10-peer','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('91400000-0000-0000-0000-000000000003','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000002','91000000-0000-0000-0000-000000000002','A10-S1Q','A10 Owner','a10-owner-quiz','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now()),
  ('91400000-0000-0000-0000-000000000004','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000003','91000000-0000-0000-0000-000000000002','A10-S1L','A10 Owner','a10-owner-lan','Approved',now(),'Completed','Submitted',0,false,'Lan',now(),now()),
  ('92400000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','92300000-0000-0000-0000-000000000001','92000000-0000-0000-0000-000000000001','A10-X','A10 Other','a10-other','Approved',now(),'Completed','Submitted',0,false,'PublicCloud',now(),now());
insert into public.submissions(
  id,organization_id,session_id,participant_id,attempt_number,status,deadline_at,
  is_late,is_official,idempotency_key,source_mode,created_at,updated_at)
values
  ('91500000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000001','91400000-0000-0000-0000-000000000001',2,'Submitted',now(),false,true,'a10-own-returned','PublicCloud',now(),now()),
  ('91500000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000001','91400000-0000-0000-0000-000000000001',3,'Submitted',now(),false,true,'a10-own-graded','PublicCloud',now(),now()),
  ('91500000-0000-0000-0000-000000000003','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000001','91400000-0000-0000-0000-000000000002',1,'Submitted',now(),false,true,'a10-peer-returned','PublicCloud',now(),now()),
  ('91500000-0000-0000-0000-000000000004','91100000-0000-0000-0000-000000000001','91300000-0000-0000-0000-000000000003','91400000-0000-0000-0000-000000000004',1,'Submitted',now(),false,true,'a10-lan-returned','Lan',now(),now()),
  ('92500000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','92300000-0000-0000-0000-000000000001','92400000-0000-0000-0000-000000000001',1,'Submitted',now(),false,true,'a10-other-returned','PublicCloud',now(),now());
insert into public.grades(
  id,organization_id,submission_id,status,score,max_score,general_comment,graded_at,
  returned_at,revision,created_at,updated_at)
values
  ('91600000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91500000-0000-0000-0000-000000000001','Returned',8.25,10,'Essay returned','2026-08-02 09:00+00','2026-08-02 10:00+00',1,now(),now()),
  ('91600000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91500000-0000-0000-0000-000000000002','Graded',7,10,'Essay graded','2026-08-02 09:00+00',null,1,now(),now()),
  ('91600000-0000-0000-0000-000000000003','91100000-0000-0000-0000-000000000001','91500000-0000-0000-0000-000000000003','Returned',6,10,'Peer returned','2026-08-02 09:00+00','2026-08-02 10:00+00',1,now(),now()),
  ('91600000-0000-0000-0000-000000000004','91100000-0000-0000-0000-000000000001','91500000-0000-0000-0000-000000000004','Returned',5,10,'LAN returned','2026-08-02 09:00+00','2026-08-02 10:00+00',1,now(),now()),
  ('92600000-0000-0000-0000-000000000001','92100000-0000-0000-0000-000000000001','92500000-0000-0000-0000-000000000001','Returned',5,10,'Other returned','2026-08-02 09:00+00','2026-08-02 10:00+00',1,now(),now());
insert into public.graded_attachments(
  id,organization_id,grade_id,name,size_bytes,sha256,mime_type,cloud_object_path,created_at,updated_at)
values ('91700000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001',
  '91600000-0000-0000-0000-000000000001',E'C:\\private\\feedback.pdf',256,'a10-feedback-sha',
  'application/pdf','private/a10-feedback',now(),now());

insert into public.quiz_questions(
  id,organization_id,exam_id,version,sort_order,question_text,points,multiple,created_at,updated_at)
values ('91800000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001',
  '91200000-0000-0000-0000-000000000002',1,1,'A10 Q1',10,false,now(),now());
insert into public.quiz_choices(
  id,organization_id,question_id,sort_order,choice_text,is_correct,created_at,updated_at)
values
  ('91900000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001','91800000-0000-0000-0000-000000000001',1,'Correct',true,now(),now()),
  ('91900000-0000-0000-0000-000000000002','91100000-0000-0000-0000-000000000001','91800000-0000-0000-0000-000000000001',2,'Wrong',false,now(),now());
insert into public.quiz_attempts(
  id,organization_id,session_id,participant_id,attempt_number,exam_version,result_policy,status,
  started_at,deadline_at,finalized_at,auto_score,score,max_score,grading_status,general_comment,
  graded_at,returned_at,snapshot_json,source_mode,created_at,updated_at)
values ('91a00000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001',
  '91300000-0000-0000-0000-000000000002','91400000-0000-0000-0000-000000000003',3,1,'Hidden','Finalized',
  '2026-08-02 09:00+00','2026-08-02 10:00+00','2026-08-02 09:50+00',10,10,10,'Returned','Quiz returned',
  '2026-08-02 09:55+00','2026-08-02 10:00+00',
  '[{"id":"91800000-0000-0000-0000-000000000001","points":10,"multiple":false,"choices":[{"id":"91900000-0000-0000-0000-000000000001"},{"id":"91900000-0000-0000-0000-000000000002"}]}]'::jsonb,
  'PublicCloud',now(),now());
insert into public.quiz_answers(
  id,organization_id,attempt_id,question_id,choice_ids,revision,client_updated_at,source_mode,created_at,updated_at)
values ('91b00000-0000-0000-0000-000000000001','91100000-0000-0000-0000-000000000001',
  '91a00000-0000-0000-0000-000000000001','91800000-0000-0000-0000-000000000001',
  '["91900000-0000-0000-0000-000000000001"]'::jsonb,1,now(),'PublicCloud',now(),now());

create temporary table a10_state(key text primary key, payload jsonb) on commit drop;
grant select,insert,update on a10_state to authenticated;

set local role authenticated;
select set_config('request.jwt.claims','{"role":"authenticated"}',true);
select throws_ok($$select public.get_student_results()$$,
  '28000','AUTHENTICATION_REQUIRED','anonymous request is rejected');
select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select throws_ok($$select public.get_student_results()$$,
  '42501','ACTIVE_STUDENT_REQUIRED','teacher cannot use student results RPC');

select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
insert into a10_state values ('owner',public.get_student_results(50,null,null,null));
select is(pg_catalog.jsonb_array_length((select payload->'items' from a10_state where key='owner')),2,
  'owner receives only own Returned PublicCloud Essay and Quiz results');
select ok(not exists(select 1 from pg_catalog.jsonb_array_elements(
  (select payload->'items' from a10_state where key='owner')) item where item->>'status' <> 'Returned'),
  'student result page contains only Returned status');
select results_eq($$select item->>'resultType' from pg_catalog.jsonb_array_elements(
  (select payload->'items' from a10_state where key='owner')) item order by 1$$,
  $$values ('EssayFile'),('Quiz')$$,
  'EssayFile and Quiz are mapped explicitly');
select ok((select item->>'submissionId' is not null and item->>'attemptId' is null
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='EssayFile'),
  'Essay result uses SubmissionId only');
select ok((select item->>'attemptId' is not null and item->>'submissionId' is null
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='Quiz'),
  'Quiz result uses AttemptId only');
select ok((select item->>'attemptNumber'='2'
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='EssayFile') and (select item->>'attemptNumber'='3'
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='Quiz'),
  'Essay and Quiz use persisted authoritative attempt numbers');
select is((select item->'attachments'->0->>'fileName'
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='EssayFile'),'feedback.pdf',
  'Essay attachment exposes sanitized filename metadata');
select ok(position('private' in lower((select payload::text from a10_state where key='owner'))) = 0
  and position('cloud_object' in lower((select payload::text from a10_state where key='owner'))) = 0,
  'result payload exposes no physical path or cloud object key');
select ok((select item->'quizSummary'->>'totalQuestions'='1'
  and item->'quizSummary'->>'correctCount'='1'
  and (item->'quizSummary'->>'earnedPoints')::numeric=10
  from pg_catalog.jsonb_array_elements((select payload->'items' from a10_state where key='owner')) item
  where item->>'resultType'='Quiz'),
  'Quiz summary contains authoritative aggregate values');
select ok(position('answerkey' in lower((select payload::text from a10_state where key='owner'))) = 0
  and position('correctoption' in lower((select payload::text from a10_state where key='owner'))) = 0
  and position('choiceids' in lower((select payload::text from a10_state where key='owner'))) = 0,
  'Quiz result exposes no answer key or selected answer graph');
select ok(not ((select payload->'items' from a10_state where key='owner') @>
  '[{"submissionId":"91500000-0000-0000-0000-000000000002"}]'::jsonb),
  'Graded Essay is not visible');

select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000003","role":"authenticated"}',true);
select is(pg_catalog.jsonb_array_length(public.get_student_results()->'items'),1,
  'peer sees only the peer result, not owner results');
select set_config('request.jwt.claims','{"sub":"92000000-0000-0000-0000-000000000001","role":"authenticated"}',true);
select is(pg_catalog.jsonb_array_length(public.get_student_results()->'items'),1,
  'other organization is isolated to its own result');
select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000004","role":"authenticated"}',true);
select is(public.get_student_results()->'items','[]'::jsonb,
  'student with no Returned result receives an empty list');

select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
insert into a10_state values ('page1',public.get_student_results(1,null,null,null));
select ok(pg_catalog.jsonb_array_length((select payload->'items' from a10_state where key='page1'))=1
  and (select payload->'nextCursor' from a10_state where key='page1') is not null,
  'first bounded page returns an opaque stable cursor');
insert into a10_state
select 'page2',public.get_student_results(
  1,
  (payload->'nextCursor'->>'returnedAtUtc')::timestamptz,
  payload->'nextCursor'->>'resultType',
  (payload->'nextCursor'->>'resultId')::uuid)
from a10_state where key='page1';
select ok((select payload->'items'->0 from a10_state where key='page1') <>
  (select payload->'items'->0 from a10_state where key='page2')
  and (select payload->'nextCursor' from a10_state where key='page2') = 'null'::jsonb,
  'second page has no duplicate and terminates');
select is((select payload->'items'->0->>'resultType' from a10_state where key='page1'),'EssayFile',
  'equal timestamps use ResultType then stable ID as deterministic tie breakers');

reset role;
update public.grades set status='Graded',returned_at=null
where id='91600000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select is(pg_catalog.jsonb_array_length(public.get_student_results()->'items'),1,
  'reopened Essay disappears immediately');
reset role;
update public.grades set status='Returned',score=9,returned_at='2026-08-02 11:00+00'
where id='91600000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claims','{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',true);
select ok((public.get_student_results()->'items'->0->>'score')::numeric=9,
  'returned-again Essay appears with current authoritative data');
update public.grades set score=1
where id='91600000-0000-0000-0000-000000000001';
select is((select score from public.grades
  where id='91600000-0000-0000-0000-000000000001'),9::numeric,
  'student cannot mutate an Essay result');
update public.quiz_attempts set score=1
where id='91a00000-0000-0000-0000-000000000001';
select is((select (item->>'score')::numeric
  from pg_catalog.jsonb_array_elements(public.get_student_results()->'items') item
  where item->>'resultType'='Quiz'),10::numeric,
  'student cannot mutate a Quiz result');
select throws_ok($$select public.get_student_results(101,null,null,null)$$,
  '22023','STUDENT_RESULTS_PAGE_SIZE_INVALID','oversized page is rejected');
select throws_ok($$select public.get_student_results(10,now(),null,null)$$,
  '22023','STUDENT_RESULTS_CURSOR_INCOMPLETE','incomplete cursor is rejected');

select * from finish();
rollback;
