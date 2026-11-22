# Phase results (historical)

> Historical record: Phase 0 to 5 results as recorded on 2026-11-21. For the current state, see the [README's Status](../../README.md#status).

What was found and checked in Phases 0 to 5, recorded at the time (2026-11-21). Versions and counts are those of the day (agent 0.2.0 at the Phase 2 checks); the self-test, for one, has gained checks since.

## Phase 0 findings (VM discovery, 2026-11-21)

Facts the later phases build on. Source: `vm-setup/discover.ps1` run in the VM.

| Item | Finding |
|---|---|
| Windows | Windows 11 Home 24H2 (build 26100), ARM64, 4 cores, 4 GB |
| User | the owner's account, an administrator. **UAC is switched off** (`EnableLUA = 0`), so every process, including the agent, runs elevated whatever run level the task asks for |
| PowerShell | Windows PowerShell 5.1 only; no PowerShell 7, no .NET runtime (the agent is self-contained) |
| Network | one adapter, `192.168.64.3/24`, gateway `192.168.64.1`, category Public |
| ISTA | **not installed** (no `ISTAGUI.exe`, no `C:\Rheingold`, no `C:\ProgramData\BMW`) |
| EDIABAS | 7.3.0 in `C:\EDIABAS`. `Bin\api32.dll` is 32-bit x86. `Bin\api64.dll` also exists. Config in `Bin\EDIABAS.INI` |
| `Api.h` | present at `C:\EDIABAS\Api\WIN32\Api.h`; a copy is in `discovery/` (not committed). It is the source of truth for Phase 2's P/Invoke |
| EDIABAS host process | `C:\EDIABAS\Bin\ebas32.exe` exists. Phase 2 must check whether it stays running after `apiEnd` |
| EDIABAS settings | `Interface = STD:OBD`, `EcuPath = C:\EDIABAS\ECU`, `TracePath = C:\EDIABAS\TRACE`, `Simulation = 0`, **`ApiTrace = 0` and `IfhTrace = 0`** (no traces are being written) |
| `obd.ini` | `Port=Com1`, `Hardware=USB`, `RETRY=ON`, no `UBATT` line |
| Cable | **no COM port present** at discovery time (cable not passed through to the VM) |
| SGBD files | 1,251 `.prg` and 353 `.grp` in `C:\EDIABAS\ECU`. `TMODE`, `UTILITY`, `NVE_65`, `N62_TUE`, `ME9N62_2` and `D_MOTOR` are all present |
| INPA | 5.0.6, `C:\EC-APPS\INPA\BIN\INPALOAD.exe` |
| Tool32 | 4.0.3, `C:\EDIABAS\Bin\Tool32.exe` |
| NCS Expert | 4.0.1, `C:\NCSEXPER\BIN\NCSEXPER.exe` |
| WinKFP | 5.3.1, `C:\EC-APPS\NFS\BIN\winkfpt.exe` |
| NCS Dummy | `C:\NCS Dummy\NcsDummy.exe` (does not use the cable) |
| BMW Coding Tool | `C:\BMW Coding Tool v250\BMW Coding Tool.exe` |
| Shortcuts | none is flagged "run as administrator" (moot with UAC off) |
| Desktop zip | `VRRTuning_2021_BMWTOOLPACK.zip`, the installer pack the tools came from |
| Trace folders | `C:\EDIABAS\TRACE` holds one empty file; `C:\NCSEXPER\WORK` has no module trace yet |
| Guest agent | QEMU guest agent and SPICE tools are running, so `utmctl ip-address` works |
| Power | sleep after 15 min on AC (the installer sets this to never); display off after 5 min; no screen-lock timeout configured |

### What this changed

- **Elevation:** the plan's "not elevated by default" cannot hold on this VM while UAC is off.
  The agent's folder permissions therefore don't constrain it. This is a property of the VM,
  not something the bridge should change.
- **Conflict list:** `INPALOAD`, `TOOL32`, `NCSEXPER`, `WINKFPT` and `BMW Coding Tool` are all
  confirmed process names, and match the installed config.
- **Logs (Phase 5):** with EDIABAS tracing off there is little to read. Turning it on means
  editing `EDIABAS.INI`, which the safety rules forbid the bridge to do; it would be the owner's choice.
- **Phase 2 desk test:** `TMODE.PRG` is present, so the no-cable test is available.

## Phase 1 and 3 acceptance results (2026-11-21)

| Check | Result |
|---|---|
| `/health` from the Mac with the token | 200 |
| without a token, or with a wrong one | 401 |
| unknown path / wrong method (with token) | 404 / 405 |
| listeners in the VM (`netstat`) | one, on `192.168.64.3:8765` |
| `EdiabasAgent.exe --selftest` | PASS, 5 of 5 (config, token ACL, bind address, firewall rule, listener) |
| `update.ps1` | replaced the exe and restarted the agent |
| Windows restart | agent back about 30 s after going down, in session 1 |
| token hand-off | installer stored it in the Mac's Keychain; nobody copied it |
| MCP tools against the live VM | `vm_health` and `vm_list_windows` both correct |
| fresh Claude Code session ("is the VM up?") | used the tools unprompted and answered correctly |
| Claude desktop app ("is the BMW VM up?") | called `vm_health` and answered correctly, after `node` was allowed under macOS Local Network |

Found and fixed during acceptance:
- The self-test rejected the range form (`192.168.64.1-192.168.64.1`) in which Windows reports
  a single-address firewall rule.
- **macOS Local Network permission.** The desktop app starts the server in a way that makes
  macOS treat `node` as its own app, so the first call shows "node would like to find devices
  on your local network" and fails until it is allowed (System Settings > Privacy & Security >
  Local Network). The server used to report this as "VM not running"; it now reports
  `local_network_blocked` with the fix, and logs the underlying failure to stderr. The
  permission is tied to the `node` binary's path, so a Homebrew upgrade of node can prompt again.
- With only the server's built-in notes (no skill), the desktop app offered screenshots and
  car reads that are not built. The notes and the health text now say what does not exist yet.

## Phase 2, 4 and 5 results (2026-11-21, no car)

| Check | Result |
|---|---|
| a refused job (`FS_LOESCHEN`, `TMODE SENDE_TELEGRAMM`) | 403 `job_not_allowed` with the rule; EDIABAS never started |
| `TMODE INFO`, `TMODE _JOBS`, cable unplugged | 200. First call after boot about 10 s, later calls under 0.5 s |
| `describe TMODE` | 25 jobs with verdicts: `INFO` and four `LESE_*` reads runnable, every set, send and raw-telegram job refused |
| batch of allowed + refused + unknown ECU | each item answered on its own, one init/end cycle |
| with INPA open | every `/ediabas/*` call 409 `cable_in_use` naming `INPALOAD`; EDIABAS not touched |
| `describe NVE_65`, cable not in the VM | 502 `cable_not_found` (EDIABAS error 28, `IFH-0018`): a real SGBD does not load without the interface |
| idle release | agent unloads `api32.dll` 20 s after the last call, `ebas32.exe` is then gone from the process list, and the next call works |
| `describe NVE_65`, cable in the VM but not in the car | 200: 59 jobs, 22 runnable (15 by name, 7 `STATUS_` reads that passed the description screen), 37 refused |
| `--selftest` | PASS, 19 of 19: config, policy, token ACL, bind address, firewall rule, listener, all 20 DLL exports, log-path cases (`..`, junction, symlink, alternate data stream, 8.3 names, device names, hard link), desktop capture |
| screenshot of the desktop and of one window | readable image; bad size 400, unknown window 404 |
| screenshot with INPA open | the INPA loader menu, readable |
| screenshot with the VM locked | 503 `desktop_unavailable` |
| logs | both roots listed newest first; `ABLAUF.TRC` read; `..`, other folders, the agent's own token file, alternate data streams, device names, `\\?\` paths and forward slashes all refused |
| the same through the MCP tools | `vm_health`, `ediabas_list_ecus`, `ediabas_describe`, `ediabas_job`, refusals and `cable_in_use` messages all correct |

Found and fixed during these checks:
- `ebas32.exe` (EDIABAS's host process) stayed running after `apiEnd` for as long as the agent
  had `api32.dll` loaded. The agent now unloads the DLL after `ediabasIdleReleaseSeconds`
  (default 20) without an EDIABAS request.
- EDIABAS error 28 (`IFH-0018`) is now reported as `cable_not_found` with what to do.
- A batch could run longer than the Mac's 75 s timeout; every job is now bounded by the
  request's time budget.
- The Mac server no longer sends the Keychain token to an address set with `EDIABAS_AGENT_URL`.
