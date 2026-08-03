export function canonicalJson(value: unknown): string {
  if (typeof value === "string") return JSON.stringify(value.normalize("NFC"));
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value !== null && typeof value === "object") {
    return `{${
      Object.entries(value as Record<string, unknown>)
        .map(([key, item]) => [key.normalize("NFC"), item] as const)
        .sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)
        .map(([key, item]) => `${JSON.stringify(key)}:${canonicalJson(item)}`)
        .join(",")
    }}`;
  }
  return JSON.stringify(value);
}

export function dotnetTimestamp(date: Date): string {
  return date.toISOString().replace("Z", "0000+00:00");
}

export function canonicalDeviceCommand(input: {
  commandId: string;
  sessionId: string;
  deviceId: string;
  commandType: string;
  payload: unknown;
  createdAt: Date;
  expiresAt: Date;
  issuedBy: string;
}): string {
  return [
    input.commandId.toLowerCase(),
    input.sessionId.toLowerCase(),
    input.deviceId.normalize("NFC"),
    input.commandType.normalize("NFC"),
    canonicalJson(input.payload),
    dotnetTimestamp(input.createdAt),
    dotnetTimestamp(input.expiresAt),
    input.issuedBy.toLowerCase(),
  ].join("\n");
}

export async function hmacHex(secret: string, value: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = new Uint8Array(
    await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(value)),
  );
  return [...signature].map((part) => part.toString(16).padStart(2, "0")).join(
    "",
  );
}
