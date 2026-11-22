import { mkdtempSync, readdirSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { recordingFetch } from "../recorder.ts";

const TOKEN = "synthetic-token-0123456789abcdef";

let dir: string;
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), "record-fetch-"));
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

/** Stands in for the network: answers by path, like the agent would. */
const inner: typeof fetch = async (input, init) => {
  const url = new URL(String(input));
  const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
    new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", ...headers } });
  if (url.pathname === "/hello") return json(200, { agent: "bimmer-bridge", proof: "derived-from-token" });
  if (url.pathname === "/health") return json(200, { status: "ok" });
  if (url.pathname === "/ediabas/job") return json(200, { ecu: "SYNTH_ECU", echo: JSON.parse(String(init?.body)) });
  if (url.pathname === "/screen/capture") {
    return new Response(new Uint8Array([0xff, 0xd8, 0xff, 0xe0]), { status: 200, headers: { "content-type": "image/jpeg", "x-capture-method": "printwindow", "set-cookie": "a=b" } });
  }
  return json(403, { error: { code: "path_not_allowed", message: "no" } });
};

const auth = { accept: "application/json", authorization: `Bearer ${TOKEN}` };
const names = () => readdirSync(dir).sort();
const read = (f: string) => readFileSync(join(dir, f), "utf8");

describe("recordingFetch", () => {
  it("writes one file per exchange, images beside them, and never the token or Authorization", async () => {
    const warnings: string[] = [];
    const f = recordingFetch(dir, (t) => t, { fetch: inner, warn: (m) => warnings.push(m) });
    await f("http://vm:8765/hello?nonce=abc", { method: "GET", headers: { accept: "application/json" } });
    await f("http://vm:8765/health", { method: "GET", headers: auth });
    const res = await f("http://vm:8765/ediabas/job", { method: "POST", headers: { ...auth, "content-type": "application/json" }, body: JSON.stringify({ ecu: "SYNTH_ECU", job: "IDENT" }) });
    expect(((await res.json()) as { ecu: string }).ecu).toBe("SYNTH_ECU"); // the caller still gets a readable response
    const shot = await f("http://vm:8765/screen/capture?window=INPA&maxEdge=1600", { method: "GET", headers: auth });
    expect(new Uint8Array(await shot.arrayBuffer())).toHaveLength(4);
    await f("http://vm:8765/logs", { method: "GET", headers: auth });
    expect(warnings).toEqual([]);

    const files = names();
    expect(files.filter((x) => x.endsWith(".json"))).toHaveLength(4); // /hello is not recorded
    expect(files.filter((x) => x.endsWith(".jpg"))).toHaveLength(1);
    expect(files.some((x) => x.includes("hello"))).toBe(false);
    expect(files.some((x) => /^\d{8}T\d{9}Z-0002-POST-ediabas_job-SYNTH_ECU-IDENT\.json$/.test(x))).toBe(true);

    const all = files.filter((x) => x.endsWith(".json")).map(read);
    for (const t of all) {
      expect(t).not.toContain(TOKEN);
      expect(t.toLowerCase()).not.toContain("authorization");
    }
    const job = JSON.parse(all.find((t) => t.includes('"/ediabas/job"'))!);
    expect(job.request).toEqual({ method: "POST", path: "/ediabas/job", query: {}, body: { ecu: "SYNTH_ECU", job: "IDENT" } });
    expect(job.response.status).toBe(200);
    const cap = JSON.parse(all.find((t) => t.includes('"/screen/capture"'))!);
    expect(cap.request.query).toEqual({ window: "INPA", maxEdge: "1600" });
    expect(cap.response.headers).toEqual({ "content-type": "image/jpeg", "x-capture-method": "printwindow" });
    expect(files).toContain(cap.response.imageFile);
    const err = JSON.parse(all.find((t) => t.includes('"/logs"'))!);
    expect(err.response).toMatchObject({ status: 403, body: { error: { code: "path_not_allowed" } } });
  });

  it("redacts with the function it is given", async () => {
    const f = recordingFetch(dir, (t) => t.split("SYNTH_ECU").join("[redacted]"), { fetch: inner, warn: () => {} });
    await f("http://vm:8765/ediabas/job", { method: "POST", headers: auth, body: JSON.stringify({ ecu: "SYNTH_ECU", job: "IDENT" }) });
    expect(read(names()[0]!)).not.toContain("SYNTH_ECU");
  });

  it("a recording failure warns and does not fail the call", async () => {
    const warnings: string[] = [];
    const f = recordingFetch("/dev/null/cannot", (t) => t, { fetch: inner, warn: (m) => warnings.push(m) });
    const res = await f("http://vm:8765/health", { method: "GET", headers: auth });
    expect(res.status).toBe(200);
    expect(warnings.some((w) => /could not record/.test(w))).toBe(true);
  });

  it("passes a transport failure through unrecorded", async () => {
    const f = recordingFetch(dir, (t) => t, { fetch: async () => { throw new TypeError("fetch failed"); }, warn: () => {} });
    await expect(f("http://vm:8765/health", { headers: auth })).rejects.toThrow("fetch failed");
    expect(names()).toEqual([]);
  });
});
