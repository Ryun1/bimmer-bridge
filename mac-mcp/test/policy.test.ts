import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { DEFAULT_POLICY_PATH, PolicyGuard, evaluate, findDuplicateKey, findNonIntegerLiteral, parsePolicy } from "../src/policy.js";
import { BridgeError } from "../src/errors.js";

const REPO_POLICY = fileURLToPath(new URL("../../policy/policy.json", import.meta.url));
const CASES_PATH = fileURLToPath(new URL("../../policy/policy-cases.json", import.meta.url));

interface Case {
  ecu: string;
  job: string;
  args?: string;
  results?: string;
  allowed: boolean;
  rule: string;
  screen: boolean;
  note?: string;
}

const policyText = readFileSync(REPO_POLICY, "utf8");
const policy = parsePolicy(policyText);
const cases = (JSON.parse(readFileSync(CASES_PATH, "utf8")) as { cases: Case[] }).cases;

describe("policy/policy-cases.json (shared with the agent)", () => {
  it("has cases", () => {
    expect(cases.length).toBeGreaterThan(100);
  });

  it.each(cases.map((c, i) => [i, `${JSON.stringify(c.ecu)} ${JSON.stringify(c.job)}${c.note ? ` — ${c.note}` : ""}`, c] as const))(
    "case %i: %s",
    (_i, _name, c) => {
      const v = evaluate(policy, { ecu: c.ecu, job: c.job, args: c.args, results: c.results });
      expect(v).toEqual({ allowed: c.allowed, rule: c.rule, screen: c.screen });
    },
  );
});

const PARSE_CASES_PATH = fileURLToPath(new URL("../../policy/policy-parse-cases.json", import.meta.url));
const parseCases = (JSON.parse(readFileSync(PARSE_CASES_PATH, "utf8")) as { cases: { name: string; valid: boolean; text: string }[] }).cases;

describe("policy/policy-parse-cases.json (shared with the agent: both parsers accept and refuse the same texts)", () => {
  it("has both kinds", () => {
    expect(parseCases.some((c) => c.valid)).toBe(true);
    expect(parseCases.filter((c) => !c.valid).length).toBeGreaterThan(30);
  });

  it.each(parseCases.map((c, i) => [i, c.name, c] as const))("case %i: %s", (_i, _name, c) => {
    if (c.valid) {
      expect(() => parsePolicy(c.text)).not.toThrow();
    } else {
      expect(() => parsePolicy(c.text)).toThrow();
      const g = PolicyGuard.load("/x/policy.json", () => c.text);
      expect(g.version).toBeNull();
      expect(g.error).not.toBeNull();
    }
  });
});

describe("findDuplicateKey", () => {
  it.each([
    ['{"a":1,"b":2}', null],
    ['{"a":1,"a":2}', "a"],
    ['{"a":{"x":1},"b":{"x":2}}', null],
    ['{"a":[{"x":1},{"x":2}]}', null],
    ['{"a":[{"x":1,"x":2}]}', "x"],
    ['{"a":"\\"}{,\\"b\\":","b":1}', null],
    ['{"a":["a","a"]}', null],
    ['{"\\u0061":1,"a":2}', "a"],
    ['{"k":{"a":1},"k":2}', "k"],
    ['{}', null],
    ['[]', null],
  ] as const)("%s → %s", (text, dup) => {
    JSON.parse(text); // the cases are valid JSON
    expect(findDuplicateKey(text)).toBe(dup);
  });
});

describe("findNonIntegerLiteral (JSON.parse reads 3.0 as 3; the agent refuses it)", () => {
  it.each([
    ['{"version":3,"maxArgLength":10}', null],
    ['{"version":-0,"maxArgLength":0}', null],
    ['{"version":3.0}', "version"],
    ['{"version" : 3e0}', "version"],
    ['{"a":1,"maxArgLength":\n1E+1}', "maxArgLength"],
    ['{"\\u0076ersion":3.5}', "version"],
    ['{"allow":{"version":3.0}}', null],
    ['{"x":[{"version":3.0}],"version":3}', null],
    ['{"x":"\\"version\\":3.0","version":3}', null],
    ['{"version":"3.0"}', null],
  ] as const)("%s → %s", (text, key) => {
    JSON.parse(text); // the cases are valid JSON
    expect(findNonIntegerLiteral(text)).toBe(key);
  });
});

describe("policy loading", () => {
  it("resolves the repo's policy/policy.json by default", () => {
    expect(DEFAULT_POLICY_PATH).toBe(REPO_POLICY);
    const g = PolicyGuard.load();
    expect(g.error).toBeNull();
    expect(g.version).toBe(4);
  });

  it("reads descriptionScreen.ignore (the agent runs the screen; the Mac only validates it)", () => {
    expect(policy.version).toBe(4);
    expect(policy.screenIgnore).toEqual(["beschreibung", "umsetzer"]);
  });

  const failing: [string, () => string][] = [
    ["a missing file", () => {
      throw Object.assign(new Error("ENOENT: no such file or directory"), { code: "ENOENT" });
    }],
    ["invalid JSON", () => "{ not json"],
    ["a missing field", () => JSON.stringify({ ...JSON.parse(policyText), deny: undefined })],
    ["a wrong type", () => JSON.stringify({ ...JSON.parse(policyText), version: "1" })],
    ["an invalid pattern", () => JSON.stringify({ ...JSON.parse(policyText), namePattern: "([" })],
    ["an empty file", () => ""],
    ["no descriptionScreen.ignore", () => {
      const j = JSON.parse(policyText);
      delete j.descriptionScreen.ignore;
      return JSON.stringify(j);
    }],
    ["an upper-case ignore entry", () => {
      const j = JSON.parse(policyText);
      j.descriptionScreen.ignore = ["beschreibung", "Umsetzer"];
      return JSON.stringify(j);
    }],
    ["a duplicate ignore entry", () => {
      const j = JSON.parse(policyText);
      j.descriptionScreen.ignore = ["umsetzer", "umsetzer"];
      return JSON.stringify(j);
    }],
  ];

  it.each(failing)("fails closed on %s: every check refuses with policy_unavailable", (_name, read) => {
    const g = PolicyGuard.load("/x/policy.json", read);
    expect(g.version).toBeNull();
    expect(g.error).toContain("/x/policy.json");
    for (const fn of [
      () => g.require(),
      () => g.check({ ecu: "NVE_65", job: "FS_LESEN" }),
      () => g.evaluate({ ecu: "NVE_65", job: "IDENT" }),
      () => g.checkShape("NVE_65"),
    ]) {
      let err: unknown;
      try {
        fn();
      } catch (e) {
        err = e;
      }
      expect(err).toBeInstanceOf(BridgeError);
      expect((err as BridgeError).code).toBe("policy_unavailable");
      expect((err as BridgeError).message).toMatch(/nothing was sent to the VM/);
    }
  });
});

describe("PolicyGuard", () => {
  const g = PolicyGuard.fromPolicy(policy);

  it("check() throws job_not_allowed naming the rule, without suggesting a workaround", () => {
    try {
      g.check({ ecu: "NVE_65", job: "FS_LOESCHEN" });
      throw new Error("expected a refusal");
    } catch (e) {
      const err = e as BridgeError;
      expect(err.code).toBe("job_not_allowed");
      expect(err.rule).toBe("deny.substring:LOESCH");
      expect(err.message).toContain("deny.substring:LOESCH");
      expect(err.message).toMatch(/do not look for another/i);
    }
  });

  it("check() returns the verdict for an allowed job", () => {
    expect(g.check({ ecu: "NVE_65", job: "STATUS_X" })).toEqual({ allowed: true, rule: "allow.pattern", screen: true });
  });

  it("checkShape() checks names only: describing a denied job is allowed", () => {
    expect(() => g.checkShape("NVE_65", "FS_LOESCHEN")).not.toThrow();
    expect(() => g.checkShape("NVE_65")).not.toThrow();
    expect(() => g.checkShape("../TMODE")).toThrow(/shape\.ecu/);
    expect(() => g.checkShape("NVE_65", "FS_LESEN\n")).toThrow(/shape\.job/);
  });

  it("requires a full match even when the file's pattern is not anchored", () => {
    const p = parsePolicy(JSON.stringify({ ...JSON.parse(policyText), namePattern: "[A-Z_]{1,64}" }));
    expect(evaluate(p, { ecu: "NVE;X", job: "IDENT" }).rule).toBe("shape.ecu");
  });
});
