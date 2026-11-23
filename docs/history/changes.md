# What changed in 0.3.0 to 0.6.1

> Historical record: the release notes of earlier versions, moved from the README. For the current state, see the [README's Status](../../README.md#status).

## Phase 6 at the car (2026-11-23)

First session at the car, with 0.6.2, ignition on for every read. Acceptance (PLAN.md section 9):

- Ignition and a voltage in volts shown: **pass** (`STAT_ZUENDUNG` = 1; `NVE_65` `STATUS_BORDNETZSPANNUNG` = 12).
- `IDENT` through `D_MOTOR` names the engine SGBD: **pass** (`N62_TUE`).
- `ediabas_read_faults` on `NVE_65` shows A752/A751: **read passed, pair absent** from the car (not a bridge fault).
- Ignition-on under 10 minutes: **fail**, about 18 minutes, mostly a Claude Desktop link drop (mobile data).
- Responses saved as fixtures: **recorded** (`dev/fixtures/nve-faults`, `dev/fixtures/battery`).

Also seen: `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` took about 4 s each; the DME
holds `2EF4` and `2DED`; three pinned `CAS` approvals gave a negative response. Afterwards the
owner removed those approvals and the `MPM_60` one from the VM's `reviewedJobs` and set
`ediabasIdleReleaseSeconds` back to 20. Claude Desktop reads its servers from
`claude_desktop_config.json`, not from `claude mcp`. Details:
[the feedback response](../superpowers/specs/2026-11-23-phase6-feedback-response.md).

## What changed in 0.6.1

Hardening for the first session at the car, from a desk rehearsal of the at-the-car sequence
(2026-11-22, cable in the VM, not in the car). Only the Mac server changed; the agent is
rebuilt for the version number.

- **`ediabas_error` advice by EDIABAS code** (code table from EdiabasLib, checked against every
  code seen at the desk): the cable codes (`IFH-0002`, `IFH-0003`), low or wrong battery voltage,
  a module that did not answer (`IFH-0009`), a disturbed answer, ignition off and `SYS-0010` each
  say what to check. Cable, voltage and ignition codes no longer send Claude to
  `ediabas_vehicle_status`, and no `IFH-` code suggests a wrong job name.
- **`ediabas_vehicle_status` says its battery and KL30 readings come from the cable**: with no
  car connected they read `STAT_UBATT=1` and KL30 12000. Only a voltage source (a module) shows
  the car is awake. Fixture `dev/fixtures/no-car`.
- **The recording server runs on Node 26**, which removed `--experimental-transform-types`:
  `mac-mcp/src` and `dev/` now use only syntax Node can strip (`erasableSyntaxOnly`).
- `docs/at-the-car.md` has the tested commands to register the recording server (the name goes
  before `-e`) and a check that the folder fills.

## What changed in 0.6.0

- **The project is now bimmer-bridge** (it was ediabas-bridge). The MCP server registers as
  `bimmer-bridge`, so its tools are `mcp__bimmer-bridge__*`, and the skill is `bimmer-bridge`.
  Re-register the server and reinstall the skills: see
  [docs/install.md](../install.md#upgrading-to-060).
- Names an existing install depends on keep the old name, so the VM agent needs no reinstall:
  `C:\ProgramData\EdiabasBridge`, the task and firewall rule, the Keychain service, the Mac audit
  log folder and the agent's handshake.
- The README is shorter and starts with a use-at-your-own-risk disclaimer.
- Licensed under MIT ([LICENSE](../../LICENSE)).

## Checks of 0.5.0 in the VM and the phase table

0.5.0 in the VM, 2026-11-22 (cable in the VM, not in the car):
- Approvals pinned before the update; `vm_health` then showed 5 pinned, 0 ignored, and policy
  version 4 with the same SHA-256 on both sides.
- `update.ps1` under Windows PowerShell 5.1: the first Update added `WINKFP` and `ISTAGUI` and
  recorded `conflictDefaultsOffered`, with `reviewedJobs` and the other keys unchanged; a second
  Update changed nothing; a default removed by hand stayed removed after a third.
  `vm-setup/*.ps1` also parse under `pwsh` 7.7 on the Mac.
- `--selftest`: PASS (after the self-test's own 8.3 check was fixed, PR #2).
- Desk checks: `TMODE INFO`; `ediabas_vehicle_status` (TMODE lines; `NVE_65` voltage from the
  Mac's `config.local.json`); INPA open gives `cable_in_use`; an INPA screenshot is readable;
  `ediabas_describe E60` shows `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` runnable; a
  `reviewedJobs` edit shows `[approvals changed]` first on a result and after the `[code]` on an
  error; with the car absent, the info memory line gives the real no-response error. Stopping a
  running job when a BMW tool opens mid-job (`apiBreak`) is covered by the agent's tests only: a
  desk-side describe is too short to open INPA during it.
- Restart checks: after a VM reboot the agent came back by itself at log-on, and the running
  Mac server reconnected without a restart. After the Mac slept, the agent and the bridge stayed
  up, but the cable was handed back to the Mac: a `TMODE` job then failed with error 48 until the
  cable was ticked for the VM again in UTM. The error hint for 48 now says so.

| Phase | State |
|---|---|
| 0 Discovery | done (2026-11-21) |
| 1 Agent skeleton | done (2026-11-21): installed in the VM, self-test passes, came back after a Windows restart |
| 2 EDIABAS at the desk | done (2026-11-21, agent 0.2.0): every desk check passes. Agent 0.2.2 deployed and used at the desk on 2026-11-22 |
| 3 Mac MCP | done: the ten tools of 0.2.x (`ediabas_jobs`, the eleventh, came in 0.3.0). Checked in Claude Code and in the desktop app (`ediabas_describe` on `NVE_65`, 2026-11-21) |
| 4 Screenshots | done (2026-11-21): INPA screenshot readable, locked screen refused |
| 5 Logs | done (2026-11-21): listing, reading and every path refusal pass, in the VM self-test too |
| 6 At the car | built (vehicle status, fault tables). **Not run: this is the owner's, at the car** |
| 7 Skills | both skills installed; replay agent with `idle` and `inpa-open` fixtures; evals 02, 05, 07 and 08 passed with the skills (2026-11-21), but for 07 and 08 the run without the skills was not shown to do worse, so PLAN.md's acceptance is not met for them; eval 04 passed 3 of 3 with the skills and failed 3 of 3 without them (2026-11-22, `needs-review` fixture); skill references filled from the desk recordings of 2026-11-21/22 |
| 8 Hardening | built: `VERSION`, `release.sh`, the docs. The plan's acceptance check (reinstalling from this README alone) has not been run; updates from the README's lines have |

Open:
- **Phase 6 at the car**, the owner's: [docs/at-the-car.md](../at-the-car.md).
- **Evals 01, 03 and 06** wait for the `nve-faults` and `battery` fixtures, recorded at the car
  ([dev/fixtures/README.md](../../dev/fixtures/README.md)).
- **Eval acceptance**: 07 and 08 still need a run without the skills that does worse.
- **Skill reference entries that need car recordings**: the vehicle-status field names, the
  engine SGBD, and the SGBDs that can only be described with the control unit answering.

## What changed in 0.5.1

- After the Mac sleeps, UTM hands the cable back to the Mac. `ediabas_error` 48
  (`IFH-0038`) now says the cable may not be attached to the VM, as error 16 does after a replug,
  instead of only blaming the ECU. Mac server only.
- `global.json` pins the .NET SDK to 10.0.x, and `release.sh` / `vm-agent/build.sh` prefer the
  `dotnet@10` keg and check the version, so a newer `dotnet` on `PATH` is refused, not used.
- The agent's code is unchanged since 0.5.0; its version moves with the others, so the Update
  line brings it to 0.5.1. Skill references: job verdicts under policy version 4.

## What changed in 0.5.0

- **Pin your approvals before updating.** Only pinned entries (`ECU:JOB@hash`) approve anything;
  unpinned ones are ignored. `ediabas_describe` prints the line to paste. Steps:
  [Upgrading to 0.5.0](../install.md#upgrading-to-050).
- Approvals are listed in `vm_health`; a change prints `[approvals changed]`; tools refuse with
  `[policy_mismatch]` if the Mac's and the VM's policy files differ.
- Policy version 4: `E65`, `E70`, `E89X`, `F01`, `R56` restricted like `E60`; switching and
  triggering words added to the description screen.
- The agent stops a running job (`apiBreak`) within about 250 ms if a BMW tool starts, and checks
  again just before `apiInit`. `WINKFP` and `ISTAGUI` join the default conflict list (Update adds
  the defaults a `config.json` was never offered, records them in `conflictDefaultsOffered` so
  one you remove stays removed, and keeps `config.json.bak`).
- The Mac sends the token only to an address that proves itself with `GET /hello` in the same
  call; the VM is found from its newest DHCP lease on UTM's network only.
- A job the SGBD lacks is `ediabas_error` again (`job_not_found` only comes from
  `ediabas_describe` now). This reverses a 0.3.0 item.
- `ediabas_vehicle_status` runs as one jobs batch from the Mac. Move `voltageSources` from the
  VM's `config.json` to the Mac's `config.local.json` and delete the VM key (the agent warns
  while it is there).
- `/logs/read` refuses a log file that is itself a link; a failed `apiEnd` shows in `vm_health`;
  screenshots fall back to the screen copy for a window that does not answer in 500 ms.
- Update: backs up the exe and rolls back on failure; the firewall rule is re-pinned to the
  current gateway.
- The recorder moved to `dev/` (`dev/record-server.ts`).
- A 0.5.0 Mac server needs a 0.5.0 agent: `vm_health` warns on a version difference, and a 0.4.0
  agent answers `/hello` with 401, which shows as `[agent_outdated]`.

## What changed in 0.4.0

- Usable by anyone with an EDIABAS-supported BMW: no owner name in messages, docs or skills;
  see [Which cars](../../README.md#which-cars).
- The VM's MAC has no built-in default: it is read from UTM's config for the VM named
  `Windows` (or `vmName`), with an optional git-ignored `config.local.json` (`vmName`, `vmMac`,
  `agentPort`) and the `EDIABAS_VM_*` environment variables as overrides.
- The agent's `voltageSources` default is empty (it was the E60/E61 night-vision module);
  existing `config.json` files are unchanged.
- `discover.ps1` no longer checks for E61-specific SGBD files.

## What changed in 0.3.0

From the review round recorded in [plan-review-fixes.md](plan-review-fixes.md):

- Policy version 3: `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` (whole-car ident and
  faults), `C_FA_LESEN` (vehicle order), more description-screen terms, stricter parse rules.
- New tool `ediabas_jobs`: up to 20 read jobs in one call.
- Every error starts with its `[code]`; new codes `ediabas_request_lost` and `ediabas_busy`.
- EDIABAS calls leave the Mac one at a time; a connect timeout counts as never sent.
- Car-facing results end with a car-session line (time since the first read from the car).
- `vm_health` shows the Mac server's version beside the agent's, and warns when they differ.
- Approvals in `reviewedJobs` can be pinned to the job's description (`ECU:JOB@hash`);
  `ediabas_describe` prints the line, and the agent re-reads them without a restart.
- Job arguments go through the description screen; a job the SGBD lacks is `job_not_found`.
- Queue time counts against a request's time limit; unreadable results get their own message.
- Screenshot errors `capture_busy` and `capture_failed`; one 5 s limit per log grep; one set of
  log roots for listing and reading.
- No tool result exceeds 60 000 characters.
- Deploy: the token is never printed; uninstall keeps data unless `$Purge=$true`;
  `release.sh` refuses a dirty tree and the zip carries `commit.txt`.

