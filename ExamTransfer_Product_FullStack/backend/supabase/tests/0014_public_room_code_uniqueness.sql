begin;
select plan(14);

select ok(exists(
  select 1
  from pg_catalog.pg_index i
  join pg_catalog.pg_class c on c.oid = i.indexrelid
  where c.oid = 'public.ux_exam_sessions_active_public_room'::regclass
    and i.indisunique),
  'active PublicCloud room index exists and is unique');

select ok(position('upper(btrim(room_code))' in lower(pg_get_indexdef(
  'public.ux_exam_sessions_active_public_room'::regclass))) > 0,
  'active PublicCloud room key normalizes stored room codes');

select ok((
  select position('access_mode' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
    and position('publiccloud' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
    and position('admission_mode' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
    and position('openrequest' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
    and position('waiting' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
    and position('accepting_participants' in lower(pg_get_expr(i.indpred, i.indrelid))) > 0
  from pg_catalog.pg_index i
  where i.indexrelid = 'public.ux_exam_sessions_active_public_room'::regclass),
  'active PublicCloud room index has the exact eligibility predicate');

insert into auth.users(id,email) values
  ('91000000-0000-0000-0000-000000000001','a01-teacher@example.test'),
  ('91000000-0000-0000-0000-000000000002','a01-student@example.test'),
  ('92000000-0000-0000-0000-000000000001','a01-other-teacher@example.test')
on conflict (id) do nothing;

insert into public.organizations(id,name) values
  ('91000000-0000-0000-0000-000000000000','A01 Organization'),
  ('92000000-0000-0000-0000-000000000000','A01 Other Organization');

insert into public.profiles(
  id,organization_id,display_name,role,username,student_code,is_active,date_of_birth)
values
  ('91000000-0000-0000-0000-000000000001','91000000-0000-0000-0000-000000000000',
   'A01 Teacher','Teacher','a01-teacher',null,true,null),
  ('91000000-0000-0000-0000-000000000002','91000000-0000-0000-0000-000000000000',
   'A01 Student','Student','A0101','A0101',true,'2008-01-01'),
  ('92000000-0000-0000-0000-000000000001','92000000-0000-0000-0000-000000000000',
   'A01 Other Teacher','Teacher','a01-other-teacher',null,true,null);

insert into public.exams(
  id,organization_id,class_id,title,subject,duration_minutes,status,version,created_by,
  delivery_type,supervision_mode,quiz_result_policy,created_at,updated_at)
values
  ('91100000-0000-0000-0000-000000000001','91000000-0000-0000-0000-000000000000',null,
   'A01 Exam','IT',60,'Published',1,'91000000-0000-0000-0000-000000000001',
   'FileSubmission','None','Hidden',now(),now()),
  ('92100000-0000-0000-0000-000000000001','92000000-0000-0000-0000-000000000000',null,
   'A01 Other Exam','IT',60,'Published',1,'92000000-0000-0000-0000-000000000001',
   'FileSubmission','None','Hidden',now(),now());

insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values (
  '91200000-0000-0000-0000-000000000001','91000000-0000-0000-0000-000000000000',
  '91100000-0000-0000-0000-000000000001',null,'AbC123','Waiting','PublicCloud',
  false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now());

create temporary table a01_results(
  key text primary key,
  passed boolean not null,
  constraint_name text,
  value jsonb)
on commit drop;
grant select,insert,update on a01_results to authenticated;

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
insert into a01_results(key,passed,value) values (
  'normalized-join',
  true,
  public.join_open_public_session_by_room_code(
    'abc123','a01-device','student-machine','1.0.0','{}'));
select is((select value->>'sessionId' from a01_results where key='normalized-join'),
  '91200000-0000-0000-0000-000000000001',
  'normalized input joins the one valid PublicCloud room');
select throws_ok($$select public.join_open_public_session_by_room_code(
  'MISSING','a01-device','student-machine','1.0.0','{}')$$,
  'P0002','OPEN_PUBLIC_SESSION_NOT_FOUND',
  'missing normalized room returns P0002');
reset role;

do $conflict$
declare
  v_constraint text;
begin
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000002','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Waiting','PublicCloud',
    false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now());
  insert into a01_results(key,passed) values ('same-org-conflict',false);
exception when unique_violation then
  get stacked diagnostics v_constraint = constraint_name;
  insert into a01_results(key,passed,constraint_name)
  values ('same-org-conflict',true,v_constraint);
end
$conflict$;
select ok((select passed from a01_results where key='same-org-conflict'),
  'same organization rejects abc123, ABC123 and AbC123 as one active key');
select is((select constraint_name from a01_results where key='same-org-conflict'),
  'ux_exam_sessions_active_public_room',
  'normalized collision is rejected by the named unique invariant');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '92200000-0000-0000-0000-000000000001','92000000-0000-0000-0000-000000000000',
    '92100000-0000-0000-0000-000000000001',null,'ABC123','Waiting','PublicCloud',
    false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'same normalized code in another organization is allowed');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000003','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Waiting','LanOnly',
    false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'OnlyLAN room with the same normalized code is allowed');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000004','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Finished','PublicCloud',
    false,false,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'Finished PublicCloud room does not occupy the active key');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000005','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Cancelled','PublicCloud',
    false,false,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'Cancelled PublicCloud room does not occupy the active key');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000006','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Archived','PublicCloud',
    false,false,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'Archived PublicCloud room does not occupy the active key');

select lives_ok($$
  insert into public.exam_sessions(
    id,organization_id,exam_id,class_id,room_code,status,access_mode,
    auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
    quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
  values (
    '91200000-0000-0000-0000-000000000007','91000000-0000-0000-0000-000000000000',
    '91100000-0000-0000-0000-000000000001',null,'ABC123','Waiting','PublicCloud',
    false,false,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now())
$$,'non-accepting Waiting PublicCloud room does not occupy the active key');

drop index public.ux_exam_sessions_active_public_room;
insert into public.exam_sessions(
  id,organization_id,exam_id,class_id,room_code,status,access_mode,
  auto_approve,accepting_participants,capacity,delivery_type,supervision_mode,
  quiz_result_policy,exam_version,admission_mode,created_at,updated_at)
values (
  '91200000-0000-0000-0000-000000000008','91000000-0000-0000-0000-000000000000',
  '91100000-0000-0000-0000-000000000001',null,'ABC123','Waiting','PublicCloud',
  false,true,36,'FileSubmission','None','Hidden',1,'OpenRequest',now(),now());

set local role authenticated;
select set_config(
  'request.jwt.claims',
  '{"sub":"91000000-0000-0000-0000-000000000002","role":"authenticated"}',
  true);
select throws_ok($$select public.join_open_public_session_by_room_code(
  'abc123','a01-device','student-machine','1.0.0','{}')$$,
  'P0003','OPEN_PUBLIC_ROOM_CODE_AMBIGUOUS',
  'RPC retains P0003 as a defensive legacy ambiguity guard');

reset role;
select * from finish();
rollback;
