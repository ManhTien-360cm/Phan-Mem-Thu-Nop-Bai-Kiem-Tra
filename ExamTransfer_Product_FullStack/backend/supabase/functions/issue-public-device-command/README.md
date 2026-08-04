# issue-public-device-command

The teacher desktop calls this authenticated Edge Function. The function
validates the caller through Supabase Auth, signs the immutable command envelope
with `EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET`, then invokes the
service-role-only `issue_public_device_command` RPC.

Required server-side secrets: `SUPABASE_URL`, `SUPABASE_ANON_KEY`,
`SUPABASE_SERVICE_ROLE_KEY`, and an HMAC secret of at least 32 characters. Never
place the HMAC or service-role secret in desktop configuration, logs, test
fixtures, or Realtime payloads.
