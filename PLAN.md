# ediabas-bridge — build plan v2

**Audience:** Claude Code, running natively on Ryan's Mac.
**Goal:** let Claude (Claude Code, the Claude desktop app, and cloud sessions linked to this
Mac) see into the Windows VM in UTM that runs BMW Standard Tools. Claude should be able to
run **read-only** EDIABAS diagnostic jobs against Ryan's car, take screenshots of the VM, and
read EDIABAS / NCS Expert trace files.

v2 replaces [the original plan](docs/plan-v1-original.md). The reasons for each change are in
[docs/plan-review.md](docs/plan-review.md). The main differences:

- The VM has **INPA and BMW Standard Tools, not ISTA**. The UI Automation reader is gone;
  the EDIABAS runner is now the core and is built second.
- **v1 has no write path.** Fault clearing is deferred (§10).
- Job policy is one shared data file, default-deny, with a second check on job descriptions.
- .NET 10, a testable project split, IP discovery by MAC address, a desk phase before the car.

Facts are marked **[confirmed]** (checked on this Mac or in a cited source) or **(verify)**
(to be settled by Phase 0 or at the desk). Don't hard-code anything marked (verify).

---

## 0. Context

### The machines
- **Mac:** Apple Silicon, macOS. Node v24.16.0 under nvm **[confirmed]**. No .NET SDK yet
  **[confirmed]**. Claude Code runs natively here and can reach the UTM network directly.
- **VM:** UTM VM named `Windows`, QEMU backend, aarch64, network mode **Shared**, MAC
  `F2:E5:81:1E:82:07` **[confirmed from UTM's config]**. Windows on ARM runs the 32-bit
  x86 BMW tools under emulation.
- **Cable:** K+DCAN USB, passed through to the VM. INPA already talks to the car through it,
  so the EDIABAS configuration is known to work.
- `utmctl` is at `/Applications/UTM.app/Contents/MacOS/utmctl` (not on PATH) **[confirmed]**.

### Software in the VM
Seen on the VM desktop on 2026-11-21. Versions are those of BMW Standard Tools 2.12 and
executable names are from memory; Phase 0 confirms both.

| Tool | What it does | Uses the cable | Risk | Role here |
|---|---|---|---|---|
| **INPA** (`INPALOAD.exe`) | Diagnostics: fault memory, live values, activations, via scripts over EDIABAS | Yes | Low to medium (can activate components and clear faults) | Ryan's manual tool. Claude sees it through screenshots only |
| **Tool32** (`TOOL32.EXE`) | Runs any single EDIABAS job from any SGBD | Yes | High (any job, including writes) | Reference: the agent does the same thing, restricted to read jobs |
| **NCS Expert** (`NCSEXPER.EXE`) | Reads and writes ECU coding | Yes | High (wrong coding disables functions) | Never driven. Its trace files are readable as logs |
| **WinKFP** (`WINKFPT.EXE`) | Flashes ECU firmware | Yes | **Very high (an interrupted flash can brick an ECU)** | Never driven. Agent refuses all cable use while it runs |
| **NCS Dummy** | Offline viewer/editor for NCS Expert trace files | No | Low (edits files that NCS Expert later writes to the car) | None |
| **BMW Coding Tool** | Copies SP-Daten files into the NCS Expert, WinKFP and EDIABAS folders | No | Medium (replaces SGBD files) | Agent refuses cable use while it runs |
| **EDIABAS** (`api32.dll`) | The communication layer under all of the above | Yes | — | What the agent calls |
| `VRRTuning…` (zip) | Unknown; nothing found under that name | — | — | Ignored. Phase 0 lists its name only |

ISTA is **not** expected to be installed (verify in Phase 0).

### The car
- 2005 BMW E61 550i M Sport (N62B48TU). Diagnostics run through EDIABAS SGBDs
  (`.prg` / `.grp`) over the K+DCAN cable.
- Known SGBD: `NVE_65.PRG` (Night Vision). Historic fault codes A752/A751 are the read-only
  acceptance test at the car.
- Engine ECU SGBD is probably `N62_TUE` (ME9.2.2 TUE) or `ME9N62_2` (verify with the
  `D_MOTOR` group at the car).
- **Recurring battery drain.** Sessions must be short; the tooling must not hold the
  interface open while idle. Battery-related reads to look for on the engine ECU (verify):
  `STATUS_IBS_MESSWERTE`, `STATUS_BATTERIEINTEGRATOR`, `STATUS_SYSTEMCHECK_PM_INFO_1/2`.

### Why the design looks like this
- A process started over SSH or as a service runs in session 0 and cannot capture the
  desktop. The agent therefore runs **in Ryan's logged-in session**, started by a Task
  Scheduler task at log-on.
- INPA draws its own screens, so its text is not expected to be machine-readable.
  Screenshots are the only view of INPA. That is acceptable because INPA is a front-end over
  EDIABAS jobs, and the agent can run the read jobs directly and get structured results.
- `api32.dll` is a 32-bit DLL, so the agent is an x86 process.

---

## 1. Architecture

```
Claude (Claude Code, desktop app, or linked cloud session)
   │  MCP over stdio
   ▼
mac-mcp/  "ediabas-bridge" — TypeScript MCP server, native macOS process
   │  HTTP, Authorization: Bearer <token>
   │  UTM Shared Network only (Mac = the VM's gateway)
   ▼
vm-agent/  EdiabasAgent.exe — C# .NET 10, win-x86, in Ryan's interactive session
   ├─ EDIABAS runner   → api32.dll via P/Invoke, policy-checked read jobs only
   ├─ Window capture   → JPEG/PNG of a window or the desktop
   └─ Log access       → read-only, allowlisted folders
```

- **.NET 10 (LTS).** .NET 8 left support on 10 Nov 2026.
- **Not elevated by default.** Elevation was only needed to read ISTA's UI. Run elevated only
  if Phase 0 shows the BMW tools are set to run as administrator.
- Built on the Mac (`EnableWindowsTargeting=true`) as one self-contained exe.

---

## 2. Hard safety rules (non-negotiable)

Enforced **in code on both sides**, not only in tool descriptions.

1. **No input into the VM.** No click, keystroke, mouse or clipboard endpoints.
2. **No writes to the car in v1.** No fault clearing, coding, programming, activations or
   adaptations. There is no endpoint that can run them.
3. **EDIABAS is default-deny.** A job runs only if `JobPolicy` allows it (§6). The policy is
   one data file, `policy/policy.json`, loaded by both the agent and the Mac server.
4. **One user of the cable at a time.** The agent refuses every EDIABAS call with
   `cable_in_use` while any of these is running: INPA, Tool32, NCS Expert, WinKFP, BMW Coding
   Tool, or any other process that has `api32.dll` loaded. There is no override flag.
   WinKFP and NCS Expert matter most: a job landing mid-flash can brick an ECU.
5. **Release the interface when idle.** Per request: `apiInit` → job(s) → `apiEnd`. No handle
   is kept between requests.
6. **Network exposure.**
   - Bind only to the VM's address on the UTM shared network, never `0.0.0.0`.
   - Windows Firewall allows inbound only from the adapter's default gateway (the Mac).
   - Every request needs a Bearer token (32 random bytes), compared in constant time.
   - On the Mac the token lives in the Keychain, not in a config file.
7. **Read-only file access.** Log endpoints only read, only under configured roots. Resolve
   real paths; reject `..`, junction/symlink escapes and alternate data streams. The agent
   writes only its own log and audit files under `C:\ProgramData\EdiabasBridge\`.
8. **Never change EDIABAS configuration.** No edits to `EDIABAS.INI` or `obd.ini`, no
   `apiSetConfig`.
9. **Audit trail.** Both sides append one line per request (time, endpoint/tool, ECU, job,
   outcome, duration; rejected job names included).
   - VM: `C:\ProgramData\EdiabasBridge\audit.log`
   - Mac: `~/Library/Logs/ediabas-bridge/audit.log`
10. **Never test against the car unattended.** Claude Code may run *offline* jobs (`TMODE`,
    and the `_JOBS` family, which read the SGBD file) after Ryan confirms the cable is
    unplugged. Everything else is run by Ryan, at the car.

---

## 3. Repository layout

```
ediabas-bridge/
  README.md
  PLAN.md
  docs/
  policy/
    policy.json             the job policy (single source for both sides)
    policy-cases.json       shared test fixture: job name → expected verdict
  vm-agent/
    EdiabasAgent.Core/      net10.0, no Windows APIs — runs and tests on the Mac
      JobPolicy.cs          loads policy.json; pure
      CommentScreen.cs      screens job descriptions (§6)
      PathRules.cs          log path validation logic
      ResultShaping.cs      EDIABAS result sets → JSON
    EdiabasAgent/           net10.0-windows, win-x86, WinExe, self-contained single file
      Program.cs            Kestrel minimal API, auth, routing, bind/rebind
      Config.cs
      Auth/TokenAuth.cs
      Ediabas/Api32.cs      P/Invoke declarations
      Ediabas/EdiabasRunner.cs
      Proc/ConflictCheck.cs
      Capture/WindowCapture.cs
      Logs/LogAccess.cs
      Audit/AuditLog.cs
      SelfTest.cs           `EdiabasAgent.exe --selftest` (Windows-only checks, no cable)
    EdiabasAgent.Tests/     xUnit against Core (dotnet test on the Mac)
  vm-setup/
    discover.ps1            Phase 0, read-only
    install.ps1             idempotent; keeps token and config on upgrade
    update.ps1              stop task → verify hash → swap exe → start task
    uninstall.ps1
  mac-mcp/
    src/index.ts            MCP server (stdio), tool registration
    src/agentClient.ts      HTTP client, timeouts, token from Keychain
    src/vmAddress.ts        find the VM's IP from its MAC address
    src/policy.ts           loads policy/policy.json
    src/format.ts           fault tables, km → miles
    test/                   vitest against a fake agent
  dev/
    serve.py                serves release/ and receives uploads, bound to the bridge IP
```

No BMW binaries, SGBD files, `api.h` or discovery output are committed (`.gitignore`).

---

## 4. Phase 0 — discovery (Ryan runs one script in the VM)

`dev/serve.py` is a small purpose-written server (Python's built-in `http.server` cannot
receive files). It listens on the Mac's bridge address only, serves `release/` and accepts
uploads into `discovery/` (git-ignored). Ryan starts the VM and pastes one line into
PowerShell; the script uploads its results, so nothing is pasted back.

`discover.ps1` is read-only and must report:

- OS version, architecture, and whether the user is an administrator.
- IPv4 addresses, default gateway and network category of each adapter.
- Install locations and file versions of `api32.dll`, `api.h`, `EDIABAS.INI`, `obd.ini`,
  `INPALOAD.exe`, `TOOL32.EXE`, `NCSEXPER.EXE`, `WINKFPT.EXE`, NCS Dummy, BMW Coding Tool,
  searched under `C:\EDIABAS`, `C:\INPA`, `C:\EC-APPS`, `C:\NCSEXPER`, `C:\NFS` and Program Files.
- **Whether ISTA exists** (`ISTAGUI.exe`, `C:\Rheingold`, `C:\ProgramData\BMW`).
- Every desktop shortcut's target and whether it is flagged "run as administrator".
- `EDIABAS.INI` key settings (`Interface`, `EcuPath`, `TracePath`, `ApiTrace`, `IfhTrace`,
  `Simulation`) and the whole of `obd.ini` (port, `UBATT`, hardware).
- Presence of `TMODE.PRG`, `UTILITY.PRG`, `NVE_65.PRG`, `N62_TUE.PRG`, `ME9N62_2.PRG`,
  `D_MOTOR.GRP`, and a count of SGBD files in the Ecu folder.
- COM ports and USB serial devices (the cable).
- Diagnostic processes running now, including `ebas32`-style EDIABAS host processes.
- Recent files under the EDIABAS trace folder, `C:\NCSEXPER\WORK` and the WinKFP work folder.
- Sleep and screen-lock settings.

It uploads the report, plus `api.h` if present. `api.h` is the source of truth for P/Invoke.

**Resolve before writing the runner:** EDIABAS path; exact process names for the conflict
list; whether elevation is needed; log roots; the VM's gateway address; whether `TMODE.PRG`
exists (it is the desk test).

---

## 5. Deployment (Mac → VM)

1. Mac: `dev/serve.py` builds `release/EdiabasAgent.zip`, prints its SHA-256 and serves it on
   the bridge address only.
2. VM, first install (one line, elevated PowerShell):
   `$sha='<hash>'; irm http://<mac>:8000/install.ps1 | iex`
3. VM, every later build: `$sha='<hash>'; irm http://<mac>:8000/update.ps1 | iex`

The expected hash travels in the line Ryan pastes, not from the server, and both scripts
refuse to unpack a zip that doesn't match it. The server is stopped when not deploying.
Claude Code gives Ryan the exact line each time.

---

## 6. VM agent specification

### Config — `C:\ProgramData\EdiabasBridge\config.json`
```json
{
  "bindAddress": "auto",
  "port": 8765,
  "tokenFile": "C:\\ProgramData\\EdiabasBridge\\token.txt",
  "ediabasDll": "C:\\EDIABAS\\BIN\\api32.dll",
  "ecuPath": "C:\\EDIABAS\\ECU",
  "logRoots": ["C:\\EDIABAS\\TRACE", "C:\\NCSEXPER\\WORK"],
  "conflictProcesses": ["INPALOAD", "TOOL32", "NCSEXPER", "WINKFPT", "BMW Coding Tool"],
  "voltageSources": [ { "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" } ],
  "reviewedJobs": [],
  "jobTimeoutSeconds": 30,
  "maxJobTimeoutSeconds": 60
}
```
Values come from Phase 0. `auto` binds to the adapter whose default gateway is the Mac, and
rebinds when that address changes. If no such adapter is up yet, retry every 5 s; never fall
back to `0.0.0.0`. `token.txt` is ACL'd to Ryan's user and Administrators.

### Endpoints
All need `Authorization: Bearer`. JSON responses; errors are `{ "error": { "code", "message" } }`.

| Method & path | Purpose | Notes |
|---|---|---|
| `GET /health` | version, uptime, OS/arch, bind address, elevated?, conflicting processes running, EDIABAS DLL found, `lastCableUseAt` | never touches the cable |
| `GET /windows` | visible top-level windows: handle, title, process, PID, bounds, minimised? | |
| `GET /screen/capture?window=<handle\|title-substring\|desktop>&maxEdge=1600&format=jpeg\|png` | image bytes | see Capture |
| `GET /logs?root=<index>&glob=*.trc&since=<iso>&limit=50` | list files under one root, newest first | |
| `GET /logs/read?path=<abs>&tailBytes=65536` or `&offset=&length=`, optional `&grep=<regex>` | read part of a file | 256 KB cap; UTF-8 then Windows-1252; files opened with `FileShare.ReadWrite\|Delete`; regex uses `NonBacktracking`, pattern ≤ 200 chars |
| `GET /ediabas/ecus?filter=<substr>` | SGBD and group files in the Ecu folder | file listing only |
| `GET /ediabas/describe?ecu=<name>&job=<name?>` | job list of an SGBD, or one job's description, arguments and results (`_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, `_RESULTS`), each with its policy verdict | reads the SGBD file; conflict check still applies |
| `POST /ediabas/job` `{ ecu, job, args?, results?, timeoutSeconds? }` | run one allowed job | |
| `POST /ediabas/jobs` `{ items: [ {ecu, job, args?} ], timeoutSeconds? }` | up to 20 jobs in one init/end cycle | each item policy-checked; one failure doesn't abort the rest |
| `GET /ediabas/vehicle` | ignition and voltage | see below |

There is no clear, write or raw-telegram endpoint.

### Job policy (`policy/policy.json`, class `JobPolicy`)
Default-deny. A job runs only if all of these pass:

1. **Shape.** ECU and job names match `^[A-Za-z0-9_]{1,64}$` and are then upper-cased; every
   later rule works on the upper-cased name. `args` and `results` are each ≤ 200 chars,
   printable ASCII only.
2. **Allowed by name**, one of:
   - global exact names: `IDENT`, `INFO`, `FS_LESEN`, `FS_LESEN_DETAIL`, `IS_LESEN`,
     `IS_LESEN_DETAIL`, `AIF_LESEN`, `SERIENNUMMER_LESEN`, `ZIF_LESEN`, `ZIF_BACKUP_LESEN`,
     `PHYSIKALISCHE_HW_NR_LESEN`, `HARDWARE_REFERENZ_LESEN`, `DATEN_REFERENZ_LESEN`,
     `PRUEFSTEMPEL_LESEN`, `C_FG_LESEN`, `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, `_RESULTS`,
     `_VERSIONINFO`, `_TABLES`, `_TABLE`;
   - the pattern `^STATUS_[A-Z0-9_]+$`;
   - per-ECU exact names, for ECUs where the pattern is switched off:
     - `TMODE`: `INFO`, `LESE_INTERFACE_TYP`, `LESE_INTERFACE_VERSION`, `LESE_SPANNUNG_KL30`,
       `LESE_SPANNUNG_KL15`, plus the `_JOBS` family. `TMODE` also contains raw-telegram and
       programming-voltage jobs, so nothing else on it is ever allowed.
     - `UTILITY`: `INFO`, `STATUS_UBATT`, `STATUS_ZUENDUNG`, plus the `_JOBS` family.
       `UTILITY` also contains `ABS_RELAIS` and `IF_RESET`.
3. **Not denied by name.** The denylist overrides the allowlist:
   - as a substring anywhere: `SCHREIB`, `LOESCH`, `STEUER`, `CODIER`, `FLASH`, `VORGEB`,
     `SETZE`, `ABGLEICH`, `KALIB`, `TELEGR`, `AUFTRAG`, `AUTHENT`, `RESET`, `SLEEP`,
     `POWER_DOWN`;
   - as a whole `_`-separated token: `START`, `STOP`, `ENDE`, `INIT`, `LERN`, `LERNEN`,
     `ADAPT`, `PROG`, `SPEICHER`, `SENDE`.

   Token matching is what lets reads such as `STATUS_NWGADAPTION` and `STATUS_DKLERN_KT`
   through. This rule set was run against the published job lists of `NVE_65`, `N62_TUE`,
   `TMode` and `UTILITY`: every write, clear, flash, reset and activation job is denied, and
   the only doubtful job it lets through by name is `STATUS_SYSTEMCHECK_GRUNDADAPT`, which
   step 4 exists to catch.
4. **Description screen (VM side only, for jobs allowed by the `STATUS_` pattern).** A
   `STATUS_` name does not guarantee a pure read. Before running such a job, the agent reads
   its `_JOBCOMMENTS` from the SGBD and refuses with `job_needs_review` (returning the
   description) if it mentions starting, activating, writing, clearing, setting,
   programming, resetting or learning. Descriptions are German or English, so screen for
   both: `start`, `ansteuer`, `aktivier`, `schreib`, `loesch`, `lösch`, `setz`, `programm`,
   `reset`, `lernen`, `abgleich`, `write`, `clear`, `erase`, `delete`, `activate`, `learn`.
   A job with no description is also refused. This will flag some harmless reads (for
   example a description that says "Status Lernen"); that is the intended direction. Ryan
   approves a specific job by adding `ECU:JOB` to `reviewedJobs` in the VM's config.

Rejections return 403 `job_not_allowed` with the rule that matched, and are audited.
`policy-cases.json` holds every case both test suites run, including tricks such as
`status_steuern_x`, `FS_LESEN;FS_LOESCHEN`, whitespace and mixed case. A policy change
without a fixture change is a bug.

### EDIABAS runner
- **P/Invoke.** Use `api.h` if Phase 0 finds it. The expected shape, taken from the MIT
  `pydiabas` wrapper **[confirmed in its source]**, to be checked against the real DLL's
  exports before use:
  - stdcall; exports prefixed `__api`; handle is `unsigned int`, obtained from
    `__apiInit(&handle)` and passed first to every other call;
  - `__apiJob(handle, ecu, job, params, results)`, `__apiState(handle)` → 0 busy, 1 ready,
    2 break, 3 error; `__apiBreak`, `__apiEnd`;
  - `__apiResultSets(handle, ushort*)`, `__apiResultNumber(handle, ushort*, set)`,
    `__apiResultName(handle, char*, index, set)`, `__apiResultFormat(handle, int*, name, set)`,
    `__apiResultText(handle, char*, name, set, format)`, `__apiResultBinary…`;
  - `__apiErrorCode(handle)`, `__apiErrorText(handle, char*, length)`;
  - result formats 0–8: char, byte, integer, word, long, dword, text, binary, real;
  - buffers: 1024 for text and binary, 256 for result names.
- **Threading.** One dedicated worker thread owns every EDIABAS call; a queue feeds it one
  request at a time.
- **Per request:** conflict check → `apiInit` → for each job: policy → `apiJob` → poll
  `apiState` every 50 ms until not busy or timeout (`apiBreak` on timeout) → read every
  result set → `apiEnd` in `finally`.
- **Results.** Set 0 holds system results (`VARIANTE`, `JOB_STATUS`, …); sets 1..n are job
  results. Text as string, integers and reals as numbers, binary as hex.
  ```json
  { "ecu": "NVE_65", "job": "FS_LESEN", "variant": "NVE_65", "jobStatus": "OKAY",
    "systemResults": { }, "sets": [ { "F_ORT_NR": 42834, "F_ORT_TEXT": "…" } ], "durationMs": 812 }
  ```
- **Errors.** Return EDIABAS's `{ errorCode, errorText }`. Map interface errors that mean the
  port is busy or missing to `cable_in_use` / `cable_not_found` (codes to confirm at the desk).
- Group names (e.g. `D_MOTOR`) are accepted; the resolved variant is reported.

### Conflict check
- Named processes from `conflictProcesses`.
- Plus any other process with `api32.dll` loaded (best effort; processes that can't be
  inspected are judged by name only).

### `/ediabas/vehicle`
- Ignition: `UTILITY` `STATUS_ZUENDUNG`.
- Voltage in volts: each entry in `voltageSources`. `NVE_65 STATUS_BORDNETZSPANNUNG` returns
  volts **[confirmed in the SGBD documentation]**. Add the engine ECU's job once verified.
- `UTILITY STATUS_UBATT` is reported as a presence flag only: its result has no unit, and
  `obd.ini` can force it on (`UBATT=OFF`).
- Cable-side readings from `TMODE` `LESE_SPANNUNG_KL30` / `KL15` (mV) are included if the
  cable supports them (verify).

### Capture
- Window: `PrintWindow` with `PW_RENDERFULLCONTENT`, falling back to a `BitBlt` of the window
  rectangle from the screen. `desktop` captures the primary screen.
- A minimised window is reported as minimised, never restored.
- Process is per-monitor DPI-aware. Downscale to `maxEdge` (default 1600). JPEG q85 by default.
- Headers `X-Window-Title` (percent-encoded) and `X-Capture-Method`.
- Locked or secure desktop returns `desktop_unavailable`.

### install.ps1
1. Create `C:\ProgramData\EdiabasBridge\`, copy the exe, write `config.json` from detected
   values. Generate the token only if none exists; ACL it.
2. Firewall: allow inbound TCP 8765 from the adapter's default gateway only.
3. Scheduled task: at log-on of the current user, interactive, no time limit, restart on
   failure. `-Elevated` switch sets highest run level; default is off.
4. Disable sleep on AC. Ask Ryan before changing the idle screen lock.
5. Print the port and, on first install only, the token with the Keychain command (§7).

Re-running it upgrades in place. `uninstall.ps1` reverses everything.

---

## 7. Mac MCP server specification

- TypeScript, `@modelcontextprotocol/sdk`, stdio, `zod`. Output `mac-mcp/dist/index.js`.
- **Finding the VM.** `vmAddress.ts` looks up MAC `F2:E5:81:1E:82:07` in
  `/var/db/dhcpd_leases`, then `arp -an`, then `utmctl ip-address Windows`. Env
  `EDIABAS_AGENT_URL` overrides. Details **[confirmed on this Mac]**:
  - macOS prints MAC bytes without leading zeros (`f2:e5:81:1e:82:7`), so compare numerically.
  - The lease file holds two entries for this MAC, `192.168.65.3` (2025) and `192.168.64.3`
    (today), so the subnet has changed before. Take the entry with the latest expiry, and
    accept an address only after `/health` answers there with the right token.
- **Token.** `security find-generic-password -s ediabas-bridge -a agent -w`; env
  `EDIABAS_AGENT_TOKEN` as a fallback with a warning on stderr.
- Timeouts: 15 s, and 75 s for EDIABAS calls. An unreachable agent gets a specific message:
  VM not running, agent not started (nobody logged in), or firewall.
- The Mac server runs `JobPolicy` from the same `policy.json` before any call leaves the Mac.
- Logs to stderr only. Audit log at `~/Library/Logs/ediabas-bridge/audit.log`.

### Tools
All tools carry `readOnlyHint: true`. There is no destructive tool.

| Tool | Args | Returns |
|---|---|---|
| `vm_health` | — | agent status, conflicting tools running, last cable use |
| `vm_list_windows` | — | compact table |
| `vm_screenshot` | `window?` (default `desktop`), `maxEdge?` | image plus a one-line caption |
| `vm_logs_list` | `root?`, `glob?`, `since?`, `limit?` | path / size / modified |
| `vm_logs_read` | `path`, `tailBytes?` or `offset?`/`length?`, `grep?` | text |
| `ediabas_list_ecus` | `filter?` | SGBD and group names |
| `ediabas_describe` | `ecu`, `job?` | jobs with descriptions and policy verdicts; or one job's arguments and results |
| `ediabas_vehicle_status` | — | ignition, voltage |
| `ediabas_job` | `ecu`, `job`, `args?`, `results?`, `raw?` | formatted text; raw JSON only when `raw: true` |
| `ediabas_read_faults` | `ecus[]` (≤ 20), `includeInfoMemory?` | `FS_LESEN` (+ `IS_LESEN`) per ECU in one init/end cycle, as fault tables |

### Formatting
- Fault tables: one row per set — code in hex (`F_ORT_NR`), text (`F_ORT_TEXT`), present or
  stored, frequency, logged conditions. Field names vary by SGBD: map the known ones and pass
  the rest through as `key=value`.
- **Distances in miles first**, km in brackets: `121,609 mi (195,712 km)`.

### Registration
- **Claude Code:** `claude mcp add ediabas-bridge -- <node> <repo>/mac-mcp/dist/index.js`.
- **Claude desktop app:** add an `mcpServers` entry to
  `~/Library/Application Support/Claude/claude_desktop_config.json`. The file has no
  `mcpServers` key today **[confirmed]**; create it without touching the other keys. Use
  absolute paths, with `command` pointing at a stable Node symlink so an nvm upgrade doesn't
  break it.
- Token: `security add-generic-password -s ediabas-bridge -a agent -w '<token>' -U`.

---

## 8. Build phases and acceptance

Stop at each gate. "Claude" checks are run by Claude Code with `curl` from the Mac.

| Phase | Build | Acceptance |
|---|---|---|
| **0 Discovery** | .NET 10 SDK (`brew install dotnet@10`), `dev/serve.py`, `discover.ps1` | Report uploaded; every (verify) item needed for Phase 2 resolved and recorded in the README |
| **1 Agent skeleton** | Core + host projects, config, auth, `/health`, `/windows`, bind/rebind, install/update/uninstall, audit | Claude: `/health` → 200 with the token, 401 without. `netstat` in the VM shows one listener on the shared-network address. Agent returns after a VM reboot. `--selftest` passes |
| **2 EDIABAS at the desk** | P/Invoke, runner, policy, conflict check, `/ediabas/ecus`, `/ediabas/describe`, `/ediabas/job(s)` | **Cable unplugged, confirmed by Ryan.** Claude: `TMODE INFO` and `TMODE _JOBS` return results; `describe NVE_65` lists its jobs with verdicts (verify that a non-`TMODE` SGBD loads with no cable; if not, repeat with the cable in the laptop but not in the car); a denied job is refused with the matching rule; with INPA open every EDIABAS call returns `cable_in_use`; no EDIABAS process is left running after a call |
| **3 Mac MCP** | MCP server, VM lookup, Keychain token, tools for everything built so far, registration | In Claude Code and in the desktop app: `vm_health` and `ediabas_describe` work. If cloud sessions matter, check one there too |
| **4 Screenshots** | `/screen/capture`, `vm_screenshot` | With INPA open on any screen, `vm_screenshot` returns a readable image of it; a locked VM returns `desktop_unavailable` |
| **5 Logs** | `/logs`, `/logs/read`, tools | Newest trace file listed and its tail readable; `..` and junction escapes rejected (`--selftest` plus one manual try) |
| **6 At the car** | `/ediabas/vehicle`, `ediabas_read_faults`, formatter | **Ryan, ignition on, all BMW tools closed.** Ignition and a voltage in volts are shown; `IDENT` through `D_MOTOR` names the engine SGBD; `ediabas_read_faults` on `NVE_65` shows the A752/A751 pair from the INPA session of 11/09/2026; total ignition-on time under 10 minutes |
| **7 Hardening** | README (setup, troubleshooting, safety rules), version stamp, release script | Ryan can reinstall from the README alone |

### Automated tests (no VM, no car)
- `EdiabasAgent.Tests` (xUnit, on the Mac): every case in `policy-cases.json`; the
  description screen; path rules; result shaping from a fake API.
- `mac-mcp/test` (vitest): every tool against a stub agent; the same `policy-cases.json`;
  MAC-address matching; formatter output including km → miles.
- `EdiabasAgent.exe --selftest` (in the VM, no cable): Windows path cases (junctions,
  alternate data streams, 8.3 names, device names), token ACL, bind address, firewall rule.

---

## 9. Out of scope for v1
- Any input into the VM.
- Any write to the car: fault clearing, coding, programming, activations, adaptations.
- Driving INPA, Tool32, NCS Expert, WinKFP, NCS Dummy or BMW Coding Tool.
- Raw `api*` access, raw telegrams, changing EDIABAS configuration.
- Reading window text through UI Automation.
- Exposing the agent beyond UTM's shared network.

## 10. Possible later
- **Fault clearing**, gated by Touch ID on the Mac (a button dialog can be clicked by a
  Claude session with computer use) and by a flag in the VM's config.
- UI Automation text for Tool32's result pane, if screenshots prove too coarse.
- A saved sweep: the list of ECUs read in the INPA session of 11/09/2026.
- GitHub Actions on `windows-latest` to run the Windows-only tests.
- Packaging the Mac server as a desktop extension.

## 11. Working agreements for Claude Code
- Ryan runs everything inside the VM. Give him one line at a time and say what to expect.
- Claude runs the `curl` checks for Phases 1–5 itself, and offline EDIABAS jobs only after
  Ryan confirms the cable is unplugged.
- Don't run anything that talks to the car. Phase 6 is Ryan's.
- A fact marked (verify) stays out of the code until Phase 0 or a desk check settles it.
- Policy changes go through `policy.json` and `policy-cases.json` together.
- Commit after each phase with a short message naming the phase.

## Sources
- [pydiabas](https://github.com/BembelBytes/pydiabas) — api32.dll signatures, constants, `TMODE` offline jobs.
- [EDIABAS SGBD documentation index](https://emdzej.github.io/ediabasx-docs-sgbd/) — `UTILITY`, `TMode`, `NVE_65`, `N62_TUE`, `ME9N62_2`, `d_motor` job lists.
- [NCS Dummy thread, bimmerforums](https://www.bimmerforums.com/forum/showthread.php?p=19766272) — what NCS Dummy does.
- [Updating SP-Daten with BMW Coding Tool](https://obdinnovations.freshdesk.com/support/solutions/articles/36000079519-how-to-update-sp-daten-files-with-bmw-coding-tool-2-50) — what BMW Coding Tool does.
- [BMW Standard Tools 2.12 contents](https://blog.udn.com/obdtool/31604013) — tool versions.
