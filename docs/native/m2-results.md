# Native engine M2: at the car

Plan: [M2 plan](../superpowers/plans/2026-11-23-native-engine-m2.md). Design:
[native engine](../superpowers/specs/2026-11-22-native-engine-design.md), "Milestones and
acceptance", M2, with its "M1 amendments". M1's results and notes for M2:
[m1-results.md](m1-results.md), section 5. M0's echo-timing question:
[m0-results.md](m0-results.md), section 6.

The spec's M2 list:

1. `IDENT` on one BMW-FAST module (`NVE_65`). Results match the VM's, and the telegrams in the
   native recording match those in the VM's IFH trace.
2. `FS_LESEN` on the [at-the-car](../at-the-car.md) modules, then vehicle status, then the E60
   whole-car reads, all matching the VM in the same session. Count IFH-0003 and short reads.
3. One fast-init module, if M0 found any (it found none: dropped).
4. A shorter repeat on Linux (dropped in M0, section 9).

**Rules for every session below.** Nothing talks to the car unless Ryan is there and has said go
for that session. A charger is on the car for the whole session and its voltage is noted.
Ignition-on time stays under 10 minutes per trip; anything left over goes to another trip.
[at-the-car.md](../at-the-car.md)'s rules hold: no retry after a refusal or a lost request, and
restart the VM if a job will not stop. `kline loopback` never runs on a car. The 24 reads the
gate refuses natively (M1, section 2) are in no native job list. Recordings, traces and run
outputs stay under `~/m2/`, never in the repo: a `C_FA_LESEN` reply holds the VIN.

Status: procedures written; nothing run yet.

Commands below use:
```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec
EDIABASD=engine/ediabasd-net/bin/Release/net10.0/ediabasd
```

---

## 1. Desk rehearsal: the cable on the Mac, no car (Ryan, Claude)

The cable is plugged into the Mac, unticked in UTM, and **not in the car**. With no 12 V on
pin 16 there is no echo and no module, so nothing here reaches a car.

### Before

- [ ] Release builds: `/opt/homebrew/opt/dotnet@10/bin/dotnet build -c Release engine/ediabasd-net`
      and `cd mac-mcp && npm run build`, with no warnings.
- [ ] Two configs, `~/m2/desk/ediabasd-1.json` and `~/m2/desk/ediabasd-16.json`:
      `{"ecuPath": "~/ediabas-ecu/Ecu" (absolute), "recordDir": "~/m2/desk" (absolute), "latencyMs": 1}` (and 16).

### Steps (Ryan's go)

1. `$EDIABASD health --config ~/m2/desk/ediabasd-1.json`: `cable.state` is `free`; the port is
   the FTDI cable.
2. `$EDIABASD job NVE_65 IDENT --config ~/m2/desk/ediabasd-1.json`, then the same with
   `ediabasd-16.json`. Expected: an error with no reply (no car), a recording whose `open` says
   `1ms` / `16ms`, one `send` of `IDENT`'s telegram, and no `refused` event.
3. `node dev/telegrams.ts timing ~/m2/desk/*.jsonl`: runs, zero echoes.
4. While a job runs, `$EDIABASD health` from a second shell says `locked`.
5. Tick the cable for the VM in UTM: `health` says `absent`; a native job is `cable_not_found`.
   Untick it again.
6. A scratch MCP registration with `EDIABAS_BACKEND=native` (and `dotnetRoot` in
   `config.local.json`, or `DOTNET_ROOT` in its environment): `bridge_health` answers, and
   `ediabas_describe NVE_65` works (offline). Restore the production registration afterwards.

### Results

| Check | Result |
|---|---|
| `health`: state and port | |
| Job at latency 1: error, `open` latency label, sends, refused events | |
| Job at latency 16: the same | |
| `telegrams.ts timing` | |
| `health` during a job | |
| Cable ticked for the VM: `health`, then a native job | |
| `bridge_health` and `ediabas_describe NVE_65` on the native backend | |

---

## 2. Trip A: Phase 6, the VM only (Ryan at the car; Claude through the MCP tools)

Exactly [at-the-car.md](../at-the-car.md), unchanged: the VM bridge's own first car session,
and M2's baseline.

### Before

- [ ] at-the-car.md's "Before going to the car" items.

### Results

| | |
|---|---|
| Date, charger voltage | |
| Ignition-on time | |
| Modules `IDENT_FUNKTIONAL` showed answering | |
| `IDENT_FUNKTIONAL` / `FS_LESEN_FUNKTIONAL` durations | |
| Anything that failed | |

Session C's `FS_LESEN` list (`dev/m2/session-c.json`) is filled from the answering
at-the-car.md modules after this trip, leaving out any gate-refused read.

---

## 3. Trip B: `NVE_65 IDENT`, telegrams and echo timing (Ryan at the car)

### Before

- [ ] In the VM, with Ryan's OK for this change: back up `C:\EDIABAS\Bin\EDIABAS.INI` and set
      `IfhTrace = 3` (as M0, section 4). Note the backup's SHA-256.
- [ ] `~/m2/ediabasd-1.json` and `~/m2/ediabasd-16.json`: `ecuPath`, `recordDir` `~/m2/b/`,
      `latencyMs` 1 and 16.
- [ ] `mac-mcp` built; `ediabasd` Release built; Mac on power and kept awake (`caffeinate -i`).

### Steps (Ryan's go)

1. **VM side** (cable ticked for the VM, ignition on, note the time):
   `node dev/car-compare.ts run --side vm --jobs dev/m2/session-b.json --out ~/m2/b-vm.jsonl`.
   Copy `C:\EDIABAS\TRACE\ifh.trc` to `~/m2/b-ifh.trc` over the dev shell.
2. **Move the cable:** untick it in UTM. `$EDIABASD health --config ~/m2/ediabasd-1.json`: `free`.
3. **Native side, twice:**
   `node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-1.json --jobs dev/m2/session-b.json --out ~/m2/b-native-1.jsonl`,
   then the same with `ediabasd-16.json` and `b-native-16.jsonl`. Ignition off. Note the total time.
4. **Compare:** `node dev/car-compare.ts compare ~/m2/b-vm.jsonl ~/m2/b-native-1.jsonl` (and
   `-16`); `node dev/telegrams.ts compare ~/m2/b-ifh.trc <the latency-1 recording>`;
   `node dev/telegrams.ts timing ~/m2/b/*.jsonl`.

### Afterwards

- [ ] Restore `EDIABAS.INI` from the backup and check its SHA-256 against the backup's.

### Results

| | |
|---|---|
| Date, charger voltage, ignition-on time | |
| Results, native (1 ms) against VM | |
| Results, native (16 ms) against VM | |
| Telegrams: matched, first divergence | |
| `EDIABAS.INI` restored, SHA-256 matches | |

| Recording | Latency | Echoes | Echo ms min/median/max | Replies | Reply ms min/median/max | Short reads |
|---|---|---|---|---|---|---|
| | 1ms | | | | | |
| | 16ms | | | | | |

If latency 1 is no faster than 16, M0's open risk 3 is real; the fallback is a longer
`ObdAddRecTimeout`, for M3.

---

## 4. Trip C: fault memory, vehicle status and the whole-car reads (Ryan at the car)

No INI change. If the job list will not fit in 10 minutes of ignition-on on both sides, it is
split at a job boundary across two trips, and each half is compared on its own.

### Before

- [ ] `dev/m2/session-c.json` holds trip A's answering modules.
- [ ] `~/m2/ediabasd-c.json`: `ecuPath`, `recordDir` `~/m2/c/`, `latencyMs` 1.

### Steps (Ryan's go)

1. **VM side:** `node dev/car-compare.ts run --side vm --jobs dev/m2/session-c.json --out ~/m2/c-vm.jsonl`.
2. **Move the cable** (untick in UTM; `health` says `free`).
3. **Native side:**
   `node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-c.json --jobs dev/m2/session-c.json --out ~/m2/c-native.jsonl`.
   Ignition off.
4. **Compare:** `node dev/car-compare.ts compare ~/m2/c-vm.jsonl ~/m2/c-native.jsonl` (it counts
   IFH-0003 per side); `node dev/telegrams.ts timing ~/m2/c/*.jsonl` for short reads.

### Results

| ECU | Job | Outcome | Difference, explained |
|---|---|---|---|
| | | | |

| | VM | Native |
|---|---|---|
| IFH-0003 errors | | |
| Short reads | — | |

Live values such as voltages may differ between the two reads; fault entries and identities may
not.

---

## 5. Results against the spec's M2 list

| Item | Evidence | Status |
|---|---|---|
| 1. `IDENT` on `NVE_65`: results and telegrams match | | |
| 2. Fault memory, vehicle status, whole-car reads match; IFH-0003 and short reads counted | | |
| 3. Fast init | M0: no module uses 0x010C | dropped |
| 4. Linux repeat | M0, section 9 | dropped |

**The VM stays the default backend** (Ryan, 2026-11-23): the strict gate refuses 24 reads
natively (M1, section 2), `NVE_65 STATUS_BORDNETZSPANNUNG` among them, so native is opt-in
(`EDIABAS_BACKEND=native`) until the gate is revisited.

---

## 6. For M3

- The M2 recordings, as replay fixtures (kept local unless scrubbed).
- The latency answer (section 3).
- Any telegram divergence found (sections 3 and 4).
