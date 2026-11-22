// Every error code the server can produce is listed in docs/tools.md, the one home for codes.
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { LOCAL_ERROR_CODES } from "../src/errors.js";

const toolsMd = readFileSync(new URL("../../docs/tools.md", import.meta.url), "utf8");
const errorsTs = readFileSync(new URL("../src/errors.ts", import.meta.url), "utf8");
// The agent codes the server has a message for (describeAgentError's cases).
const agentCodes = [...errorsTs.matchAll(/^\s*case "([a-z_]+)":/gm)].map((m) => m[1]!);

describe("docs/tools.md lists every error code", () => {
  it("finds codes to check", () => {
    expect(LOCAL_ERROR_CODES.length).toBeGreaterThan(10);
    expect(agentCodes).toContain("cable_in_use");
  });

  it.each([...LOCAL_ERROR_CODES, ...agentCodes])("%s", (code) => {
    expect(toolsMd).toContain(`\`${code}\``);
  });
});
