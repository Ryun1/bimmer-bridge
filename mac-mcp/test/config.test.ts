import { describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";

describe("loadConfig", () => {
  it("uses the documented defaults when nothing is set", () => {
    const { config, warnings } = loadConfig({}, "/Users/test");
    expect(config).toEqual({
      agentUrl: undefined,
      agentPort: 8765,
      vmMac: "F2:E5:81:1E:82:07",
      vmName: "Windows",
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
