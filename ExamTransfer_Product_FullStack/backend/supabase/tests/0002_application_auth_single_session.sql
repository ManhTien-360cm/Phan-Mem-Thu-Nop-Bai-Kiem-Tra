begin;

create extension if not exists pgtap with schema extensions;
select plan(12);

select has_column('public', 'profiles', 'username', 'profiles.username exists');
select has_column('public', 'profiles', 'student_code', 'profiles.student_code exists');
select has_column('public', 'profiles', 'is_active', 'profiles.is_active exists');
select has_column('public', 'profiles', 'last_login_at', 'profiles.last_login_at exists');
select has_table('public', 'user_login_sessions', 'login session table exists');
select has_column('public', 'user_login_sessions', 'token_hash', 'login session token hash exists');
select has_column('public', 'user_login_sessions', 'encrypted_refresh_token', 'encrypted refresh token column exists');
select ok((select relrowsecurity from pg_class where oid = 'public.user_login_sessions'::regclass), 'login session RLS enabled');
select has_function('public', 'claim_examtransfer_login_session', array['uuid','text','text','inet','text','text','integer'], 'claim login RPC exists');
select has_function('public', 'heartbeat_examtransfer_login_session', array['uuid','uuid','text','integer'], 'heartbeat login RPC exists');
select has_function('public', 'release_examtransfer_login_session', array['uuid','uuid','text','text'], 'release login RPC exists');
select is((select schema_version from public.examtransfer_cloud_meta where id = 1), 26, 'schema version is 26');

select * from finish();
rollback;
