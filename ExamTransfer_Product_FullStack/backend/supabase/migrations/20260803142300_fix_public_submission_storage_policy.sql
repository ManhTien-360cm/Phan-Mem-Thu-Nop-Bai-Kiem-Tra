-- Qualify the outer storage object inside nested policy subqueries. Without
-- qualification, PostgreSQL resolves `name` to submission_files.name, so a
-- valid initialized PublicCloud archive is rejected by RLS during upload.
drop policy if exists examtransfer_public_submission_owner_select on storage.objects;
drop policy if exists examtransfer_public_submission_owner_insert on storage.objects;

create policy examtransfer_public_submission_owner_select
on storage.objects
for select
to authenticated
using (
  storage.objects.bucket_id = 'public-submission-archives'
  and storage.objects.owner_id = (select auth.uid())::text
  and (storage.foldername(storage.objects.name))[1] = (select public.current_organization_id())::text
  and (storage.foldername(storage.objects.name))[2] = 'public-submissions'
  and (storage.foldername(storage.objects.name))[3] = (select auth.uid())::text
  and array_length(storage.foldername(storage.objects.name), 1) = 4
  and exists (
    select 1
    from public.submissions s
    join public.session_participants p on p.id = s.participant_id
    where s.id::text = (storage.foldername(storage.objects.name))[4]
      and s.organization_id = (select public.current_organization_id())
      and s.source_mode = 'PublicCloud'
      and p.user_id = (select auth.uid())
  )
);

create policy examtransfer_public_submission_owner_insert
on storage.objects
for insert
to authenticated
with check (
  storage.objects.bucket_id = 'public-submission-archives'
  and storage.objects.owner_id = (select auth.uid())::text
  and (storage.foldername(storage.objects.name))[1] = (select public.current_organization_id())::text
  and (storage.foldername(storage.objects.name))[2] = 'public-submissions'
  and (storage.foldername(storage.objects.name))[3] = (select auth.uid())::text
  and array_length(storage.foldername(storage.objects.name), 1) = 4
  and exists (
    select 1
    from public.submission_files f
    join public.submissions s on s.id = f.submission_id
    join public.session_participants p on p.id = s.participant_id
    where s.id::text = (storage.foldername(storage.objects.name))[4]
      and f.cloud_object_path = storage.objects.name
      and f.archive_signature_verified = false
      and s.status = 'Uploading'
      and s.source_mode = 'PublicCloud'
      and p.user_id = (select auth.uid())
  )
);
