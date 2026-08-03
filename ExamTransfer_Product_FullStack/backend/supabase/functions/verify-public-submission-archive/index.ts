const corsHeaders = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, apikey, content-type, x-client-info",
  "access-control-allow-methods": "POST, OPTIONS",
};
const jsonHeaders = { ...corsHeaders, "content-type": "application/json; charset=utf-8" };
const maxBytes = 10 * 1024 * 1024;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function detectMagic(name: string, bytes: Uint8Array): string | null {
  const extension = name.toLowerCase().split(".").pop();
  const starts = (...signature: number[]) => signature.every((value, index) => bytes[index] === value);
  if (extension === "zip") {
    return starts(0x50, 0x4b, 0x03, 0x04) || starts(0x50, 0x4b, 0x05, 0x06) || starts(0x50, 0x4b, 0x07, 0x08) ? "zip" : null;
  }
  if (extension === "rar") return starts(0x52, 0x61, 0x72, 0x21, 0x1a, 0x07) ? "rar" : null;
  if (extension === "7z") return starts(0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c) ? "7z" : null;
  return null;
}

function hex(bytes: Uint8Array): string {
  return [...bytes].map((part) => part.toString(16).padStart(2, "0")).join("");
}

export async function handler(request: Request): Promise<Response> {
  if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: corsHeaders });
  if (request.method !== "POST") return new Response(JSON.stringify({ error: "METHOD_NOT_ALLOWED" }), { status: 405, headers: jsonHeaders });
  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const publishableKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  const authorization = request.headers.get("authorization");
  if (!supabaseUrl || !publishableKey || !serviceRoleKey) return new Response(JSON.stringify({ error: "SERVER_NOT_CONFIGURED" }), { status: 503, headers: jsonHeaders });
  if (!authorization?.toLowerCase().startsWith("bearer ")) return new Response(JSON.stringify({ error: "AUTHENTICATION_REQUIRED" }), { status: 401, headers: jsonHeaders });

  let body: { submissionId?: string; idempotencyKey?: string };
  try { body = await request.json(); }
  catch { return new Response(JSON.stringify({ error: "INVALID_JSON" }), { status: 400, headers: jsonHeaders }); }
  if (!body.submissionId
    || !uuidPattern.test(body.submissionId)
    || !body.idempotencyKey?.trim()
    || body.idempotencyKey.length > 200) return new Response(JSON.stringify({ error: "INVALID_REQUEST" }), { status: 400, headers: jsonHeaders });

  const selectUrl = `${supabaseUrl}/rest/v1/submission_files?submission_id=eq.${encodeURIComponent(body.submissionId)}&select=id,name,size_bytes,sha256,cloud_object_path,archive_signature_verified`;
  const metadataResponse = await fetch(selectUrl, { headers: { authorization, apikey: publishableKey } });
  if (!metadataResponse.ok) return new Response(JSON.stringify({ error: "SUBMISSION_FILE_FORBIDDEN" }), { status: 403, headers: jsonHeaders });
  const rows = await metadataResponse.json() as Array<{ id: string; name: string; size_bytes: number; sha256: string; cloud_object_path: string; archive_signature_verified: boolean }>;
  if (rows.length !== 1 || rows[0].size_bytes <= 0 || rows[0].size_bytes > maxBytes) return new Response(JSON.stringify({ error: "SUBMISSION_FILE_INVALID" }), { status: 422, headers: jsonHeaders });
  const file = rows[0];

  if (!file.archive_signature_verified) {
    const objectPath = file.cloud_object_path.split("/").map(encodeURIComponent).join("/");
    const objectResponse = await fetch(`${supabaseUrl}/storage/v1/object/authenticated/public-submission-archives/${objectPath}`, {
      headers: { authorization: `Bearer ${serviceRoleKey}`, apikey: serviceRoleKey },
    });
    if (!objectResponse.ok) return new Response(JSON.stringify({ error: "ARCHIVE_OBJECT_NOT_FOUND" }), { status: 404, headers: jsonHeaders });
    const contentLength = Number(objectResponse.headers.get("content-length") ?? "0");
    if (contentLength > maxBytes) return new Response(JSON.stringify({ error: "SUBMISSION_TOO_LARGE" }), { status: 422, headers: jsonHeaders });
    const archive = new Uint8Array(await objectResponse.arrayBuffer());
    const magicType = detectMagic(file.name, archive);
    if (archive.byteLength !== file.size_bytes || archive.byteLength > maxBytes || !magicType) {
      return new Response(JSON.stringify({ error: "ARCHIVE_SIGNATURE_INVALID" }), { status: 422, headers: jsonHeaders });
    }
    const actualSha256 = hex(new Uint8Array(await crypto.subtle.digest("SHA-256", archive)));
    if (actualSha256 !== file.sha256.toLowerCase()) return new Response(JSON.stringify({ error: "ARCHIVE_HASH_MISMATCH" }), { status: 422, headers: jsonHeaders });

    const verifyResponse = await fetch(`${supabaseUrl}/rest/v1/rpc/verify_public_submission_archive`, {
      method: "POST",
      headers: {
        ...jsonHeaders,
        authorization: `Bearer ${serviceRoleKey}`,
        apikey: serviceRoleKey,
      },
      body: JSON.stringify({
        p_submission_id: body.submissionId,
        p_file_id: file.id,
        p_observed_sha256: actualSha256,
        p_observed_size: archive.byteLength,
        p_magic_type: magicType,
      }),
    });
    if (!verifyResponse.ok) return new Response(JSON.stringify({ error: "ARCHIVE_VERIFICATION_UPDATE_FAILED" }), { status: 502, headers: jsonHeaders });
  }

  const finalizeResponse = await fetch(`${supabaseUrl}/rest/v1/rpc/finalize_public_submission`, {
    method: "POST",
    headers: { ...jsonHeaders, authorization, apikey: publishableKey },
    body: JSON.stringify({ p_submission_id: body.submissionId, p_idempotency_key: body.idempotencyKey }),
  });
  if (!finalizeResponse.ok) return new Response(JSON.stringify({ error: "SUBMISSION_FINALIZE_REJECTED" }), { status: 409, headers: jsonHeaders });
  const receiptCode = await finalizeResponse.json();
  return new Response(JSON.stringify({ receiptCode }), { status: 200, headers: jsonHeaders });
}

if (import.meta.main) Deno.serve(handler);
