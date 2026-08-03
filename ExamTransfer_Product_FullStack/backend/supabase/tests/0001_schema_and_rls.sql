begin;

create extension if not exists pgtap with schema extensions;
select plan(15);

select has_table('public', 'examtransfer_cloud_meta', 'cloud metadata table exists');
select has_table('public', 'profiles', 'profiles table exists');
select has_table('public', 'classes', 'classes table exists');
select has_table('public', 'exam_files', 'exam files table exists');
select has_table('public', 'submission_files', 'submission files table exists');
select has_table('public', 'backups', 'backups table exists');
select is((select schema_version from public.examtransfer_cloud_meta where id = 1), 26, 'schema version is 26');
select ok((select relrowsecurity from pg_class where oid = 'public.profiles'::regclass), 'profiles RLS enabled');
select ok((select relrowsecurity from pg_class where oid = 'public.classes'::regclass), 'classes RLS enabled');
select ok((select relrowsecurity from pg_class where oid = 'public.audit_logs'::regclass), 'audit RLS enabled');
select policies_are('storage', 'objects', array[
  'examtransfer_storage_delete',
  'examtransfer_storage_insert',
  'examtransfer_storage_staff_select',
  'examtransfer_storage_update',
  'examtransfer_public_submission_owner_insert',
  'examtransfer_public_submission_owner_select'
], 'ExamTransfer storage policies installed');
select has_function('public', 'current_organization_id', array[]::text[], 'tenant helper exists');
select has_function('public', 'current_examtransfer_role', array[]::text[], 'role helper exists');
select has_function('public', 'bootstrap_examtransfer_organization', array['text','text'], 'bootstrap helper exists');

select has_check(
  'public',
  'profiles',
  'profiles role constraint exists');

select * from finish();
rollback;
