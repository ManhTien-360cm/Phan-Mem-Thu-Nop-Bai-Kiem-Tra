import { canonicalDeviceCommand, hmacHex } from "./device-command-signature.ts";

Deno.test("device command golden fixture", async () => {
  const fixture = JSON.parse(
    await Deno.readTextFile(
      new URL("../../fixtures/device-command-signature.json", import.meta.url),
    ),
  );
  const canonical = canonicalDeviceCommand({
    commandId: fixture.commandId,
    sessionId: fixture.sessionId,
    deviceId: fixture.deviceId,
    commandType: fixture.commandType,
    payload: fixture.payload,
    createdAt: new Date(fixture.createdAtUtc),
    expiresAt: new Date(fixture.expiresAtUtc),
    issuedBy: fixture.issuedBy,
  });
  if (canonical !== fixture.canonical) throw new Error("canonical mismatch");
  if (await hmacHex(fixture.secretUtf8, canonical) !== fixture.signature) {
    throw new Error("signature mismatch");
  }
});
