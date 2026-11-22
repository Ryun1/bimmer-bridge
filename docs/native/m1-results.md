# Native engine M1: `ediabasd` (.NET) at the desk

Plan: [M1 plan](../superpowers/plans/2026-11-22-native-engine-m1.md). Design:
[native engine](../superpowers/specs/2026-11-22-native-engine-design.md), "Milestones and
acceptance", M1, and its "M1 amendments". M0's results: [m0-results.md](m0-results.md).

M1 builds `ediabasd`: EdiabasLib, unmodified, on the Zig cable layer `libkline`, behind the
VM agent's service and policy, with a JSON-lines stdio front and a CLI. The acceptance list:

1. Both policy case files pass.
2. The frame gate's unit tests pass; it fails closed (no concept declared, unknown concept,
   refused SID, `F1→F1` writes), latches, and surfaces as `frame_refused` even when the SGBD
   masks the error.
3. `describe` matches the VM agent's for every `.prg` in the Ecu folder (scripted, both
   backends). `.grp` names give `needs_car` until the variant cache has an entry.
4. Upstream's `coverage.bat` job list on upstream's test SGBDs (used in place from the
   submodule) gives the same result sets through real EDIABAS (VM) and `ediabasd`.

**Nothing in M1 talks to the car.** Sections 1–2 are tests. Sections 3–4 run EDIABAS in the VM
and are desk sessions: Ryan unplugs the cable from the car first (it may stay in the VM) and
starts each one. Claude never runs EDIABAS jobs, Tool32 or INPA over SSH.

Status: sections 1–2 done (2026-11-22). Sections 3–4 wait for the desk sessions.

---

## 1. Policy case files (Claude)

`ediabasd` uses `EdiabasAgent.Core`'s `JobPolicy.Embedded`, the same embedded
`policy/policy.json` as the VM agent. `engine/ediabasd-net.Tests/PolicyCaseTests.cs` runs both
shared case files against the policy instance `EdiabasdHost` actually uses:

| File | Cases | Result |
|---|---|---|
| `policy/policy-cases.json` (allowed, rule, screen) | 150 | all pass |
| `policy/policy-parse-cases.json` (valid and invalid policy texts) | 75 (7 valid, 68 invalid) | all pass |

The VM agent's own `JobPolicyTests` still pass on the same files (`vm-agent/EdiabasAgent.Tests`,
1,292 tests).

## 2. The frame gate (Claude)

| Requirement | Test |
|---|---|
| No concept declared: refused | `gate.zig` "no concept refuses"; `session.zig` "send refused before any concept, and the refusal latches"; C# `KlineInterfaceTests.TransmitData_NoConcept_Refused` (a plain `EdInterfaceObd` never forwards the concept, so libkline refuses) |
| Unknown concept: refused | `gate.zig` "unknown concept refuses" |
| Refused service IDs, physical and functional | `gate.zig` "every listed sid refused", "bmw-fast clear dtc refused physical and functional", "default session allowed, other sessions refused", both long-length forms; "same rules for kwp2000\*, kwp2000 bmw and d-can" |
| Reads allowed | "bmw-fast read ident allowed", "read sids allowed", "ds2 frames allowed" |
| `F1→F1` adapter writes refused, FA/FD/FE reads allowed | "adapter reads allowed, adapter writes refused" |
| Truncated frames refused | "truncated header refuses as malformed", "zero-length data is malformed" |
| A refusal latches until close | `session.zig` "send refused before any concept, and the refusal latches"; C# `TransmitData_ClearFaults_RefusedAndLatched` |
| Applies to replay too | "gate applies in replay too" |
| Surfaces as `frame_refused` even when the SGBD masks the error | `NativeEdiabasApiTests.GateRefusal_FailsJobEvenWhenMasked` (a fake cable reporting `gate_refused` under a job that ends ready), `GateRefused_FailsAReadyJob`. Not end to end: none of upstream's allowed test SGBDs declares a concept and then sends a refused service ID under a trap mask (with no concept declared, EdiabasLib refuses `xsend` itself before the transport). M2's recordings can supply one; Core `NativeErrorMapTests` maps 9005 to `frame_refused` |
| Fails the whole request; nothing more is started | Core `EdiabasServiceTests.FrameRefused_FailsTheWholeBatch_AndStartsNothingMore`, `FrameRefused_FailsAVehicleRead`; the latch survives the next SGBD's `xconnect` (`KlineInterfaceTests.Latch_SurvivesAnotherConnect`, `Latch_SurvivesDisconnectAndConnect`) |
| KWP2000\* framing (length at byte 3, service ID at byte 4) | `gate.zig` "kwp2000\* (0x010D) has its length at byte 3 …" (found by the final review: the gate had read 0x010D as BMW-FAST) |

Suites at the end of M1 (after the final review's fixes): Zig 62 pass + 3 skip (macOS) in the
library, 7 CLI, 6 C ABI; `engine/EdiabasLib.Native.Tests` 16; `engine/ediabasd-net.Tests` 67;
`vm-agent/EdiabasAgent.Tests` 1,295.

Found while building: no upstream test job fails through the interface. `cmd_test2`'s
`TEST_IFACE_FLAGS` traps every interface error and ends ready, which is also a real example of
an SGBD masking a send error.

### The gate against real reads (Claude, 2026-11-22, no device)

A scratch probe (not committed) took 87 of Ryan's SGBDs: the eight E61 modules, all 28 KWP2000\*,
the one KWP2000 BMW, and samples of DS2 (25), D-CAN (10) and BMW-FAST (15). For each one it
ran every job that the policy allows and that takes no argument, through the production path
(`NativeEdiabasApi` → EdiabasLib → `KlineInterface` → libkline's gate) into an **empty replay**,
so nothing reached a device. What libkline saw first, per job:

| Outcome | Jobs |
|---|---|
| The gate let the first telegram through (the replay then refused it, as it is empty) | 1,381 |
| Nothing was sent | 135 |
| **The gate refused it** | **54** |

Every refusal is service **0x30** (input/output control, 41 jobs; 21 of them with control option
01, "report current state") or **0x31** (start routine, 13 jobs), read correctly under the SGBD's
own concept. None is a malformed or misread frame, so the stricter gate from the final review
refuses no legitimate frame shape. But **24 of them are policy-allowed `STATUS_` reads on this
car's own modules**:
- `NVE_65`: `STATUS_TASTER`, `STATUS_BORDNETZSPANNUNG` (0x30), `STATUS_PAIRING`,
  `STATUS_FSC_LESEN` (0x31)
- `CAS`: `STATUS_CAS_DIAGNOSE` (0x30), `STATUS_KL15_ABSCHALTUNG`, `STATUS_WAKEUP_HISTORY` (0x31)
- `LM_60`: 17 `STATUS_` jobs (0x30, report current state)

This is the design's open risk 5 happening for real: the gate fails these reads safely, but
natively they cannot run (on the VM they can). `NVE_65 STATUS_BORDNETZSPANNUNG` is also the
`voltageSources` example in docs/api.md.

**Decision (Ryan, 2026-11-22): keep the gate strict for now.** The SID list is unchanged; these
24 reads stay VM-only until revisited. Any later change (0x30 with option 01 only, or named
0x31 routines) is made with its own test case.

## 3. Describe for every `.prg` (desk session)

**Before:** the cable unplugged from the car (it may stay in the VM). Ryan says go.

```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec
/opt/homebrew/opt/dotnet@10/bin/dotnet build engine/ediabasd-net
(cd mac-mcp && npm ci && npm run build)
node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out ~/m1-describe-diff.jsonl
```

The output stays outside the repo. A run can be stopped and resumed: ECUs already in the file
are skipped. 1,251 `.prg` files; at a few seconds each on each side this takes about two hours.
Try `--only NVE_65 --only E60` first.

Expected `vm_error` rows: real EDIABAS runs each SGBD's INIT before `_JOBS` (M0 section 4). For
an SGBD whose INIT sends a telegram (DS2 wake-up, for instance) that fails in the VM while the
cable is not in a car, where the native describe (no INIT, by design) succeeds.

| Kind | Count |
|---|---|
| `match` | |
| `differ` | |
| `vm_error` | |
| `native_error` | |
| `both_error` | |
| **total** | |

Every `differ` and `*_error` row, explained:

| ECU | Kind | What differs | Why |
|---|---|---|---|

Groups (`.grp`) are not compared: natively they give `needs_car` until a live request has
resolved them (353 `.grp` files in the folder).

## 4. Coverage, real EDIABAS against `ediabasd` (desk session)

`dev/coverage/jobs.json` is `coverage.bat`'s runs on upstream's own test SGBDs: 217 jobs in 125
runs (lines 89–160, both `for` loops expanded, `TEST_TIME_FLAGS` left out because its argument is
the current time). The Mac side runs them through `ediabasd`'s engine (EdiabasNet with
`KlineInterface` on an empty replay); the VM side through `api32.dll`.

**Ryan's OK is needed** (M1 plan, decision 8): the VM runner declares `apiInitExt`,
`apiSetConfig`, `apiJobData` and `apiJobExt`, which the agent never does, and sets `EcuPath` for
its own process only. `EDIABAS.INI` and `C:\EDIABAS` are not touched.

1. Mac: build and run the Mac side, and build the VM runner.
   ```sh
   export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec; D=/opt/homebrew/opt/dotnet@10/bin/dotnet
   $D run --project dev/coverage -- run --out ~/m1-coverage-mac.jsonl
   $D publish dev/coverage-vm -r win-x86 -c Release -o ~/m1-coverage-vm
   ```
2. Claude copies to the VM over the dev shell, into `C:\bimmer-bridge-m1\`: `CoverageVm.exe`,
   `jobs.json`, and into `Ecu\` the test files `cmd_test1.prg`, `cmd_test2.prg`, `base1.prg`,
   `base2.prg`, `cmd_ident.grp`, `test_file1.txt`, `test_file2.txt` (upstream's, from the
   submodule; `cmd_test2` reads the `base` files, `TEST_FILES` the text files).
3. Ryan, at the VM's desktop (not over SSH), cable in the VM but not in the car:
   ```
   C:\bimmer-bridge-m1\CoverageVm.exe --ecu C:\bimmer-bridge-m1\Ecu --jobs C:\bimmer-bridge-m1\jobs.json --out C:\bimmer-bridge-m1\vm.jsonl
   ```
4. Claude copies `vm.jsonl` back, runs the comparison, then deletes `C:\bimmer-bridge-m1\`.
   ```sh
   $D run --project dev/coverage -- compare ~/m1-coverage-vm.jsonl ~/m1-coverage-mac.jsonl
   ```

Expected differences: `TEST_IFACE_FLAGS` (the VM has a real interface, the Mac an empty
replay); error codes EDIABAS 7.3 does not have (EdiabasLib emulates 7.7.3, so `SEC-xxxx` and
`IFH-0069`/`IFH-0074` may come back differently).

| | Count |
|---|---|
| Jobs | 217 |
| Matched | |
| Differing | |

Every difference, explained or fixed:

| Run#index | Job | What differs | Why / fix |
|---|---|---|---|

## 5. For M2

- **Gate and 0x30/0x31 reads: strict for now** (Ryan, 2026-11-22). 24 `STATUS_` reads on
  NVE_65, CAS and LM_60 are refused natively and stay on the VM; M2's comparisons skip them,
  and a `voltageSources` entry on one of them gives `frame_refused` natively.

- The MCP server's native backend (`EDIABAS_BACKEND=native`) and `bridge_health`, with the
  cable's free/locked state.
- M2 starts with the echo timing test (M0 section 6), then `IDENT` on `NVE_65`.
- `reviewedJobs` are read when `ediabasd` starts (Core's reloader reads the VM's config shape).
- Linux: SGBD names are resolved case-insensitively by the file system on macOS only; check on
  Linux when it is picked up again.
