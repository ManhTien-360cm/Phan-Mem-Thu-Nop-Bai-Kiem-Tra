begin;

create or replace function public.get_public_exam_manifest(p_session_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
  v_session public.exam_sessions%rowtype;
begin
  select s.* into v_session
  from public.exam_sessions s
  join public.session_participants p
    on p.session_id = s.id
   and p.organization_id = s.organization_id
   and p.user_id = v_profile.id
   and p.status = 'Approved'
   and p.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('Distributing','InProgress','Paused','Collecting')
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1
          from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = v_profile.id
            and m.organization_id = s.organization_id
        )
        and exists (
          select 1
          from public.public_class_assignments a
          where a.class_id = s.class_id
            and a.exam_id = s.exam_id
            and a.organization_id = s.organization_id
            and (a.available_from is null or a.available_from <= pg_catalog.now())
            and (a.available_until is null or a.available_until >= pg_catalog.now())
        )
      )
    );
  if not found then
    raise exception 'PUBLIC_EXAM_MANIFEST_FORBIDDEN' using errcode = '42501';
  end if;

  return coalesce((
    select jsonb_agg(jsonb_build_object(
      'id', f.id,
      'name', f.name,
      'size_bytes', f.size_bytes,
      'sha256', lower(f.sha256),
      'mime_type', f.mime_type)
      order by f.name, f.id)
    from public.exam_files f
    where f.exam_id = v_session.exam_id
      and f.organization_id = v_session.organization_id
      and f.cloud_object_path is not null
  ), '[]'::jsonb);
end
$function$;

revoke all on function public.get_public_exam_manifest(uuid) from public, anon;
grant execute on function public.get_public_exam_manifest(uuid) to authenticated;

create or replace function public.get_public_exam_file_download(
  p_session_id uuid,
  p_file_id uuid)
returns table(object_path text, file_name text, size_bytes bigint, sha256 text)
language plpgsql
security definer
set search_path = ''
as $function$
declare
  v_profile public.profiles%rowtype := private.require_active_student();
begin
  return query
  select f.cloud_object_path, f.name, f.size_bytes, lower(f.sha256)
  from public.exam_files f
  join public.exams e
    on e.id = f.exam_id and e.organization_id = f.organization_id
  join public.exam_sessions s
    on s.exam_id = e.id and s.organization_id = e.organization_id
  join public.session_participants p
    on p.session_id = s.id
   and p.organization_id = s.organization_id
   and p.user_id = v_profile.id
   and p.status = 'Approved'
   and p.source_mode = 'PublicCloud'
  where s.id = p_session_id
    and f.id = p_file_id
    and s.organization_id = v_profile.organization_id
    and s.access_mode = 'PublicCloud'
    and s.status in ('Distributing','InProgress','Paused','Collecting')
    and (
      s.admission_mode = 'OpenRequest'
      or (
        s.admission_mode = 'ClassMembersOnly'
        and exists (
          select 1
          from public.class_members m
          where m.class_id = s.class_id
            and m.user_id = v_profile.id
            and m.organization_id = s.organization_id
        )
        and exists (
          select 1
          from public.public_class_assignments a
          where a.class_id = s.class_id
            and a.exam_id = s.exam_id
            and a.organization_id = s.organization_id
            and (a.available_from is null or a.available_from <= pg_catalog.now())
            and (a.available_until is null or a.available_until >= pg_catalog.now())
        )
      )
    )
    and f.cloud_object_path is not null;
  if not found then
    raise exception 'PUBLIC_EXAM_FILE_FORBIDDEN' using errcode = '42501';
  end if;
end
$function$;

revoke all on function public.get_public_exam_file_download(uuid,uuid) from public, anon;
grant execute on function public.get_public_exam_file_download(uuid,uuid) to authenticated;

commit;
