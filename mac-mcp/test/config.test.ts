import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { DEFAULT_LOCAL_CONFIG_PATH, loadConfig } from "../src/config.js";

describe("loadConfig", () => {
  it("uses the documented defaults when nothing is set", () => {
    const { config, warnings } = loadConfig({}, "/Users/test");
    expect(config).toEqual({
      agentUrl: undefined,
      agentPort: 8765,
      vmMac: undefined,
      vmName: "Windows",
      utmConfigPath: "/Users/test/Library/Containers/com.utmapp.UTM/Data/Documents/Windows.utm/config.plist",
      envToken: undefined,
      auditLogPath: "/Users/test/Library/Logs/ediabas-bridge/audit.log",
      keychainService: "ediabas-bridge",
      keychainAccount: "agent",
      utmctlPath: "/Applications/UTM.app/Contents/MacOS/utmctl",
    });
    expect(warnings).toEqual([]);
  });

  it("reads every variable", () => {
    const { config } = loadConfig(
      {
        EDIABAS_AGENT_URL: "http://10.0.0.5:9000/",
        EDIABAS_AGENT_PORT: "9001",
        EDIABAS_VM_MAC: "aa:bb:cc:dd:ee:ff",
        EDIABAS_VM_NAME: "Win11",
        EDIABAS_AGENT_TOKEN: "  tok  ",
        EDIABAS_AUDIT_LOG: "~/x/audit.log",
      },
      "/Users/test",
    );
    expect(config.agentUrl).toBe("http://10.0.0.5:9000");
    expect(config.agentPort).toBe(9001);
    expect(config.vmMac).toBe("aa:bb:cc:dd:ee:ff");
    expect(config.vmName).toBe("Win11");
    expect(config.utmConfigPath).toBe("/Users/test/Library/Containers/com.utmapp.UTM/Data/Documents/Win11.utm/config.plist");
    expect(config.envToken).toBe("tok");
    expect(config.auditLogPath).toBe("/Users/test/x/audit.log");
  });

  it("treats empty strings as unset", () => {
    const { config } = loadConfig(
      { EDIABAS_AGENT_URL: "", EDIABAS_AGENT_TOKEN: "   ", EDIABAS_VM_NAME: "" },
      "/h",
    );
    expect(config.agentUrl).toBeUndefined();
    expect(config.envToken).toBeUndefined();
    expect(config.vmName).toBe("Windows");
  });

  it("falls back to the default port with a warning on a bad port", () => {
    const { config, warnings } = loadConfig({ EDIABAS_AGENT_PORT: "abc" }, "/h");
    expect(config.agentPort).toBe(8765);
    expect(warnings.join("\n")).toMatch(/EDIABAS_AGENT_PORT/);
  });

  it("keeps an invalid agent URL so tools can report it", () => {
    const { config, warnings } = loadConfig({ EDIABAS_AGENT_URL: "not a url" }, "/h");
    expect(config.agentUrl).toBe("not a url");
    expect(warnings.join("\n")).toMatch(/EDIABAS_AGENT_URL/);
  });
});

describe("config.local.json", () => {
  let dir: string;
  let path: string;
  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), "ediabas-local-config-"));
    path = join(dir, "config.local.json");
  });
  afterEach(() => rmSync(dir, { recursive: true, force: true }));

  const write = (value: unknown) => writeFileSync(path, typeof value === "string" ? value : JSON.stringify(value));

  it("is at the repo root", () => {
    expect(DEFAULT_LOCAL_CONFIG_PATH).toBe(join(import.meta.dirname, "..", "..", "config.local.json"));
  });

  it("is used when the environment does not set a value", () => {
    write({ _comment: "ignored", vmName: "Win11", vmMac: "02:00:00:00:00:01", agentPort: 9002 });
    const { config, warnings } = loadConfig({}, "/h", path);
    expect(warnings).toEqual([]);
    expect(config.vmName).toBe("Win11");
    expect(config.vmMac).toBe("02:00:00:00:00:01");
    expect(config.agentPort).toBe(9002);
    expect(config.utmConfigPath).toBe("/h/Library/Containers/com.utmapp.UTM/Data/Documents/Win11.utm/config.plist");
  });

  it("loses to the environment", () => {
    write({ vmName: "Win11", vmMac: "02:00:00:00:00:01", agentPort: 9002 });
    const { config } = loadConfig({ EDIABAS_VM_NAME: "Other", EDIABAS_VM_MAC: "02:00:00:00:00:02", EDIABAS_AGENT_PORT: "9003" }, "/h", path);
    expect(config.vmName).toBe("Other");
    expect(config.vmMac).toBe("02:00:00:00:00:02");
    expect(config.agentPort).toBe(9003);
  });

  it("leaves the MAC unset (read from UTM later) and the defaults when it does not set them", () => {
    write({ vmName: "Win11" });
    const { config } = loadConfig({}, "/h", path);
    expect(config.vmMac).toBeUndefined();
    expect(config.agentPort).toBe(8765);
  });

  it("a bad EDIABAS_AGENT_PORT falls back to the file's port", () => {
    write({ agentPort: 9002 });
    const { config, warnings } = loadConfig({ EDIABAS_AGENT_PORT: "x" }, "/h", path);
    expect(config.agentPort).toBe(9002);
    expect(warnings).toEqual(["EDIABAS_AGENT_PORT is not a valid port; using 9002."]);
  });

  it("a missing file is fine and not read when no path is given", () => {
    expect(loadConfig({}, "/h", join(dir, "nope.json")).warnings).toEqual([]);
    write("{ not json");
    expect(loadConfig({}, "/h").warnings).toEqual([]);
  });

  it("invalid JSON is a warning, not a crash", () => {
    write("{ not json");
    const { config, warnings } = loadConfig({}, "/h", path);
    expect(config.vmName).toBe("Windows");
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toMatch(/config\.local\.json is not valid JSON .*; it is ignored\.$/);
  });

  it("a non-object is a warning", () => {
    write([1]);
    expect(loadConfig({}, "/h", path).warnings[0]).toMatch(/must hold one JSON object/);
  });

  it("unknown keys and bad values are warnings; the valid keys still apply", () => {
    write({ vmname: "x", vmName: "Win11", vmMac: "", agentPort: "8000" });
    const { config, warnings } = loadConfig({}, "/h", path);
    expect(config.vmName).toBe("Win11");
    expect(config.vmMac).toBeUndefined();
    expect(config.agentPort).toBe(8765);
    expect(warnings).toEqual([
      `${path}: unknown key "vmname" is ignored (known keys: vmName, vmMac, agentPort).`,
      `${path}: vmMac must be a non-empty string; it is ignored.`,
      `${path}: agentPort must be a whole number from 1 to 65535; it is ignored.`,
    ]);
  });
});
