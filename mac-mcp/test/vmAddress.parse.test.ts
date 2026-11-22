import { describe, expect, it } from "vitest";
import {
  bridgeSubnets,
  isOnLink,
  leaseCandidates,
  macEquals,
  parseLeases,
  parseMac,
  macFromUtmConfig,
} from "../src/vmAddress.js";

const VM_MAC = "02:00:00:00:00:01";

const TWO_BLOCK_LEASES = `{
	name=DESKTOP-OLD
	ip_address=192.168.65.3
	hw_address=1,2:0:0:0:0:1
	identifier=1,2:0:0:0:0:1
	lease=0x68a1baf4
}
{
	name=DESKTOP-NEW
	ip_address=192.168.64.3
	hw_address=1,2:0:0:0:0:1
	identifier=1,2:0:0:0:0:1
	lease=0x6b01baf4
}
`;

describe("MAC comparison", () => {
  it("matches macOS short bytes against the configured MAC", () => {
    expect(macEquals("2:0:0:0:0:1", "02:00:00:00:00:01")).toBe(true);
    expect(macEquals("2:00:0:00:0:1", "02:00:00:00:00:01")).toBe(true);
    expect(macEquals("02-00-00-00-00-01", "2:0:0:0:0:1")).toBe(true);
  });

  it("rejects mismatches", () => {
    expect(macEquals("2:0:0:0:0:2", VM_MAC)).toBe(false);
    expect(macEquals("2:0:0:0:0:10", VM_MAC)).toBe(false);
    expect(macEquals("0:0:0:0:1:2", VM_MAC)).toBe(false);
  });

  it("rejects malformed input rather than matching", () => {
    for (const bad of ["", "2:0:0:0:0", "02:00:00:00:00:01:00", "zz:0:0:0:0:1",
      "2:0:0:0:0:101", "2::0:0:0:1", "(incomplete)", "2:0:0:0:0:1x"]) {
      expect(parseMac(bad)).toBeNull();
      expect(macEquals(bad, VM_MAC)).toBe(false);
    }
    expect(macEquals(VM_MAC, "garbage")).toBe(false);
  });

  it("parses bytes numerically", () => {
    expect(parseMac("2:0:0:0:0:1")).toEqual([0x02, 0x00, 0x00, 0x00, 0x00, 0x01]);
  });
});

describe("DHCP lease parsing", () => {
  it("gives only the newest of two leases for the MAC", () => {
    expect(leaseCandidates(TWO_BLOCK_LEASES, VM_MAC)).toEqual(["192.168.64.3"]);
  });

  it("gives the newest by expiry even when it comes first or last in the file", () => {
    const newer = `{\n\tip_address=192.168.64.3\n\thw_address=1,2:0:0:0:0:1\n\tlease=0x6b01baf4\n}\n`;
    const older = `{\n\tip_address=192.168.65.3\n\thw_address=1,2:0:0:0:0:1\n\tlease=0x68a1baf4\n}\n`;
    expect(leaseCandidates(newer + older, VM_MAC)).toEqual(["192.168.64.3"]);
    expect(leaseCandidates(older + newer, VM_MAC)).toEqual(["192.168.64.3"]);
  });

  it("on an equal expiry the earlier entry in the file wins, as in serve.py", () => {
    const a = `{\n\tip_address=192.168.64.3\n\thw_address=1,2:0:0:0:0:1\n\tlease=0x6b01baf4\n}\n`;
    const b = `{\n\tip_address=192.168.64.4\n\thw_address=1,2:0:0:0:0:1\n\tlease=0x6b01baf4\n}\n`;
    expect(leaseCandidates(a + b, VM_MAC)).toEqual(["192.168.64.3"]);
    expect(leaseCandidates(b + a, VM_MAC)).toEqual(["192.168.64.4"]);
  });

  it("handles field order variants", () => {
    const text = `{
	lease=0x6b01baf4
	hw_address=1,2:0:0:0:0:1
	name=x
	ip_address=192.168.64.9
}
{
	identifier=1,2:0:0:0:0:1
	ip_address=192.168.64.8
	lease=0x6b01bb00
	hw_address=1,2:0:0:0:0:1
	name=y
}`;
    expect(parseLeases(text)).toEqual([
      { ipAddress: "192.168.64.9", hwAddress: "2:0:0:0:0:1", lease: 0x6b01baf4 },
      { ipAddress: "192.168.64.8", hwAddress: "2:0:0:0:0:1", lease: 0x6b01bb00 },
    ]);
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.8"]);
  });

  it("ignores other hosts", () => {
    const text = `{
	name=iphone
	ip_address=192.168.64.20
	hw_address=1,aa:bb:cc:dd:ee:ff
	lease=0x7fffffff
}
${TWO_BLOCK_LEASES}
{
	name=other
	ip_address=192.168.64.21
	hw_address=1,2:0:0:0:0:2
	lease=0x7ffffffe
}`;
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.3"]);
  });

  it("returns nothing for an empty or missing file and skips broken blocks", () => {
    expect(leaseCandidates("", VM_MAC)).toEqual([]);
    expect(leaseCandidates(`{\n\tip_address=not-an-ip\n\thw_address=1,2:0:0:0:0:1\n\tlease=0x1\n}`, VM_MAC)).toEqual([]);
    expect(leaseCandidates(`{\n\tip_address=192.168.64.4\n\tlease=0x1\n}`, VM_MAC)).toEqual([]);
    expect(leaseCandidates(`{\n\tip_address=192.168.64.4\n\thw_address=1,2:0:0:0:0:1\n\tlease=0xzz\n}`, VM_MAC)).toEqual([]);
  });
});

describe("on-link rule", () => {
  const ifaces = {
    lo0: [{ address: "127.0.0.1", netmask: "255.0.0.0", family: "IPv4", mac: "00:00:00:00:00:00", internal: true, cidr: "127.0.0.1/8" }],
    en0: [{ address: "192.168.1.20", netmask: "255.255.255.0", family: "IPv4", mac: "aa:aa:aa:aa:aa:aa", internal: false, cidr: "192.168.1.20/24" }],
    bridge100: [
      { address: "fe80::1", netmask: "ffff:ffff:ffff:ffff::", family: "IPv6", mac: "f2:00:00:00:00:64", internal: false, cidr: "fe80::1/64", scopeid: 1 },
      { address: "192.168.64.1", netmask: "255.255.255.0", family: "IPv4", mac: "f2:00:00:00:00:64", internal: false, cidr: "192.168.64.1/24" },
    ],
  } as const;

  it("only takes IPv4 subnets from bridge1NN interfaces", () => {
    expect(bridgeSubnets(ifaces as never)).toEqual([
      { iface: "bridge100", address: "192.168.64.1", netmask: "255.255.255.0" },
    ]);
  });

  it("counts only bridge1NN interfaces as on-link: bridge0 is not, bridge100 is", () => {
    const v4 = (address: string) => [{ address, netmask: "255.255.255.0", family: "IPv4", mac: "f2:00:00:00:00:64", internal: false, cidr: `${address}/24` }];
    const bridge0 = bridgeSubnets({ bridge0: v4("192.168.64.1") } as never);
    expect(bridge0).toEqual([]);
    expect(isOnLink("192.168.64.3", bridge0)).toBe(false);
    const bridge100 = bridgeSubnets({ bridge100: v4("192.168.64.1") } as never);
    expect(isOnLink("192.168.64.3", bridge100)).toBe(true);
    expect(bridgeSubnets({ bridge1: v4("10.0.0.1"), bridge1000: v4("10.0.1.1"), bridge101: v4("10.0.2.1") } as never).map((s) => s.iface)).toEqual(["bridge101"]);
  });

  it("accepts addresses inside a bridge subnet only", () => {
    const subnets = bridgeSubnets(ifaces as never);
    expect(isOnLink("192.168.64.3", subnets)).toBe(true);
    expect(isOnLink("192.168.65.3", subnets)).toBe(false);
    expect(isOnLink("192.168.1.5", subnets)).toBe(false);
    expect(isOnLink("not-an-ip", subnets)).toBe(false);
    expect(isOnLink("192.168.64.3", [])).toBe(false);
  });
});

describe("MAC from UTM's config", () => {
  it("takes the first Network entry's MacAddress", () => {
    const json = JSON.stringify({
      Network: [
        { Hardware: "virtio-net-pci", IsolateFromHost: false, MacAddress: "02:00:00:00:00:01", Mode: "Shared", PortForward: [] },
        { MacAddress: "02:00:00:00:00:02" },
      ],
    });
    expect(macFromUtmConfig(json)).toBe("02:00:00:00:00:01");
  });

  it("gives nothing for a config without a usable MAC", () => {
    for (const text of ["", "not json", "null", "[]", "{}", '{"Network":[]}', '{"Network":{}}', '{"Network":[{}]}',
      '{"Network":[{"MacAddress":"garbage"}]}', '{"Network":[{"MacAddress":7}]}', '{"Network":[null]}']) {
      expect(macFromUtmConfig(text)).toBeUndefined();
    }
  });
});
