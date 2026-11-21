import { describe, expect, it } from "vitest";
import {
  bridgeSubnets,
  isOnLink,
  leaseCandidates,
  macEquals,
  parseArp,
  parseLeases,
  parseMac,
  parseUtmctl,
  arpCandidates,
} from "../src/vmAddress.js";

const VM_MAC = "F2:E5:81:1E:82:07";

const TWO_BLOCK_LEASES = `{
	name=DESKTOP-OLD
	ip_address=192.168.65.3
	hw_address=1,f2:e5:81:1e:82:7
	identifier=1,f2:e5:81:1e:82:7
	lease=0x68a1baf4
}
{
	name=DESKTOP-NEW
	ip_address=192.168.64.3
	hw_address=1,f2:e5:81:1e:82:7
	identifier=1,f2:e5:81:1e:82:7
	lease=0x6b01baf4
}
`;

describe("MAC comparison", () => {
  it("matches macOS short bytes against the configured MAC", () => {
    expect(macEquals("f2:e5:81:1e:82:7", "F2:E5:81:1E:82:07")).toBe(true);
    expect(macEquals("2:0:0:0:0:1", "02:00:00:00:00:01")).toBe(true);
    expect(macEquals("02-00-00-00-00-01", "2:0:0:0:0:1")).toBe(true);
  });

  it("rejects mismatches", () => {
    expect(macEquals("f2:e5:81:1e:82:8", VM_MAC)).toBe(false);
    expect(macEquals("f2:e5:81:1e:82:70", VM_MAC)).toBe(false);
    expect(macEquals("e5:81:1e:82:7:f2", VM_MAC)).toBe(false);
  });

  it("rejects malformed input rather than matching", () => {
    for (const bad of ["", "f2:e5:81:1e:82", "f2:e5:81:1e:82:07:00", "zz:e5:81:1e:82:7",
      "f2:e5:81:1e:82:107", "f2::81:1e:82:7", "(incomplete)", "f2:e5:81:1e:82:7x"]) {
      expect(parseMac(bad)).toBeNull();
      expect(macEquals(bad, VM_MAC)).toBe(false);
    }
    expect(macEquals(VM_MAC, "garbage")).toBe(false);
  });

  it("parses bytes numerically", () => {
    expect(parseMac("f2:e5:81:1e:82:7")).toEqual([0xf2, 0xe5, 0x81, 0x1e, 0x82, 0x07]);
  });
});

describe("DHCP lease parsing", () => {
  it("orders the two-block sample by latest expiry: current address first", () => {
    expect(leaseCandidates(TWO_BLOCK_LEASES, VM_MAC)).toEqual(["192.168.64.3", "192.168.65.3"]);
  });

  it("orders by expiry even when the newer block comes first", () => {
    const text = `{\n\tip_address=192.168.64.3\n\thw_address=1,f2:e5:81:1e:82:7\n\tlease=0x6b01baf4\n}\n` +
      `{\n\tip_address=192.168.65.3\n\thw_address=1,f2:e5:81:1e:82:7\n\tlease=0x68a1baf4\n}\n`;
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.3", "192.168.65.3"]);
  });

  it("handles field order variants", () => {
    const text = `{
	lease=0x6b01baf4
	hw_address=1,f2:e5:81:1e:82:7
	name=x
	ip_address=192.168.64.9
}
{
	identifier=1,f2:e5:81:1e:82:7
	ip_address=192.168.64.8
	lease=0x6b01bb00
	hw_address=1,f2:e5:81:1e:82:7
	name=y
}`;
    expect(parseLeases(text)).toEqual([
      { ipAddress: "192.168.64.9", hwAddress: "f2:e5:81:1e:82:7", lease: 0x6b01baf4 },
      { ipAddress: "192.168.64.8", hwAddress: "f2:e5:81:1e:82:7", lease: 0x6b01bb00 },
    ]);
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.8", "192.168.64.9"]);
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
	hw_address=1,f2:e5:81:1e:82:8
	lease=0x7ffffffe
}`;
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.3", "192.168.65.3"]);
  });

  it("returns nothing for an empty or missing file and skips broken blocks", () => {
    expect(leaseCandidates("", VM_MAC)).toEqual([]);
    expect(leaseCandidates(`{\n\tip_address=not-an-ip\n\thw_address=1,f2:e5:81:1e:82:7\n\tlease=0x1\n}`, VM_MAC)).toEqual([]);
    expect(leaseCandidates(`{\n\tip_address=192.168.64.4\n\tlease=0x1\n}`, VM_MAC)).toEqual([]);
  });

  it("de-duplicates repeated addresses", () => {
    const text = TWO_BLOCK_LEASES + `{\n\tip_address=192.168.64.3\n\thw_address=1,f2:e5:81:1e:82:7\n\tlease=0x1\n}\n`;
    expect(leaseCandidates(text, VM_MAC)).toEqual(["192.168.64.3", "192.168.65.3"]);
  });
});

describe("arp -an parsing", () => {
  const ARP = [
    "? (192.168.1.1) at 0:11:22:33:44:55 on en0 ifscope [ethernet]",
    "? (192.168.64.3) at f2:e5:81:1e:82:7 on bridge100 ifscope [bridge]",
    "? (192.168.64.7) at (incomplete) on bridge100 ifscope [bridge]",
    "? (192.168.64.255) at ff:ff:ff:ff:ff:ff on bridge100 ifscope [bridge]",
    "? (224.0.0.251) at 1:0:5e:0:0:fb on en0 ifscope permanent [ethernet]",
    "",
    "garbage line",
  ].join("\n");

  it("parses entries and skips (incomplete) and junk", () => {
    expect(parseArp(ARP)).toEqual([
      { ipAddress: "192.168.1.1", macAddress: "0:11:22:33:44:55", iface: "en0" },
      { ipAddress: "192.168.64.3", macAddress: "f2:e5:81:1e:82:7", iface: "bridge100" },
      { ipAddress: "192.168.64.255", macAddress: "ff:ff:ff:ff:ff:ff", iface: "bridge100" },
      { ipAddress: "224.0.0.251", macAddress: "1:0:5e:0:0:fb", iface: "en0" },
    ]);
  });

  it("finds the VM's address", () => {
    expect(arpCandidates(ARP, VM_MAC)).toEqual(["192.168.64.3"]);
    expect(arpCandidates("? (192.168.64.7) at (incomplete) on bridge100 ifscope [bridge]", VM_MAC)).toEqual([]);
  });
});

describe("utmctl output parsing", () => {
  it("keeps IPv4 addresses only", () => {
    expect(parseUtmctl("192.168.64.3\nfe80::1\n\n  10.0.0.2  \nnonsense\n")).toEqual(["192.168.64.3", "10.0.0.2"]);
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

  it("only takes IPv4 subnets from bridge* interfaces", () => {
    expect(bridgeSubnets(ifaces as never)).toEqual([
      { iface: "bridge100", address: "192.168.64.1", netmask: "255.255.255.0" },
    ]);
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
