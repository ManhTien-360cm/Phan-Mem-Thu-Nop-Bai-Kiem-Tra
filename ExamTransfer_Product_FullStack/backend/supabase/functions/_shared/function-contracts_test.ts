import { handler as getExamFileUrl } from "../get-public-exam-file-url/index.ts";
import { handler as issueDeviceCommand } from "../issue-public-device-command/index.ts";
import { handler as verifySubmission } from "../verify-public-submission-archive/index.ts";

const handlers = [
  ["get-public-exam-file-url", getExamFileUrl],
  ["issue-public-device-command", issueDeviceCommand],
  ["verify-public-submission-archive", verifySubmission],
] as const;

const sessionId = "61300000-0000-4000-8000-000000000001";
const fileId = "61400000-0000-4000-8000-000000000001";
const submissionId = "61500000-0000-4000-8000-000000000001";
const userId = "61000000-0000-4000-8000-000000000001";
const authorization = "Bearer user-session-fixture";
const functionEnvironment = {
  SUPABASE_URL: "http://supabase.local",
  SUPABASE_ANON_KEY: "publishable-fixture",
  SUPABASE_SERVICE_ROLE_KEY: "server-only-fixture",
  EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET:
    "hmac-fixture-at-least-thirty-two-characters",
} as const;

type FetchLike = (
  input: RequestInfo | URL,
  init?: RequestInit,
) => Promise<Response>;

async function withFunctionEnvironment<T>(
  action: () => Promise<T>,
): Promise<T> {
  const previous = new Map<string, string | undefined>();
  for (const [key, value] of Object.entries(functionEnvironment)) {
    previous.set(key, Deno.env.get(key));
    Deno.env.set(key, value);
  }
  try {
    return await action();
  } finally {
    for (const [key, value] of previous) {
      if (value === undefined) Deno.env.delete(key);
      else Deno.env.set(key, value);
    }
  }
}

async function withMockFetch<T>(
  mock: FetchLike,
  action: () => Promise<T>,
): Promise<T> {
  const original = globalThis.fetch;
  globalThis.fetch = mock as typeof fetch;
  try {
    return await action();
  } finally {
    globalThis.fetch = original;
  }
}

function post(body: unknown, withAuthorization = true): Request {
  const headers = new Headers({ "content-type": "application/json" });
  if (withAuthorization) headers.set("authorization", authorization);
  return new Request("http://localhost", {
    method: "POST",
    headers,
    body: JSON.stringify(body),
  });
}

async function expectError(
  response: Response,
  status: number,
  error: string,
): Promise<void> {
  if (response.status !== status) {
    throw new Error(`unexpected status ${response.status}`);
  }
  const payload = await response.json();
  if (payload.error !== error) {
    throw new Error(`unexpected error ${payload.error}`);
  }
  const serialized = JSON.stringify(payload);
  if (
    serialized.includes(functionEnvironment.SUPABASE_SERVICE_ROLE_KEY) ||
    serialized.includes(
      functionEnvironment.EXAMTRANSFER_DEVICE_COMMAND_HMAC_SECRET,
    )
  ) {
    throw new Error("server credential leaked in response");
  }
}

for (const [name, handler] of handlers) {
  Deno.test(`${name} accepts CORS preflight`, async () => {
    const response = await handler(
      new Request("http://localhost", { method: "OPTIONS" }),
    );
    if (response.status !== 204) {
      throw new Error(`unexpected status ${response.status}`);
    }
    if (response.headers.get("access-control-allow-origin") !== "*") {
      throw new Error("missing CORS origin");
    }
    if (
      !response.headers.get("access-control-allow-methods")?.includes("POST")
    ) {
      throw new Error("missing CORS method");
    }
  });
}

Deno.test("all functions reject unsupported methods", async () => {
  for (const [name, handler] of handlers) {
    const response = await handler(
      new Request("http://localhost", { method: "GET" }),
    );
    if (response.status !== 405) {
      throw new Error(`${name} returned ${response.status}`);
    }
  }
});

Deno.test("all functions reject malformed authenticated payloads", async () => {
  await withFunctionEnvironment(async () => {
    for (const [name, handler] of handlers) {
      const response = await handler(post({}));
      if (response.status !== 400) {
        throw new Error(`${name} returned ${response.status}`);
      }
      const payload = await response.json();
      if (payload.error !== "INVALID_REQUEST") {
        throw new Error(`${name} returned an unexpected error`);
      }
    }
  });
});

Deno.test("configured functions reject requests without a user JWT", async () => {
  await withFunctionEnvironment(async () => {
    const bodies = [
      { sessionId, fileId },
      { sessionId, deviceId: "device-fixture", commandType: "LOCK" },
      { submissionId, idempotencyKey: "request-fixture" },
    ];
    for (let index = 0; index < handlers.length; index++) {
      await expectError(
        await handlers[index][1](post(bodies[index], false)),
        401,
        "AUTHENTICATION_REQUIRED",
      );
    }
  });
});

Deno.test("exam file function denies an unauthorized resource before signing", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input, init) => {
      const url = input.toString();
      if (!url.endsWith("/rest/v1/rpc/get_public_exam_file_download")) {
        throw new Error(`unexpected request ${url}`);
      }
      if (new Headers(init?.headers).get("authorization") !== authorization) {
        throw new Error("authorization RPC did not receive the user JWT");
      }
      return new Response(JSON.stringify({ message: "forbidden fixture" }), {
        status: 403,
      });
    }, async () => {
      await expectError(
        await getExamFileUrl(post({ sessionId, fileId })),
        403,
        "PUBLIC_EXAM_FILE_FORBIDDEN",
      );
    })
  );
});

Deno.test("exam file function returns not found without invoking storage", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input) => {
      const url = input.toString();
      if (!url.endsWith("/rest/v1/rpc/get_public_exam_file_download")) {
        throw new Error(`unexpected request ${url}`);
      }
      return Response.json([]);
    }, async () => {
      await expectError(
        await getExamFileUrl(post({ sessionId, fileId })),
        404,
        "PUBLIC_EXAM_FILE_NOT_FOUND",
      );
    })
  );
});

Deno.test("exam file function signs only the object authorized by the user RPC", async () => {
  const calls: string[] = [];
  await withFunctionEnvironment(() =>
    withMockFetch(async (input, init) => {
      const url = input.toString();
      calls.push(url);
      if (url.endsWith("/rest/v1/rpc/get_public_exam_file_download")) {
        const headers = new Headers(init?.headers);
        if (
          headers.get("authorization") !== authorization ||
          headers.get("apikey") !== functionEnvironment.SUPABASE_ANON_KEY
        ) {
          throw new Error("authorization RPC used the wrong credentials");
        }
        return Response.json([{
          object_path: "organization/session/exam.zip",
          file_name: "exam.zip",
          size_bytes: 123,
          sha256: "fixture-sha256",
        }]);
      }
      if (url.includes("/storage/v1/object/sign/exam-archives/")) {
        const headers = new Headers(init?.headers);
        if (
          headers.get("authorization") !==
            `Bearer ${functionEnvironment.SUPABASE_SERVICE_ROLE_KEY}`
        ) {
          throw new Error(
            "storage signing did not use the server-only credential",
          );
        }
        return Response.json({
          signedURL: "/object/sign/exam-archives/signed-fixture",
        });
      }
      throw new Error(`unexpected request ${url}`);
    }, async () => {
      const response = await getExamFileUrl(post({ sessionId, fileId }));
      if (response.status !== 200) {
        throw new Error(`unexpected status ${response.status}`);
      }
      const payload = await response.json();
      if (
        payload.fileName !== "exam.zip" || payload.sizeBytes !== 123 ||
        payload.expiresIn !== 180
      ) {
        throw new Error("unexpected signed file response");
      }
      if (
        JSON.stringify(payload).includes(
          functionEnvironment.SUPABASE_SERVICE_ROLE_KEY,
        )
      ) {
        throw new Error("server-only credential leaked in success response");
      }
      if (calls.length !== 2) {
        throw new Error(`unexpected request count ${calls.length}`);
      }
    })
  );
});

Deno.test("archive verifier denies an inaccessible submission before service access", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input, init) => {
      const url = input.toString();
      if (!url.includes("/rest/v1/submission_files?")) {
        throw new Error(`unexpected request ${url}`);
      }
      if (new Headers(init?.headers).get("authorization") !== authorization) {
        throw new Error("submission lookup did not use the user JWT");
      }
      return new Response(null, { status: 403 });
    }, async () => {
      await expectError(
        await verifySubmission(
          post({ submissionId, idempotencyKey: "request-fixture" }),
        ),
        403,
        "SUBMISSION_FILE_FORBIDDEN",
      );
    })
  );
});

Deno.test("archive verifier rejects a missing submission file", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input) => {
      const url = input.toString();
      if (!url.includes("/rest/v1/submission_files?")) {
        throw new Error(`unexpected request ${url}`);
      }
      return Response.json([]);
    }, async () => {
      await expectError(
        await verifySubmission(
          post({ submissionId, idempotencyKey: "request-fixture" }),
        ),
        422,
        "SUBMISSION_FILE_INVALID",
      );
    })
  );
});

Deno.test("archive verifier returns a receipt after user-authorized verification", async () => {
  const archive = new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x00, 0x00]);
  const digest = await crypto.subtle.digest("SHA-256", archive);
  const sha256 = [...new Uint8Array(digest)].map((part) =>
    part.toString(16).padStart(2, "0")
  ).join("");
  const calls: string[] = [];
  await withFunctionEnvironment(() =>
    withMockFetch(async (input, init) => {
      const url = input.toString();
      calls.push(url);
      const headers = new Headers(init?.headers);
      if (url.includes("/rest/v1/submission_files?")) {
        if (headers.get("authorization") !== authorization) {
          throw new Error("metadata lookup bypassed the user JWT");
        }
        return Response.json([{
          id: fileId,
          name: "submission.zip",
          size_bytes: archive.byteLength,
          sha256,
          cloud_object_path: "organization/submission.zip",
          archive_signature_verified: false,
        }]);
      }
      if (
        url.includes(
          "/storage/v1/object/authenticated/public-submission-archives/",
        )
      ) {
        if (
          headers.get("authorization") !==
            `Bearer ${functionEnvironment.SUPABASE_SERVICE_ROLE_KEY}`
        ) {
          throw new Error(
            "archive read did not use the server-only credential",
          );
        }
        return new Response(archive, {
          headers: { "content-length": archive.byteLength.toString() },
        });
      }
      if (url.endsWith("/rest/v1/rpc/verify_public_submission_archive")) {
        if (
          headers.get("authorization") !==
            `Bearer ${functionEnvironment.SUPABASE_SERVICE_ROLE_KEY}`
        ) {
          throw new Error(
            "verification RPC did not use the server-only credential",
          );
        }
        return Response.json(null);
      }
      if (url.endsWith("/rest/v1/rpc/finalize_public_submission")) {
        if (headers.get("authorization") !== authorization) {
          throw new Error("finalize RPC bypassed the user JWT");
        }
        return Response.json("receipt-fixture");
      }
      throw new Error(`unexpected request ${url}`);
    }, async () => {
      const response = await verifySubmission(
        post({ submissionId, idempotencyKey: "request-fixture" }),
      );
      if (response.status !== 200) {
        throw new Error(`unexpected status ${response.status}`);
      }
      const payload = await response.json();
      if (payload.receiptCode !== "receipt-fixture") {
        throw new Error("unexpected receipt");
      }
      if (
        JSON.stringify(payload).includes(
          functionEnvironment.SUPABASE_SERVICE_ROLE_KEY,
        )
      ) {
        throw new Error("server-only credential leaked in success response");
      }
      if (calls.length !== 4) {
        throw new Error(`unexpected request count ${calls.length}`);
      }
    })
  );
});

Deno.test("device command function rejects an invalid user session", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input) => {
      const url = input.toString();
      if (!url.endsWith("/auth/v1/user")) {
        throw new Error(`unexpected request ${url}`);
      }
      return new Response(null, { status: 401 });
    }, async () => {
      await expectError(
        await issueDeviceCommand(
          post({ sessionId, deviceId: "device-fixture", commandType: "LOCK" }),
        ),
        401,
        "INVALID_USER_SESSION",
      );
    })
  );
});

Deno.test("device command function hides an authorization RPC rejection", async () => {
  await withFunctionEnvironment(() =>
    withMockFetch(async (input) => {
      const url = input.toString();
      if (url.endsWith("/auth/v1/user")) return Response.json({ id: userId });
      if (url.endsWith("/rest/v1/rpc/issue_public_device_command")) {
        return new Response("sensitive backend detail fixture", {
          status: 403,
        });
      }
      throw new Error(`unexpected request ${url}`);
    }, async () => {
      const response = await issueDeviceCommand(
        post({ sessionId, deviceId: "device-fixture", commandType: "LOCK" }),
      );
      await expectError(response, 403, "COMMAND_REJECTED");
    })
  );
});

Deno.test("device command function returns a signed command after RPC authorization", async () => {
  let rpcBody: Record<string, unknown> | undefined;
  await withFunctionEnvironment(() =>
    withMockFetch(async (input, init) => {
      const url = input.toString();
      const headers = new Headers(init?.headers);
      if (url.endsWith("/auth/v1/user")) {
        if (headers.get("authorization") !== authorization) {
          throw new Error("user validation did not use the user JWT");
        }
        return Response.json({ id: userId });
      }
      if (url.endsWith("/rest/v1/rpc/issue_public_device_command")) {
        if (
          headers.get("authorization") !==
            `Bearer ${functionEnvironment.SUPABASE_SERVICE_ROLE_KEY}`
        ) {
          throw new Error("command RPC did not use the server-only credential");
        }
        rpcBody = JSON.parse(String(init?.body));
        return Response.json(null);
      }
      throw new Error(`unexpected request ${url}`);
    }, async () => {
      const response = await issueDeviceCommand(post({
        sessionId,
        deviceId: "device-fixture",
        commandType: "LOCK",
        ttlSeconds: 60,
      }));
      if (response.status !== 201) {
        throw new Error(`unexpected status ${response.status}`);
      }
      const payload = await response.json();
      if (
        !payload.commandId || !rpcBody?.p_signature ||
        rpcBody.p_issued_by !== userId
      ) {
        throw new Error("signed command contract is incomplete");
      }
      if (
        JSON.stringify(payload).includes(
          functionEnvironment.SUPABASE_SERVICE_ROLE_KEY,
        )
      ) {
        throw new Error("server-only credential leaked in success response");
      }
    })
  );
});
