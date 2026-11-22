import { createHash } from "node:crypto";
import { describe, expect, it, vi } from "vitest";
import { TokenProvider } from "../src/token.js";
import { BridgeError } from "../src/errors.js";

const KEYCHAIN_TOKEN = "kc_TOKEN_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const ENV_TOKEN = "env_TOKEN_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

function notFound(): Promise<string> {
  const err = Object.assign(new Error("security: The specified item could not be found in the keychain."), { code: 44 });
  return Promise.reject(err);
}

describe("TokenProvider", () => {
  it("reads the Keychain with execFile and the documented arguments, trimmed", async () => {
    const execFile = vi.fn(async () => `${KEYCHAIN_TOKEN}\n`);
    const warn = vi.fn();
    const tp = new TokenProvider({ execFile, warn, service: "ediabas-bridge", account: "agent" });
    expect(await tp.get()).toBe(KEYCHAIN_TOKEN);
    expect(execFile).toHaveBeenCalledWith(
      "/usr/bin/security",
      ["find-generic-password", "-s", "ediabas-bridge", "-a", "agent", "-w"],
      expect.any(Object),
    );
    expect(warn).not.toHaveBeenCalled();
  });

  it("is lazy: nothing runs until a token is needed", () => {
    const execFile = vi.fn(async () => KEYCHAIN_TOKEN);
    new TokenProvider({ execFile, warn: vi.fn(), service: "s", account: "a" });
    expect(execFile).not.toHaveBeenCalled();
  });

  it("caches the token between calls", async () => {
    const execFile = vi.fn(async () => KEYCHAIN_TOKEN);
    const tp = new TokenProvider({ execFile, warn: vi.fn(), service: "s", account: "a" });
    await tp.get();
    await tp.get();
    expect(execFile).toHaveBeenCalledTimes(1);
  });

  it("prefers the Keychain over the env token", async () => {
    const warn = vi.fn();
    const tp = new TokenProvider({ execFile: async () => KEYCHAIN_TOKEN, envToken: ENV_TOKEN, warn, service: "s", account: "a" });
    expect(await tp.get()).toBe(KEYCHAIN_TOKEN);
    expect(warn).not.toHaveBeenCalled();
  });

  it("falls back to the env token and warns once, without the token in the warning", async () => {
    const warn = vi.fn();
    const tp = new TokenProvider({ execFile: notFound, envToken: ENV_TOKEN, warn, service: "s", account: "a" });
    expect(await tp.get()).toBe(ENV_TOKEN);
    expect(await tp.refresh()).toBe(ENV_TOKEN);
    expect(await tp.refresh()).toBe(ENV_TOKEN);
    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0]?.[0])).toMatch(/EDIABAS_AGENT_TOKEN/);
    expect(String(warn.mock.calls[0]?.[0])).not.toContain(ENV_TOKEN);
  });

  it("treats empty Keychain output as no token", async () => {
    const tp = new TokenProvider({ execFile: async () => "  \n", envToken: ENV_TOKEN, warn: vi.fn(), service: "s", account: "a" });
    expect(await tp.get()).toBe(ENV_TOKEN);
  });

  it("throws a no_token error with the fix when neither exists, and retries later", async () => {
    let value: string | null = null;
    const execFile = vi.fn(async () => (value === null ? notFound() : value));
    const tp = new TokenProvider({ execFile, warn: vi.fn(), service: "ediabas-bridge", account: "agent" });
    const err = await tp.get().catch((e: unknown) => e);
    expect(err).toBeInstanceOf(BridgeError);
    expect((err as BridgeError).code).toBe("no_token");
    expect((err as BridgeError).message).toContain("python3 dev/serve.py --accept-token");
    // installed afterwards: picked up on the next call (absence is not cached)
    value = KEYCHAIN_TOKEN;
    expect(await tp.get()).toBe(KEYCHAIN_TOKEN);
  });

  it("refresh re-reads the Keychain", async () => {
    let value = KEYCHAIN_TOKEN;
    const execFile = vi.fn(async () => value);
    const tp = new TokenProvider({ execFile, warn: vi.fn(), service: "s", account: "a" });
    expect(await tp.get()).toBe(KEYCHAIN_TOKEN);
    value = "new_TOKEN_cccccccccccccccccccccccccccccccccccc";
    expect(await tp.refresh()).toBe(value);
    expect(execFile).toHaveBeenCalledTimes(2);
  });

  it("redacts every token it has seen", async () => {
    let value = KEYCHAIN_TOKEN;
    const tp = new TokenProvider({ execFile: async () => value, envToken: ENV_TOKEN, warn: vi.fn(), service: "s", account: "a" });
    await tp.get();
    value = "second_TOKEN_ddddddddddddddddddddddddddddddddd";
    await tp.refresh();
    const text = tp.redact(`a ${KEYCHAIN_TOKEN} b ${value} c ${ENV_TOKEN}`);
    expect(text).not.toContain(KEYCHAIN_TOKEN);
    expect(text).not.toContain(value);
    expect(text).not.toContain(ENV_TOKEN);
    expect(text).toContain("[redacted]");
  });

  it("helloKey is the SHA-256 of the token's UTF-8 bytes, and follows a refresh", async () => {
    let value = KEYCHAIN_TOKEN;
    const tp = new TokenProvider({ execFile: async () => value, warn: vi.fn(), service: "s", account: "a" });
    expect((await tp.helloKey()).toString("hex")).toBe(createHash("sha256").update(KEYCHAIN_TOKEN, "utf8").digest("hex"));
    value = "new_TOKEN_cccccccccccccccccccccccccccccccccccc";
    await tp.refresh();
    expect((await tp.helloKey()).toString("hex")).toBe(createHash("sha256").update(value, "utf8").digest("hex"));
  });

  it("helloKey throws no_token when there is no token", async () => {
    const tp = new TokenProvider({ execFile: notFound, warn: vi.fn(), service: "s", account: "a" });
    const err = await tp.helloKey().catch((e: unknown) => e);
    expect((err as BridgeError).code).toBe("no_token");
  });

  describe("with an overridden agent URL (envOnly)", () => {
    it("uses the env token and never reads the Keychain, without the fallback warning", async () => {
      const execFile = vi.fn(async () => KEYCHAIN_TOKEN);
      const warn = vi.fn();
      const tp = new TokenProvider({ execFile, envToken: ENV_TOKEN, envOnly: true, warn, service: "s", account: "a" });
      expect(await tp.get()).toBe(ENV_TOKEN);
      expect(await tp.refresh()).toBe(ENV_TOKEN);
      expect(execFile).not.toHaveBeenCalled();
      expect(warn).not.toHaveBeenCalled();
      expect(tp.redact(`x ${ENV_TOKEN} y`)).toBe("x [redacted] y");
    });

    it("without an env token: no_token saying why, and the Keychain is still not read", async () => {
      const execFile = vi.fn(async () => KEYCHAIN_TOKEN);
      const tp = new TokenProvider({ execFile, envOnly: true, warn: vi.fn(), service: "s", account: "a" });
      const err = await tp.get().catch((e: unknown) => e);
      expect(err).toBeInstanceOf(BridgeError);
      expect((err as BridgeError).code).toBe("no_token");
      expect((err as BridgeError).message).toMatch(/EDIABAS_AGENT_URL is set/);
      expect((err as BridgeError).message).toMatch(/EDIABAS_AGENT_TOKEN/);
      expect((err as BridgeError).message).toMatch(/Keychain token .* deliberately never sent/);
      await tp.refresh().catch(() => undefined);
      expect(execFile).not.toHaveBeenCalled();
    });
  });
});
