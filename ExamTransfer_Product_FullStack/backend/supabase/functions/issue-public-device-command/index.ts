import {
  canonicalDeviceCommand,
  hmacHex,
} from "../_shared/device-command-signature.ts";

const corsHeaders = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers":
    "authorization, apikey, content-type, x-client-info",
  "access-control-allow-methods": "POST, OPTIONS",
};
const jsonHeaders = {
  ...corsHeaders,
  "content-type": "application/json; charset=utf-8",
};
const uuidPattern =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

type CommandRequest = {
  sessionId: string;
  deviceId: string;
  commandType: string;
  payload?: unknown;
  ttlSeconds?: number;
};

export async function handler(request: Request): Promise<Response> {
  if (request.method === "OPTIONS") {
    return new Response(null, { status: 204, headers: corsHeaders });
  }
  if (request.method !== "POST") {
    return new Response(JSON.stringify({ error: "METHOD_NOT_ALLOWED" }), {
      status: 405,
      headers: jsonHeaders,
    });
  }

  const supabaseUrl = Deno.env.get("SUPABASE_URL");
  const publishableKey = Deno.env.get("SUPABASE_ANON_KEY");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  const signingSecret = Deno.env.get("EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET");
  const authorization = request.headers.get("authorization");
  if (
    !supabaseUrl || !publishableKey || !serviceRoleKey || !signingSecret ||
    signingSecret.length < 32
  ) {
    return new Response(JSON.stringify({ error: "SERVER_NOT_CONFIGURED" }), {
      status: 503,
      headers: jsonHeaders,
    });
  }
  if (!authorization?.toLowerCase().startsWith("bearer ")) {
    return new Response(JSON.stringify({ error: "AUTHENTICATION_REQUIRED" }), {
      status: 401,
      headers: jsonHeaders,
    });
  }

  let body: CommandRequest;
  try {
    body = await request.json();
  } catch {
    return new Response(JSON.stringify({ error: "INVALID_JSON" }), {
      status: 400,
      headers: jsonHeaders,
    });
  }
  if (
    !body.sessionId ||
    !uuidPattern.test(body.sessionId) ||
    !body.deviceId?.trim() ||
    body.deviceId.length > 200 ||
    !body.commandType?.trim() ||
    body.commandType.length > 100
  ) {
    return new Response(JSON.stringify({ error: "INVALID_REQUEST" }), {
      status: 400,
      headers: jsonHeaders,
    });
  }

  const userResponse = await fetch(`${supabaseUrl}/auth/v1/user`, {
    headers: { authorization, apikey: publishableKey },
  });
  if (!userResponse.ok) {
    return new Response(JSON.stringify({ error: "INVALID_USER_SESSION" }), {
      status: 401,
      headers: jsonHeaders,
    });
  }
  const user = await userResponse.json() as { id?: string };
  if (!user.id) {
    return new Response(JSON.stringify({ error: "INVALID_USER_SESSION" }), {
      status: 401,
      headers: jsonHeaders,
    });
  }

  const payload = body.payload ?? {};
  const ttlSeconds = Math.max(
    5,
    Math.min(900, Math.trunc(body.ttlSeconds ?? 60)),
  );
  const commandId = crypto.randomUUID();
  const createdAt = new Date();
  const expiresAt = new Date(createdAt.getTime() + ttlSeconds * 1000);
  const canonical = canonicalDeviceCommand({
    commandId,
    sessionId: body.sessionId,
    deviceId: body.deviceId,
    commandType: body.commandType,
    payload,
    createdAt,
    expiresAt,
    issuedBy: user.id,
  });
  const signature = await hmacHex(signingSecret, canonical);

  const rpcResponse = await fetch(
    `${supabaseUrl}/rest/v1/rpc/issue_public_device_command`,
    {
      method: "POST",
      headers: {
        ...jsonHeaders,
        authorization: `Bearer ${serviceRoleKey}`,
        apikey: serviceRoleKey,
      },
      body: JSON.stringify({
        p_command_id: commandId,
        p_session_id: body.sessionId,
        p_device_id: body.deviceId,
        p_command_type: body.commandType,
        p_payload: payload,
        p_created_at: createdAt.toISOString(),
        p_expires_at: expiresAt.toISOString(),
        p_issued_by: user.id,
        p_signature: signature,
      }),
    },
  );
  if (!rpcResponse.ok) {
    return new Response(JSON.stringify({ error: "COMMAND_REJECTED" }), {
      status: 403,
      headers: jsonHeaders,
    });
  }
  return new Response(JSON.stringify({ commandId, createdAt, expiresAt }), {
    status: 201,
    headers: jsonHeaders,
  });
}

if (import.meta.main) Deno.serve(handler);
