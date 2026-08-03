const corsHeaders = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "authorization, apikey, content-type, x-client-info",
  "access-control-allow-methods": "POST, OPTIONS",
};
const jsonHeaders = { ...corsHeaders, "content-type": "application/json; charset=utf-8" };
const signedUrlLifetimeSeconds = 180;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export async function handler(request: Request): Promise<Response> {
  if (request.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: corsHeaders });
  }
  if (request.method !== "POST") {
    return new Response(JSON.stringify({ error: "METHOD_NOT_ALLOWED" }), { status: 405, headers: jsonHeaders });
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const publishableKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  const authorization = request.headers.get("authorization");
  if (!supabaseUrl || !publishableKey || !serviceRoleKey) {
    return new Response(JSON.stringify({ error: "SERVER_NOT_CONFIGURED" }), { status: 503, headers: jsonHeaders });
  }
  if (!authorization?.toLowerCase().startsWith("bearer ")) {
    return new Response(JSON.stringify({ error: "AUTHENTICATION_REQUIRED" }), { status: 401, headers: jsonHeaders });
  }

  let body: { sessionId?: string; fileId?: string };
  try { body = await request.json(); }
  catch { return new Response(JSON.stringify({ error: "INVALID_JSON" }), { status: 400, headers: jsonHeaders }); }
  if (!body.sessionId || !body.fileId || !uuidPattern.test(body.sessionId) || !uuidPattern.test(body.fileId)) {
    return new Response(JSON.stringify({ error: "INVALID_REQUEST" }), { status: 400, headers: jsonHeaders });
  }

  // This user-token RPC is the authorization boundary. It validates an active
  // Student profile, tenant, membership, assignment, file, participant and
  // active PublicCloud session before revealing the immutable object path.
  const metadataResponse = await fetch(`${supabaseUrl}/rest/v1/rpc/get_public_exam_file_download`, {
    method: "POST",
    headers: { ...jsonHeaders, authorization, apikey: publishableKey },
    body: JSON.stringify({ p_session_id: body.sessionId, p_file_id: body.fileId }),
  });
  if (!metadataResponse.ok) {
    return new Response(JSON.stringify({ error: "PUBLIC_EXAM_FILE_FORBIDDEN" }), { status: 403, headers: jsonHeaders });
  }
  const rows = await metadataResponse.json() as Array<{
    object_path: string;
    file_name: string;
    size_bytes: number;
    sha256: string;
  }>;
  if (rows.length !== 1 || !rows[0].object_path) {
    return new Response(JSON.stringify({ error: "PUBLIC_EXAM_FILE_NOT_FOUND" }), { status: 404, headers: jsonHeaders });
  }
  const file = rows[0];
  const encodedPath = file.object_path.split("/").map(encodeURIComponent).join("/");
  const signResponse = await fetch(`${supabaseUrl}/storage/v1/object/sign/exam-archives/${encodedPath}`, {
    method: "POST",
    headers: {
      ...jsonHeaders,
      authorization: `Bearer ${serviceRoleKey}`,
      apikey: serviceRoleKey,
    },
    body: JSON.stringify({ expiresIn: signedUrlLifetimeSeconds }),
  });
  if (!signResponse.ok) {
    return new Response(JSON.stringify({ error: "SIGNED_URL_FAILED" }), { status: 502, headers: jsonHeaders });
  }
  const signed = await signResponse.json() as { signedURL?: string; signedUrl?: string };
  const relativeUrl = signed.signedURL ?? signed.signedUrl;
  if (!relativeUrl) {
    return new Response(JSON.stringify({ error: "SIGNED_URL_INVALID" }), { status: 502, headers: jsonHeaders });
  }
  const url = relativeUrl.startsWith("http") ? relativeUrl : `${supabaseUrl}/storage/v1${relativeUrl}`;
  return new Response(JSON.stringify({
    url,
    expiresIn: signedUrlLifetimeSeconds,
    fileName: file.file_name,
    sizeBytes: file.size_bytes,
    sha256: file.sha256,
  }), { status: 200, headers: jsonHeaders });
}

if (import.meta.main) Deno.serve(handler);
