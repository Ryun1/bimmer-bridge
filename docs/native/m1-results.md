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

Status: done. Sections 1–2 (2026-11-22), sections 3–4 (desk sessions, 2026-11-23).

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

Run 2026-11-22/23, the cable passed through to the VM and not in a car. The run stalled
overnight (the Mac slept) and the VM agent then stopped accepting connections; its rows from
then on were all timeouts, were dropped, and the run was resumed after the agent's scheduled
task was restarted (Ryan's OK). A last pass re-ran the 83 SGBDs that the fix below changed.

| Kind | First run | After the fix | |
|---|---|---|---|
| `match` | 839 | **906** | identical names, comments, arguments, results, variant and verdicts |
| `verdict_only` | 26 | 31 | everything identical but a review verdict: policy drift, below |
| `differ` | 162 | 90 | every one: comments the VM agent did not fetch in time, below |
| `vm_error` | 218 | 218 | below |
| `native_error` | 0 | 0 | |
| `both_error` | 6 | 6 | below |
| **total** | 1,251 | 1,251 | |

**Found and fixed: job descriptions written in lower or mixed case (83 SGBDs).** EdiabasLib keys
each job's description block by the name as the SGBD writes it (`JOBNAME:initialisierung`,
`Start_Bus_Communication`, `FS_LESEN_sl`, `Notruf_Status`) and looks it up in upper case, so
`_JOBCOMMENTS`, `_ARGUMENTS` and `_RESULTS` came back empty for those jobs where real EDIABAS
finds them: 107 jobs, 69 of them `INITIALISIERUNG`. It failed safe (no comment means
`needs_review`, never `passed`) but held harmless reads for review. `ediabasd` now rekeys the
descriptions case-insensitively before every `_` job (`JobDescriptions.IgnoreCase`, by
reflection like `EdiabasFactory.Activate`; EdiabasLib unmodified). After the fix, no job on
any SGBD has a comment on the VM that it lacks natively.

Every remaining non-match, explained:

| ECUs | Kind | What differs | Why |
|---|---|---|---|
| 90 (AFS_60, CAS, DDE41KR0, MSS52DS0, …) | `differ` | jobs with a comment natively and none on the VM (4,800+ jobs), and the verdicts that follow from it (a job with no comment is `needs_review`) | The VM agent fetches each job's comments with its own `_JOBCOMMENTS` call, and real EDIABAS runs the SGBD's INIT before each one; with no car every INIT waits out its timeouts, and the agent stops at its 60 s request limit (90 "still busy … calling apiBreak" lines in its log). The native describe, without INIT by design, returns them all. Not a native difference; these comments are unchecked against the VM |
| 31, and verdicts inside 17 `differ` rows | `verdict_only` | `needs_review` on the VM, `passed` natively | Policy drift: the VM agent runs main's policy v4, which adds the screen terms `schaltet`, `einschalt`, `ausschalt`, `umschalt`, `ausloes`, `auslös`, `trigger`, `oeffnen`, `öffnen`; this branch has v3. Each case checked contains one (`STATUS_PAIRING` "freigeschaltet", `STATUS_LAMBDA_COUNTER` "Einschaltbedingung", `STATUS_T_MV_OFF` "Ausschalt…", `STATUS_TRIPRCRDKONF_LZQ` "Trigger") |
| CAS (2 jobs) | inside `differ` | `reviewed` on the VM | The VM's `reviewedJobs` list approves them; the comparison's `ediabasd` config has none |
| 215 | `vm_error` (`ediabas_error`) | the VM fails, native succeeds | Expected (above): INIT fails with no car (`SYS-0010` INITIALIZATION ERROR, `IFH-0006`, `IFH-0003` in the agent log) |
| MS411DS2, N55_ALP, N73TU_R0 | `vm_error` (`ediabas_request_lost`) | the VM request lost | Around the agent's stall; the same INIT waits |
| A-SITZ_B, A-SITZ_F, `KOPIE VON C_ACC` (3) | `both_error` (`job_not_allowed`) | both refuse | File names outside the policy's name pattern (`-`, spaces); same answer on both sides |
| 00MDS410 | `both_error` (`ediabas_error`) | both fail | Same answer on both sides |

Not covered by this run: the comments of the 90 SGBDs above (unchecked, not mismatched), and
the 24 gate-refused reads (section 2), which describe does not run.

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

Run 2026-11-23: the VM side through EDIABAS 7.3's `api32.dll` (cable passed through to the VM,
not in a car), the Mac side through `ediabasd`'s engine on an empty replay. `C:\bimmer-bridge-m1\`
was deleted afterwards.

| | Count |
|---|---|
| Jobs | 217 |
| Matched exactly | 60 |
| Matched except for EdiabasLib's extra `GRUPPE` and `FAMILIE` in set 0 | 89 |
| Differing in substance | 68 |

**`GRUPPE` and `FAMILIE` (95 jobs, 190 lines of the report).** EdiabasLib adds both system
results to set 0 of every job; EDIABAS 7.3 has neither. Additions only: every other system
result and value in those sets is the same.

Every other difference, explained. No engine fix is needed: none of them is a bug in
libkline, the transport binding or `ediabasd`, and EdiabasLib stays unmodified.

| Run#index | Job | What differs | Why |
|---|---|---|---|
| `runtimeerr_350`–`399` (50 jobs) | `TEST_RAISE_RUNTIMEERR#350`…`399` | VM `BIP-0001`, Mac `SEC-0000`…`SEC-0049` | Expected: the `SEC-xxxx` codes are newer than EDIABAS 7.3 |
| `raise_error_28`, `_29` | `TEST_RAISE_ERROR#28`, `#29` | VM `BIP-0000`, Mac `IFH-0069`, `IFH-0074` | Expected: codes 7.3 does not have |
| `raise_error_8` | `TEST_RAISE_ERROR#8` | VM `BIP-0000`, Mac `BIP-0011` | Same kind: 7.3 maps trap bit 8 to no specific code |
| `results11#32` | `TEST_IFACE_FLAGS` | `XBATT` 12000 vs 0, `XSTATE` 0x20 vs 0x30 | Expected: a real interface on the VM, an empty replay on the Mac |
| `results10#2` | `TEST_SREG` | `CFGSG_ECUPATH*` | Expected: different Ecu folders |
| `results10#2` | `TEST_SREG` | `STRCAT2A_Y`, `STRCAT2B_Y` null on the VM | 2 KB binary results: 7.3's `apiResultBinary` refuses results that size; the text forms match |
| `results10#10` | `TEST_FILES` | Mac `BIP-0006` | The job builds `EcuPath + "\\test_file1.txt"`, a Windows path, which `fopen` cannot open on macOS |
| `results11#20`–`#23` | `TEST_FSUB/FADD/FMUL/FDIV_FLAGS` | Mac `BIP-0011` | The last case computes 1.2e308 + 1.3e308 (infinity). EdiabasLib emulates EDIABAS 7.7.3, which raises `REAL ERROR` there (trap bit 8 not masked); 7.3 carries on |
| `results15#0` | `TEST_DIVS_ZERO` | Mac `BIP-0007` | Divide by zero with every trap masked: from 7.6 on (EdiabasLib's `IsMinVersion760`, a compile-time constant 7.7.3) it is raised regardless of the mask; 7.3 carries on |
| `results11#29` | `TEST_TABLE_FLAGS` | `COLS_INIT`/`ROWS_INIT` 2/6 vs 0 | `tabcols`/`tabrows` before any `tabset`: 7.3 reports a table left from earlier, EdiabasLib none |
| `results21#0`, `results23#0`, `#4` | `TEST_MULTIARG` | formats `D`, `X`, `08X` of negative values: 7.3 two's complement, EdiabasLib `0` | EdiabasLib's `FormatResult`. `NativeEdiabasApi` formats only with `""`, so this never reaches the API |
| `results12#0`, `#1` | `TEST_SHMID#ARGS##STDARGS` on `cmd_ident.grp` | VM `SYS-0013` UNEXPECTED RESULT, Mac ok | Group resolution: 7.3 rejects the test group's `IDENTIFIKATION` results (it returns `FAMILIE` beside `VARIANTE`), EdiabasLib accepts them |
| `results17_8#1` | `TEST_SHMID` on `cmd_test1` after `cmd_test2` | VM `SYS-0018` END JOB ERROR, Mac ok | `cmd_test2`'s exit job raises an error when the SGBD changes: 7.3 reports it on the next job, EdiabasLib ignores it |

What this means for real reads:
- **Divide by zero:** an SGBD computing a status value by integer division with every trap
  masked fails natively where 7.3 would return a value. This fails safe (an error, never a
  wrong value). Watch for `BIP-0007` in M2's comparisons.
- **Float overflow to infinity:** the same, but no real status calculation reaches 1e308.
- **Group resolution** accepting what 7.3 rejects: `.grp` names are `needs_car` natively until
  a live request resolves them. M2 compares the resolved variant against the VM's.

## 5. For M2

- **Gate and 0x30/0x31 reads: strict for now** (Ryan, 2026-11-22). 24 `STATUS_` reads on
  NVE_65, CAS and LM_60 are refused natively and stay on the VM; M2's comparisons skip them,
  and a `voltageSources` entry on one of them gives `frame_refused` natively.

- **EdiabasLib emulates EDIABAS 7.7.3; the VM runs 7.3** (section 4). A divide by zero under a
  full trap mask fails natively; M2's comparisons flag `BIP-0007` and `BIP-0011` as this
  difference, not a transport fault.

- **Policy drift:** main has policy v4, this branch v3 (section 3). Merging main in, or taking
  v4 over, makes the describe verdicts match.

- **VM agent, not the native engine:** after the Mac slept mid-run the agent logged thread-pool
  starvation and then stopped accepting connections while its process stayed up; only a
  restart of its scheduled task recovered it (section 3). Its describe also drops comments past
  its 60 s limit on SGBDs whose INIT waits. Both belong to the agent's own work.

- The MCP server's native backend (`EDIABAS_BACKEND=native`) and `bridge_health`, with the
  cable's free/locked state.
- M2 starts with the echo timing test (M0 section 6), then `IDENT` on `NVE_65`.
- `reviewedJobs` are read when `ediabasd` starts (Core's reloader reads the VM's config shape).
- Linux: SGBD names are resolved case-insensitively by the file system on macOS only; check on
  Linux when it is picked up again.
