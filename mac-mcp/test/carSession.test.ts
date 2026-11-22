import { describe, expect, it } from "vitest";
import { CarSession } from "../src/carSession.js";

function clock(start = 1_000_000) {
  const c = { t: start, now: () => c.t, advance: (ms: number) => (c.t += ms) };
  return c;
}

describe("CarSession", () => {
  it("has no line before the first read from the car", () => {
    expect(new CarSession(clock().now).line()).toBeNull();
  });

  it("starts at the first read from the car and counts from it", () => {
    const c = clock();
    const s = new CarSession(c.now);
    s.record("NVE_65");
    expect(s.line()).toBe("Car session: 0 s since the first read from the car (aim to finish within 10 min).");
    c.advance(45_000);
    expect(s.line()).toBe("Car session: 45 s since the first read from the car (aim to finish within 10 min).");
    s.record("d_motor");
    c.advance(205_000);
    expect(s.line()).toBe("Car session: 4 min 10 s since the first read from the car (aim to finish within 10 min).");
  });

  it("ignores TMODE and UTILITY, which read the cable, not the car", () => {
    const s = new CarSession(clock().now);
    s.record("TMODE");
    s.record("utility");
    expect(s.line()).toBeNull();
  });

  it("ends after 5 minutes without a read from the car, and the next read starts a new one", () => {
    const c = clock();
    const s = new CarSession(c.now);
    s.record("NVE_65");
    c.advance(4 * 60_000);
    s.record("NVE_65");
    c.advance(5 * 60_000);
    expect(s.line()).toBe("Car session: 9 min 0 s since the first read from the car (aim to finish within 10 min).");
    c.advance(1);
    expect(s.line()).toBeNull();
    s.record("NVE_65");
    expect(s.line()).toMatch(/^Car session: 0 s /);
  });
});
