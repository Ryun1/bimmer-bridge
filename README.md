# ediabas-bridge

Read-only bridge that lets Claude see into the Windows VM (UTM) running BMW Standard Tools
(INPA, Tool32, NCS Expert, WinKFP): allowlisted read-only EDIABAS jobs, screenshots, and
EDIABAS / NCS Expert trace files.

- [PLAN.md](PLAN.md) — build plan v2
- [docs/api.md](docs/api.md) — the agent's HTTP contract
- [docs/deploy.md](docs/deploy.md) — how files and the token move between the Mac and the VM
- [docs/plan-review.md](docs/plan-review.md) — review of the original plan and why v2 differs
- [docs/plan-v1-original.md](docs/plan-v1-original.md) — the original plan (ISTA-based)

No BMW software, SGBD files or headers are stored in this repo (see `.gitignore`).

## Status

| Phase | State |
|---|---|
| 0 Discovery | done (2026-11-21) |
| 1 Agent skeleton | done (2026-11-21): installed in the VM, self-test passes, survives a reboot |
| 2 EDIABAS at the desk | not started |
| 3 Mac MCP | done (2026-11-21): `vm_health`, `vm_list_windows` and a first cut of the skill, working in Claude Code and the Claude desktop app |
| 4–8 | not started |

### Phase 1 and 3 acceptance results (2026-11-21)

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

## Layout

| Path | What |
|---|---|
| `vm-agent/` | the Windows agent (C#, .NET 10, win-x86). `build.sh` writes `release/EdiabasAgent.zip` |
| `vm-setup/` | `discover.ps1`, `install.ps1`, `update.ps1`, `uninstall.ps1` — run in the VM |
| `mac-mcp/` | the local MCP server Claude starts (TypeScript) |
| `dev/serve.py` | Mac-side server that hands scripts and the agent to the VM and receives uploads |
| `skills/` | Claude skills for using the tools |

## Building and testing (on the Mac)

```sh
# agent: needs the .NET 10 SDK (brew install dotnet@10)
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec PATH=/opt/homebrew/opt/dotnet@10/bin:$PATH
(cd vm-agent && dotnet test EdiabasAgent.Tests)
vm-agent/build.sh                       # prints the SHA-256 of release/EdiabasAgent.zip

# MCP server
(cd mac-mcp && npm install && npm test && npm run build)

# deploy server
python3 -m unittest dev/test_serve.py
```

## Installing or updating the agent in the VM

1. Start the `Windows` VM in UTM and log in.
2. On the Mac: `python3 dev/serve.py --accept-token`. It prints the lines to paste.
3. In the VM, in **Windows PowerShell run as administrator**, paste the Install line (first
   time, or to repair) or the Update line (new build).
4. Stop `serve.py` with Ctrl-C.

The installer sends the agent's token to the Mac's Keychain itself. See `docs/deploy.md`.

## Phase 0 findings (VM discovery, 2026-11-21)

Facts the later phases build on. Source: `discover.ps1` run in the VM.

| Item | Finding |
|---|---|
| Windows | Windows 11 Home 24H2 (build 26100), ARM64, 4 cores, 4 GB |
| User | `ryan`, an administrator. **UAC is switched off** (`EnableLUA = 0`), so every process, including the agent, runs elevated whatever run level the task asks for |
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

### What this changes

- **Elevation:** the plan's "not elevated by default" cannot hold on this VM while UAC is off.
  The agent's folder permissions therefore don't constrain it. This is a property of the VM,
  not something the bridge should change.
- **Conflict list:** `INPALOAD`, `TOOL32`, `NCSEXPER`, `WINKFPT` and `BMW Coding Tool` are all
  confirmed process names, and match the installed config.
- **Logs (Phase 5):** with EDIABAS tracing off there is little to read. Turning it on means
  editing `EDIABAS.INI`, which the safety rules forbid the bridge to do; it would be Ryan's choice.
- **Phase 2 desk test:** `TMODE.PRG` is present, so the no-cable test is available.
