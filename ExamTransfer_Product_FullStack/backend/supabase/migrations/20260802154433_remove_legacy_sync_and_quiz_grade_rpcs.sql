do $migration$
declare
  v_canonical regprocedure := pg_catalog.to_regprocedure(
    'public.save_public_quiz_grade(uuid,numeric,text,bigint,uuid)');
begin
  if v_canonical is null
     or pg_catalog.pg_get_function_result(v_canonical) <> 'jsonb' then
    raise exception 'CANONICAL_SAVE_PUBLIC_QUIZ_GRADE_REQUIRED'
      using errcode = 'P0002';
  end if;

  if exists (
    select 1
    from pg_catalog.pg_proc p
    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public'
      and p.proname = 'rpc_pull_exam_changes'
      and p.oid <> coalesce(
        pg_catalog.to_regprocedure(
          'public.rpc_pull_exam_changes(timestamp with time zone,uuid)')::oid,
        0::oid)) then
    raise exception 'UNEXPECTED_RPC_PULL_EXAM_CHANGES_SIGNATURE'
      using errcode = 'P0001';
  end if;

  if exists (
    select 1
    from pg_catalog.pg_proc p
    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public'
      and p.proname = 'rpc_push_local_events'
      and p.oid <> coalesce(
        pg_catalog.to_regprocedure('public.rpc_push_local_events(jsonb[])')::oid,
        0::oid)) then
    raise exception 'UNEXPECTED_RPC_PUSH_LOCAL_EVENTS_SIGNATURE'
      using errcode = 'P0001';
  end if;

  if exists (
    select 1
    from pg_catalog.pg_proc p
    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public'
      and p.proname = 'save_public_quiz_grade'
      and p.oid not in (
        v_canonical::oid,
        coalesce(
          pg_catalog.to_regprocedure(
            'public.save_public_quiz_grade(uuid,uuid,text,timestamp with time zone,jsonb)')::oid,
          0::oid))) then
    raise exception 'UNEXPECTED_SAVE_PUBLIC_QUIZ_GRADE_SIGNATURE'
      using errcode = 'P0001';
  end if;
end
$migration$;

drop function if exists public.rpc_pull_exam_changes(
  timestamp with time zone,
  uuid
);

drop function if exists public.rpc_push_local_events(
  jsonb[]
);

drop function if exists public.save_public_quiz_grade(
  uuid,
  uuid,
  text,
  timestamp with time zone,
  jsonb
);
