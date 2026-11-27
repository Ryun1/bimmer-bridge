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

Status: procedures written; trip A done as Phase 6 (2026-11-23, section 2); the desk rehearsal
done (2026-11-24, section 1); trip BC not run yet. The plan's "Amendments after Phase 6" (2026-11-24) changed what
follows: merge main (0.7.0) first, screen the answering modules' jobs against the gate, and
run trips B and C as one ignition-on session, trip BC (Ryan, 2026-11-24).

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

Run 2026-11-24 at the desk (Ryan, Claude), Release builds of this branch after the 0.7.0 merge
(`ediabasd-net` 0.1.0, libkline 0.1.0, `mac-mcp` 0.7.0), policy version 4 (`5d0afef1659b…`).

| Check | Result |
|---|---|
| `health`: state and port | `free`, `/dev/cu.usbserial-A50285BI` (the FTDI cable), latency `null` before any session |
| Job at latency 1: error, `open` latency label, sends, refused events | `IFH-0003` (`ediabas_error` 9007) after 0.46 s; `open` says `1ms`; one `send` `82 57 F1 1A 80 64` (checksum 0x64 correct), then `close` 0.29 s later; no `refused` |
| Job at latency 16: the same | The same at 0.53 s; `open` says `16ms`; the same single `send`; no `refused` |
| `telegrams.ts timing` | Runs; both recordings 0 echoes, 0 replies, 0 short reads, 0 refused |
| `health` during a job | `locked` four times in a row while the job held the port, `free` before and after (this left a third desk recording) |
| Cable ticked for the VM: `health`, then a native job | `absent`, port `null`; the job is `cable_not_found` (9001, `libkline: no_device`) and opens no recording. Unticked again: `free` |
| `bridge_health` and `ediabas_describe NVE_65` on the native backend | Both answer. `bridge_health`: server 0.7.0, backend native, Ecu folder, cable `free` on the FTDI port, engine and Mac policy both version 4 `5d0afef1659b`. `ediabas_describe NVE_65`: 59 jobs, 21 runnable through the bridge, offline. The tool list is `bridge_health` and the six `ediabas_*` tools, with no `vm_*` |

Notes:
- Step 6 ran the built server over stdio from a scratch script, with `EDIABAS_BACKEND=native`,
  `DOTNET_ROOT`, and `XDG_CONFIG_HOME` pointing at a scratch copy of `ediabasd-1.json`. The
  production MCP registration was never changed, so nothing needed restoring.
- `ediabasd` prints `K\u002BDCAN` in its CLI JSON (System.Text.Json escapes `+`). The MCP side
  parses the JSON and shows `K+DCAN`. Cosmetic only.
- `describe` counts `NVE_65`'s gate-refused `STATUS_` reads as runnable (policy by name). Natively
  they fail as `frame_refused`, and they are in no native job list.
- The gate-refused `NVE_65 STATUS_BORDNETZSPANNUNG` is the only `voltageSources` entry in the
  worktree's `config.local.json`. A native `ediabas_vehicle_status` through the MCP server would
  try it and get a gate refusal. Trip BC's `car-compare` runs read `dev/m2/session-c.json`, not
  `voltageSources`, so the trip is unaffected.

---

## 2. Trip A: Phase 6, the VM only (done 2026-11-23)

Trip A was the VM bridge's Phase 6, run from Claude Desktop with the recording server, on
0.6.2 with policy version 4 (SHA-256 `5d0afef1659b…`). Sources, all on main: the feedback
response (`docs/superpowers/specs/2026-11-23-phase6-feedback-response.md`),
`docs/history/changes.md`, `skills/bimmer-bridge/references/ecu-map.md`, and the fixtures
`dev/fixtures/battery` and `dev/fixtures/nve-faults` (recording numbers below).

### Results

| | |
|---|---|
| Date, charger voltage | 2026-11-23; charger voltage not recorded |
| Ignition-on time | about 18 minutes, over the 10-minute target; mostly a Claude Desktop link drop of 3 minutes on mobile data (the agent was unaffected) |
| Modules `IDENT_FUNKTIONAL` showed answering | 43 named modules plus one set without a name (`0029`); the table is in ecu-map.md. Not answering: `MPM_60` (`IFH-0009`, `0016`) and `d_0012` (`IFH-0009`, `0018`). SINE (0x50) answers `FS_LESEN_FUNKTIONAL` only. The light module is `LM_AHL` (0x70), not `LM_60` |
| `IDENT_FUNKTIONAL` / `FS_LESEN_FUNKTIONAL` durations | 3,925 ms (44 sets) / 3,964 ms (45 sets, 24 codes in 15 modules; `VVT`, `VVT2` answer `ERROR_ECU_SERVICE_NOT_SUPPORTED`) |
| Other timings (agent `durationMs`) | `FS_LESEN`: `CAS` 18, `KGM_60` 31, `D_KBM` 126, `D_PGS` 163, `D_MOTOR` 670; `D_MOTOR` `IDENT` 253; vehicle status items 1–581; the 12-item battery batch 19,154 in all |
| Anything that failed | Ignition-on time (link drop). Three pinned `CAS` reads answered "not supported" (`ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT`); their approvals and `MPM_60`'s were removed afterwards. No bridge fault |

Group resolution at the car: `D_MOTOR` → `N62_TUE` (`0024`), `D_PGS` → `PGS_65_2`, `D_KBM` →
`KBM_60` (`0018`). `NVE_65` fault and info memory were empty (`0014`, `0020`).

### Session C's list

`dev/m2/session-c.json` holds Phase 6's reads that a native run can repeat, under the names
Phase 6 used: vehicle status's four fixed readings, the two functional jobs (60 s each),
`NVE_65` `FS_LESEN` and `IS_LESEN`, `D_MOTOR` `IDENT` and `FS_LESEN`, `FS_LESEN` on `D_PGS`,
`CAS`, `KGM_60` and `D_KBM`, the battery reads the gate screen passed (below), `KOMB60`
`STATUS_KLEMMENSPANNUNG`, and `CAS` `C_FA_LESEN` (not run in Phase 6; its reply holds the VIN).
Left out: `NVE_65` `STATUS_BORDNETZSPANNUNG` and `IHKA60` `STATUS_VERBRAUCHERREDUZIERUNG`
(gate-refused natively), and the modules that did not answer.

### Gate screen of the answering modules (plan task 7b, 2026-11-24, no device)

`dev/gate-screen` (new) ran every policy-allowed (by name), argument-free job of the 43 SGBDs
that answered `IDENT_FUNKTIONAL`, plus `E60` (its two functional jobs without their optional
arguments), `UTILITY`, `TMODE` and the groups `D_MOTOR`, `D_PGS`, `D_KBM`, through EdiabasLib
and `KlineInterface` into an empty libkline replay, as M1's probe did. It reproduces M1 on
`NVE_65` and `CAS` exactly (4 and 3 refused, same services). Policy version 4. Raw report:
`~/m2/gate-screen.json`; the refused pairs: `dev/m2/gate-refused.json`, which the job-list
test now checks every list against.

**Outcome:** 930 jobs run; the gate let 618 first telegrams through, refused **264**, and 48
sent nothing. Every refusal is a policy-allowed `STATUS_` job: 232 × 0x30 (210 of them with
the byte after the local identifier 01, "report current state"), 25 × 0x31, 5 × 0x33, one
0x2E and one 0x3B.

| SGBD | Allowed | With arguments | Run | Refused (service) | Note |
|---|---|---|---|---|---|
| `ACC2` | 20 | 4 | 16 | none |  |
| `ACSM60` | 23 | 3 | 20 | 5 × 0x30, 1 × 0x2E |  |
| `AFS_90` | 51 | 5 | 46 | 33 × 0x30, 1 × 0x33 |  |
| `AHM_E65` | 18 | 3 | 15 | 2 × 0x30 |  |
| `ALBV60B` | 20 | 3 | 17 | 5 × 0x31 |  |
| `ALBV60F` | 20 | 3 | 17 | 5 × 0x31 |  |
| `AMP_60` | 19 | 3 | 16 | 1 × 0x30 |  |
| `ANT_60` | 30 | 3 | 27 | 10 × 0x30 |  |
| `ASK_60` | 20 | 3 | 17 | 4 × 0x31 |  |
| `BFS_PLX` | 35 | 3 | 32 | 20 × 0x30 |  |
| `CAS` | 63 | 11 | 52 | 1 × 0x30, 2 × 0x31 |  |
| `CCC_60` | 30 | 4 | 26 | 1 × 0x31 |  |
| `CCCA60` | 37 | 3 | 34 | none |  |
| `CCCG60` | 42 | 4 | 38 | none |  |
| `CDC_E65` | 17 | 3 | 14 | none |  |
| `CID_60` | 18 | 2 | 16 | 5 × 0x30 |  |
| `DWA_E65` | 20 | 3 | 17 | 3 × 0x30 |  |
| `DXC8_P` | 25 | 2 | 23 | none |  |
| `EHC_E65` | 16 | 4 | 12 | 1 × 0x30 |  |
| `FAS_PLX` | 35 | 3 | 32 | 20 × 0x30 |  |
| `FLA_65` | 18 | 3 | 15 | 1 × 0x3B |  |
| `GS19B` | 114 | 4 | — | — | INITIALISIERUNG sends first (let through); jobs not screened past it |
| `HKL_65_2` | 31 | 3 | 28 | 16 × 0x30 |  |
| `HUD_60` | 26 | 3 | 23 | 6 × 0x30 |  |
| `IHKA60` | 33 | 2 | 31 | 17 × 0x30 |  |
| `KBM_60` | 24 | 3 | 21 | 3 × 0x30, 2 × 0x31 |  |
| `KGM_60` | 33 | 4 | 29 | 14 × 0x30 |  |
| `KOMB60` | 37 | 3 | 34 | none |  |
| `LM_AHL` | 43 | 9 | 34 | 16 × 0x30 |  |
| `N62_TUE` | 85 | 2 | — | — | INITIALISIERUNG sends first (let through); jobs not screened past it |
| `NVC_65` | 24 | 4 | 20 | 1 × 0x30 |  |
| `NVE_65` | 22 | 4 | 18 | 2 × 0x30, 2 × 0x31 |  |
| `PDC_65_2` | 22 | 2 | 20 | 8 × 0x30 |  |
| `PGS_65_2` | 59 | 13 | 46 | 32 × 0x30, 1 × 0x31 |  |
| `RLS_E65` | 19 | 2 | — | — | INITIALISIERUNG sends first (let through); jobs not screened past it |
| `SD_KWP` | 22 | 6 | 16 | 3 × 0x31 |  |
| `SZL_60` | 20 | 3 | — | — | INITIALISIERUNG sends first (let through); jobs not screened past it |
| `SZM_60` | 21 | 2 | 19 | 5 × 0x30 |  |
| `TELE60_2` | 20 | 3 | 17 | none |  |
| `VID_65_2` | 34 | 5 | 29 | 4 × 0x30, 4 × 0x33 |  |
| `VVT_B1_0` | 9 | 1 | 8 | none |  |
| `VVT_B2_0` | 9 | 1 | 8 | none |  |
| `ZBEH60` | 22 | 4 | 18 | 7 × 0x30 |  |
| `E60` | 3 | 0 | 3 | none |  |
| `UTILITY` | 3 | 0 | 3 | none |  |
| `TMODE` | 5 | 0 | 5 | none |  |
| `D_MOTOR` | — | — | — | — | group: identification let through; jobs on its variant |
| `D_PGS` | — | — | — | — | group: identification let through; jobs on its variant |
| `D_KBM` | — | — | — | — | group: identification let through; jobs on its variant |

**Session C's reads all pass:** `FS_LESEN` (0x18) on `NVE_65`, `CAS`, `KGM_60`, `KBM_60`,
`PGS_65_2`; `NVE_65` `IS_LESEN` and `CAS` `C_FA_LESEN` (0x22); `E60` `IDENT_FUNKTIONAL` (0x1A)
and `FS_LESEN_FUNKTIONAL` (0x18), both functionally addressed (`C2 EF F1 …`); vehicle status
sends nothing (`UTILITY`, `TMODE` are interface jobs). The groups' identification telegrams
pass. **Not screened past INITIALISIERUNG** (it sends `1A 80` first, which passes): `N62_TUE`,
`GS19B`, `RLS_E65`, `SZL_60`; so `D_MOTOR` `IDENT` and `FS_LESEN` stay in session C as Phase 6
ran them, and a refusal at the car would fail them safely (`[frame_refused]`).

**Phase 6's battery reads:** `KGM_60` history blocks 1–3, `CAS` `STATUS_WUP` and
`STATUS_PA_WUP`, `KOMB60` `STATUS_KL30_H_ZAEHLER` pass and are added to session C;
`IHKA60` `STATUS_VERBRAUCHERREDUZIERUNG` is refused (0x30) and stays out. `KOMB60`
`STATUS_KLEMMENSPANNUNG` (main's planned first voltage source) passes, so it is a voltage source
the native backend can use, unlike `NVE_65` `STATUS_BORDNETZSPANNUNG`; it is added to session C
to compare it against the VM.

**Found for the VM backend (no frame gate there):** two `STATUS_` jobs that policy version 4
allows and the description screen passes send **write** services. `ACSM60` (airbag)
`STATUS_ROC`, described "KWP2000: $22 ReadDataByCommonIdentifier", sends
`85 01 F1 2E 99 02 FF FF` (0x2E WriteDataByCommonIdentifier). `FLA_65` `STATUS_SENSORPARAMETER`,
"Auslesen diverser Imager-parameter", sends `83 5F F1 3B FA 01` (0x3B
WriteDataByLocalIdentifier). Neither has been run on the car. Natively the gate refuses both.
Whether to deny them in the policy is Ryan's call (main's policy); this branch does not change
the policy.

---|---|---|---|
| | | | |

## 3. Trip BC: `NVE_65 IDENT` with telegrams and echo timing, then session C (Ryan at the car)

One ignition-on session (Ryan, 2026-11-24). The VM runs B, then C. The cable moves once. Then
`ediabasd` runs B at 1 ms, B at 16 ms, then C. By Phase 6's timings each side takes under a
minute. Every command is in `~/m2/trip.sh`, one per step. A stop therefore always falls at a
job boundary, and Ryan can run the commands directly if Claude's link drops.

### Before

- [x] Plan tasks 5b (main merged), 6 (desk rehearsal) and 7b (gate screen) are done, and
      `dev/m2/session-c.json` holds only reads the screen passed.
- [x] In the VM, with Ryan's OK for this change: back up `C:\EDIABAS\Bin\EDIABAS.INI` and set
      `IfhTrace = 3` (as M0, section 4). Note the backup's SHA-256.
      Done 2026-11-24 by Ryan with `~/m2/ini-trace-on.ps1`, run over the dev shell. The backup
      `EDIABAS.INI.m2backup` is the original, SHA-256 `27CB98B3…F7AC1` (the same as M0's). The INI
      with `IfhTrace = 3` is SHA-256 `1F41E552…535E84E3`, the same 26,255 bytes, with one byte
      changed (offset 462, `0` → `3`).
- [x] Three configs, each with `ecuPath`:
      - `~/m2/ediabasd-1.json`: `recordDir` `~/m2/b/`, `latencyMs` 1.
      - `~/m2/ediabasd-16.json`: `recordDir` `~/m2/b/`, `latencyMs` 16.
      - `~/m2/ediabasd-c.json`: `recordDir` `~/m2/c/`, `latencyMs` 1.
- [x] `mac-mcp` built; `ediabasd` Release built. `trip.sh` runs each step under `caffeinate -i`;
      the Mac stays on power.
- [x] `~/m2/trip.sh` written; its `check` step passes. Ryan still has to read it through.

`~/m2/trip.sh <step>` runs the steps one at a time, in this order:
`check`, `vm-b`, `trace-b`, `vm-c`, `vm-idle`, `free`, `nat-b1`, `nat-b16`, `nat-c`, then, with
the ignition off, `trace-bc` and `restore-ini`. Each step appends to `~/m2/trip.log` and refuses
to run if its output already exists. Two changes from the plan:
- `trace-b` copies `IFH.trc` to `~/m2/b-ifh.trc` straight after the VM's B. `TraceSize` is 1024
  and loading `NVE_65` alone traced 47 KB in M0, so C's functional jobs could wrap the trace
  and lose B's telegrams before the copy after the trip.
- `vm-idle` (`~/m2/vm-idle.mjs`) polls the agent's `/health` until EDIABAS is released, so
  amendment 5's wait does not depend on Claude's link.

### Steps (Ryan's go)

1. **VM side:** cable ticked for the VM, charger on and its voltage noted. Ignition on; note
   the time. Then run:
   `node dev/car-compare.ts run --side vm --jobs dev/m2/session-b.json --out ~/m2/b-vm.jsonl`, then
   `node dev/car-compare.ts run --side vm --jobs dev/m2/session-c.json --out ~/m2/c-vm.jsonl`.
2. **Move the cable:**
   - Wait until `vm_health` shows EDIABAS not loaded (about 20 s after the last VM read).
   - Untick the cable in UTM.
   - Check that `$EDIABASD health --config ~/m2/ediabasd-1.json` says `free`.
3. **Native side:**
   `node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-1.json --jobs dev/m2/session-b.json --out ~/m2/b-native-1.jsonl`,
   the same with `ediabasd-16.json` and `b-native-16.jsonl`, then
   `node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-c.json --jobs dev/m2/session-c.json --out ~/m2/c-native.jsonl`.
   **Ignition off.** Note the total time.
4. If the 10 minutes run out: stop at a job boundary, switch the ignition off, and note the
   last job each side completed. The rest goes to another trip.

### Afterwards (ignition off)

- [ ] Copy `C:\EDIABAS\TRACE\ifh.trc` to `~/m2/bc-ifh.trc` over the dev shell.
- [ ] Restore `EDIABAS.INI` from the backup and check its SHA-256 against the backup's.
- [ ] Unplug the cable from the car, then take the charger off.

### Compare (desk)

- **Session B:**
  - `node dev/car-compare.ts compare ~/m2/b-vm.jsonl ~/m2/b-native-1.jsonl`, and the same for
    `-16`.
  - `node dev/telegrams.ts compare ~/m2/b-ifh.trc <the latency-1 recording>` (or `bc-ifh.trc`
    if `trace-b` could not copy). `bc-ifh.trc` also holds session C, so with it expect B's
    exchanges to match and then "the native side ended".
  - `node dev/telegrams.ts timing ~/m2/b/*.jsonl`.
- **Session C:**
  - `node dev/car-compare.ts compare ~/m2/c-vm.jsonl ~/m2/c-native.jsonl` (it counts IFH-0003
    per side).
  - `node dev/telegrams.ts timing ~/m2/c/*.jsonl` for short reads.

### Results: the trip

| | |
|---|---|
| Date, charger voltage, ignition-on time | |
| Completed: all, or the last job on each side | |
| `EDIABAS.INI` restored, SHA-256 matches | |

### Results: session B

| | |
|---|---|
| Results, native (1 ms) against VM | |
| Results, native (16 ms) against VM | |
| Telegrams: matched, first divergence | |

| Recording | Latency | Echoes | Echo ms min/median/max | Replies | Reply ms min/median/max | Short reads |
|---|---|---|---|---|---|---|
| | 1ms | | | | | |
| | 16ms | | | | | |

If latency 1 is no faster than 16, M0's open risk 3 is real; the fallback is a longer
`ObdAddRecTimeout`, for M3.

---

## 4. Session C: fault memory, vehicle status and the whole-car reads (from trip BC)

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
- The gate's real cost on this car is larger than M1's 24: 264 policy-allowed `STATUS_` reads
  on the 43 answering modules (section 2), 210 of them 0x30 with "report current state". A
  gate rule for 0x30 with that option (M1's suggestion) would recover most; it needs its own
  test cases, and the 0x30 option bytes are SGBD-specific, so each concept is checked first.
- `KOMB60` `STATUS_KLEMMENSPANNUNG` passes the gate: the native voltage source.
- Any telegram divergence found (sections 3 and 4).
