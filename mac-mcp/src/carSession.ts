/**
 * How long Claude has been reading from the car, so a session at the car stays short. It counts
 * from the first successful read from the car, which is all the Mac can see: it does not know
 * when the ignition was switched on.
 */

/** A car session ends after this long without a read from the car. */
export const CAR_SESSION_GAP_MS = 5 * 60_000;

/** These SGBDs talk to the diagnostic interface (the cable), not to a module in the car. */
const INTERFACE_ECUS = new Set(["TMODE", "UTILITY"]);

function duration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const min = Math.floor(total / 60);
  return min > 0 ? `${min} min ${total % 60} s` : `${total} s`;
}

export class CarSession {
  private start: number | null = null;
  private last = 0;

  private readonly now: () => number;

  constructor(now: () => number = Date.now) {
    this.now = now;
  }

  /** A successful read on `ecu`; starts a session unless one is running. */
  record(ecu: string): void {
    if (INTERFACE_ECUS.has(ecu.toUpperCase())) return;
    const t = this.now();
    if (!this.active(t)) this.start = t;
    this.last = t;
  }

  /** The closing line for a car-facing result, or null when no session is running. */
  line(): string | null {
    const t = this.now();
    if (!this.active(t)) return null;
    return `Car session: ${duration(t - this.start!)} since the first read from the car (aim to finish within 10 min).`;
  }

  private active(t: number): boolean {
    return this.start !== null && t - this.last <= CAR_SESSION_GAP_MS;
  }
}
