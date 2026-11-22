# ista-bridge — build plan

> Historical record: the original, ISTA-based plan, replaced by [PLAN.md](../PLAN.md). For the current state, see the [README](../README.md).

**Audience:** Claude Code, running on Ryan's Mac.
**Goal:** let Claude (in the Claude desktop app and in cloud sessions linked to this Mac) see into the Windows VM running in UTM. Claude should be able to read ISTA's screen as text, take screenshots of ISTA, read ISTA/EDIABAS logs and run **read-only** EDIABAS diagnostic jobs against Ryan's car.

---

## 0. Context

### The machines
- **Mac:** Apple Silicon (arm64), macOS. Node is installed via nvm. The Claude desktop app is installed and already runs one local stdio MCP server ("Control Chrome"). Local MCP servers configured in the desktop app are proxied into Claude's cloud sessions, so a new local server shows up there automatically.
- **VM:** Windows in UTM, presumed Windows 11 on ARM, which runs x86/x64 programs under emulation. It has **ISTA** (BMW dealer diagnostics) and **INPA/EDIABAS** installed. A **K+DCAN USB cable** is passed through to the VM, and INPA already talks to the car through it, so the EDIABAS configuration is known to work.
- Claude's own shell on the Mac is a sandboxed Linux VM that **cannot reach the UTM VM**. That is why the Mac-side part of this project must be a native macOS process started by the Claude desktop app, not something Claude runs in its sandbox.

### The car
- 2005 BMW E61 550i M Sport (N62B48, pre-LCI E-series). The OBD diagnostics on this car run through EDIABAS SGBDs (`.prg`/`.grp`) via the K+DCAN cable.
- Known SGBD from earlier INPA sessions: `NVE_65.PRG` (Night Vision). It has historic fault codes A752/A751, which make a good read-only acceptance test (Phase 5).
- The car has a recurring battery-drain issue, so diagnostic sessions should be short. The tooling must not keep the bus busy or hold the interface open while idle.

### Why the design looks like this
- Anything started over SSH or as a Windows service runs in **session 0**. Session 0 cannot see the interactive desktop, so it can't take screenshots or read UI Automation of ISTA. The VM-side component must therefore run **inside Ryan's logged-in desktop session**, started by a Task Scheduler task at log-on.
- ISTA is a .NET/WPF desktop app, so most of its UI is exposed through **Windows UI Automation (UIA)**. That gives fault lists, test-plan steps and measured values as text, without reading them from pixels. Screenshots are the fallback for anything UIA doesn't expose, such as graphs, wiring diagrams and embedded document viewers.
- ISTA probably runs elevated ("Run as administrator"). Windows UIPI blocks a non-elevated process from reading an elevated window's UI, so the agent must run with highest privileges.

---

## 1. Architecture

```
Claude (desktop app or linked cloud session)
   │  MCP over stdio (proxied by the Claude desktop app)
   ▼
mac-mcp/  "ista-bridge" — TypeScript MCP server, native macOS process
   │  HTTP, Authorization: Bearer <token>
   │  UTM Shared Network only (host 192.168.64.1 → VM 192.168.64.x)
   ▼
vm-agent/  IstaAgent.exe — C# .NET 8, win-x86, runs in Ryan's interactive session (elevated)
   ├─ UIA reader        → ISTA window text / structure
   ├─ Window capture    → PNG/JPEG of ISTA window or whole desktop
   ├─ Log access        → read-only, allowlisted folders
   └─ EDIABAS runner    → api32.dll via P/Invoke, allowlisted read jobs only
```

### Why C# x86 for the agent
- `api32.dll` (EDIABAS API) is a 32-bit x86 DLL. An x86 process can load it directly, and Windows 11 on ARM runs x86 under emulation.
- UIA works across process bitness, so an x86 agent can read an x64/ARM64 ISTA.
- Kestrel (ASP.NET Core) avoids the `netsh http urlacl` setup that `HttpListener` needs.
- It builds on the Mac with the .NET 8 SDK (`EnableWindowsTargeting=true`) as a single self-contained exe, so nothing needs installing in the VM.
- If Phase 0 shows a 64-bit `api64.dll` is present and preferable, x64 is acceptable. Record that decision in the README.

---

## 2. Hard safety rules (non-negotiable)

These are enforced **in code on both sides**, not just in tool descriptions.

1. **No input into the VM.** v1 has no click, keystroke, mouse or clipboard-write endpoints. Ryan drives ISTA himself; Claude only reads.
2. **No ISTA service functions.** The agent never starts ISTA test plans, programming, coding, calibration (e.g. ACC alignment) or anything else in ISTA.
3. **EDIABAS read-only by allowlist.** `ediabas_job` accepts only:
   - exact names: `IDENT`, `INFO`, `FS_LESEN`, `FS_LESEN_DETAIL`, `IS_LESEN`, `IS_LESEN_DETAIL`, `AIF_LESEN`, `SERIENNUMMER_LESEN`, `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, `_RESULTS`
   - and `^STATUS_[A-Z0-9_]+$`

   Then a **denylist that overrides the allowlist**. Reject any job name containing (case-insensitive) `SCHREIBEN`, `LOESCHEN`, `STEUERN`, `CODIER`, `FLASH`, `PROG`, `RESET`, `ADAPT`, `VORGEBEN`, `SPEICHER`, `ABGLEICH`, `KALIB`, `LERN`, `INIT`, `SETZEN`, `START`, `STOP`, `ENDE`, `DIAGNOSE_MODE`, `SLEEP`, `POWER_DOWN`, `ENERGIESPAR`.
   Put the policy in one pure, unit-tested class (`JobPolicy`) and run it **in both the VM agent and the Mac server**. Arguments: max 200 chars; no control characters.
4. **Fault clearing is the only write, and it needs a human.** `ediabas_clear_faults` (jobs `FS_LOESCHEN` / `IS_LOESCHEN` only) works like this:
   - The Mac server first shows a **native macOS dialog** via `osascript`, naming the ECU and what will be cleared, with *Cancel* as the default button. It proceeds only if Ryan clicks *Clear*. Nothing in a tool argument can skip the dialog.
   - The VM agent additionally refuses the clear endpoint unless `allowFaultClear: true` is set in its `config.json`. The default is `false`.
5. **One user of the cable at a time.** The agent refuses all EDIABAS calls while any of these processes is running: ISTA's GUI process, `INPALOAD.exe`, `Tool32.exe`, or other EDIABAS clients (confirm exact process names in Phase 0). It returns a clear error ("close ISTA/INPA first"). There is no override flag.
6. **Release the interface when idle.** For each request: `apiInit` → job(s) → `apiEnd`. Never keep an EDIABAS handle open between requests.
7. **Network exposure:**
   - The agent binds **only** to the VM's UTM Shared Network IPv4 address, never `0.0.0.0`.
   - A Windows Firewall rule allows inbound only from `192.168.64.1`.
   - Every request needs a Bearer token (≥ 32 random bytes) with constant-time comparison.
   - On the Mac, the token is stored in the **macOS Keychain**, not in `claude_desktop_config.json`.
8. **Read-only file access.** Log endpoints only read, only under configured root folders. Resolve real paths and reject `..`, symlink/junction escapes and alternate data streams. The agent writes nothing except its own log and audit files under `C:\ProgramData\IstaAgent\`.
9. **Audit trail.** Both sides append one line per request (timestamp, endpoint/tool, ECU, job, outcome, duration) to an audit log:
   - VM: `C:\ProgramData\IstaAgent\audit.log`
   - Mac: `~/Library/Logs/ista-bridge/audit.log`
10. **Never test against the car unattended.** Claude Code must not run EDIABAS jobs as part of automated tests. Car-facing tests are run by Ryan, at the car, with the ignition on, following the acceptance steps below.

---

## 3. Repository layout

Create at `~/projects/ista-bridge/`:

```
ista-bridge/
  README.md                 setup, usage, troubleshooting, safety rules (copy §2)
  PLAN.md                   this file
  vm-agent/
    IstaAgent.csproj        net8.0-windows, RuntimeIdentifier win-x86, OutputType WinExe,
                            SelfContained, PublishSingleFile, EnableWindowsTargeting
    Program.cs              Kestrel minimal API, auth middleware, routing, startup retry
    Config.cs               loads C:\ProgramData\IstaAgent\config.json
    Auth/TokenAuth.cs
    Uia/ScreenReader.cs     FlaUI.UIA3 (fallback: System.Windows.Automation)
    Capture/WindowCapture.cs
    Logs/LogAccess.cs
    Ediabas/Api32.cs        P/Invoke declarations
    Ediabas/EdiabasRunner.cs  serialized, timeouts, init/end per request
    Ediabas/JobPolicy.cs    allow/deny logic (shared semantics with mac side)
    Proc/ConflictCheck.cs   detects ISTA/INPA/Tool32
    Audit/AuditLog.cs
  vm-agent.Tests/           xUnit — JobPolicy, path safety, result shaping (runs on the Mac)
  vm-setup/
    discover.ps1            Phase 0 read-only discovery (below)
    install.ps1             deploy exe, config, token, firewall rule, scheduled task
    uninstall.ps1
  mac-mcp/
    package.json            @modelcontextprotocol/sdk, zod
    tsconfig.json
    src/index.ts            MCP server (stdio), tool registration
    src/agentClient.ts      HTTP client, timeouts, token from Keychain
    src/policy.ts           same allow/deny rules as JobPolicy
    src/confirm.ts          osascript confirmation dialog
    src/format.ts           compact text renderings (fault tables, UIA text)
    test/                   vitest — against a fake agent (no VM needed)
```

---

## 4. Phase 0 — discovery (Ryan runs this in the VM)

Write `vm-setup/discover.ps1` (read-only) and ask Ryan to run it in an **elevated** PowerShell in the VM. The first deploy method is in §5; for Phase 0, Ryan can paste the script into PowerShell. It writes `ista-bridge-discovery.txt` to the Desktop for Ryan to paste back. Starting point:

```powershell
# ista-bridge discovery — read-only. Run elevated. Output: Desktop\ista-bridge-discovery.txt
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ista-bridge-discovery.txt'
& {
  '== OS =='
  Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture | Format-List
  "PROCESSOR_ARCHITECTURE=$env:PROCESSOR_ARCHITECTURE"

  '== Network (IPv4) =='
  Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -notlike '127.*' |
    Select-Object InterfaceAlias, IPAddress, PrefixLength, PrefixOrigin | Format-Table -AutoSize
  Get-NetConnectionProfile | Select-Object InterfaceAlias, NetworkCategory | Format-Table -AutoSize

  '== Runtimes =='
  foreach ($c in 'python','py','dotnet','pwsh') {
    $p = Get-Command $c -ErrorAction SilentlyContinue
    '{0}: {1}' -f $c, $(if ($p) { $p.Source } else { 'not found' })
  }
  Test-Path "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" | ForEach-Object { "x86 PowerShell present: $_" }

  '== Key files (ISTA / EDIABAS / INPA) =='
  $roots = @('C:\EDIABAS','C:\INPA','C:\ISTA','C:\Rheingold','C:\Program Files','C:\Program Files (x86)','C:\ProgramData\BMW') |
    Where-Object { Test-Path $_ }
  $names = 'api32.dll','api64.dll','api.h','EDIABAS.INI','obd.ini','ISTAGUI.exe','INPALOAD.exe','Tool32.exe','UTILITY.PRG'
  foreach ($r in $roots) {
    Get-ChildItem $r -Recurse -Depth 6 -File -ErrorAction SilentlyContinue |
      Where-Object { $names -contains $_.Name } |
      Select-Object FullName, Length, LastWriteTime, @{n='Version';e={$_.VersionInfo.FileVersion}}
  } | Format-Table -AutoSize -Wrap
  Get-ChildItem "$env:WINDIR\obd.ini" -ErrorAction SilentlyContinue | ForEach-Object { "== $($_.FullName) =="; Get-Content $_ }

  '== EDIABAS.INI key settings =='
  Get-ChildItem C:\EDIABAS -Recurse -Filter EDIABAS.INI -ErrorAction SilentlyContinue | ForEach-Object {
    "-- $($_.FullName)"
    Select-String -Path $_.FullName -Pattern '^\s*(Interface|EcuPath|TracePath|ApiTrace|IfhTrace|SimulationPath|Simulation)\s*=' |
      ForEach-Object { $_.Line }
  }

  '== Serial / USB (cable) =='
  Get-CimInstance Win32_PnPEntity | Where-Object Name -match 'COM\d+|FTDI|USB Serial' |
    Select-Object Name, Manufacturer, Status | Format-Table -AutoSize

  '== Diagnostic processes running now =='
  Get-Process | Where-Object { $_.Name -match 'ISTA|INPA|Ediabas|Tool32|Rheingold|ICOM' } |
    Select-Object Name, Id, Path | Format-Table -AutoSize -Wrap

  '== Recent ISTA / EDIABAS logs =='
  foreach ($r in @('C:\ProgramData\BMW','C:\EDIABAS\TRACE')) {
    if (Test-Path $r) {
      Get-ChildItem $r -Recurse -File -Include *.log,*.trc,*.txt -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 25 FullName, Length, LastWriteTime
    }
  } | Format-Table -AutoSize -Wrap

  '== Power / lock =='
  powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | Select-String 'Current AC'
} *>&1 | Tee-Object -FilePath $out
"Saved to $out"
```

Also ask Ryan directly:
- the UTM network mode for this VM (Settings → Network; want **Shared Network**);
- whether ISTA is started "as administrator";
- the ISTA version shown in ISTA's About/Info screen;
- whether the VM screen locks on idle.

**Resolve from Phase 0 before writing code:** the EDIABAS path; `api32.dll` vs `api64.dll`; whether `api.h` exists (use it as the source of truth for P/Invoke signatures, calling convention and constants); ISTA's process name and log folders; the VM's IP; and whether `UTILITY.PRG` exists (for the battery/ignition check).

---

## 5. Deployment mechanics (Mac → VM)

Claude Code builds on the Mac. To get files into the VM without extra software:

1. On the Mac, serve the release folder on UTM's host address only:
   `python3 -m http.server 8000 --bind 192.168.64.1 --directory ~/projects/ista-bridge/release`
2. In the VM, download it (elevated PowerShell):
   `Invoke-WebRequest http://192.168.64.1:8000/IstaAgent.zip -OutFile $env:TEMP\IstaAgent.zip`
3. Unzip and run `install.ps1`.

Stop the HTTP server afterwards. Put these steps in the README. Claude Code gives Ryan the exact commands to run; it cannot run anything inside the VM itself.

---

## 6. VM agent specification

### Config — `C:\ProgramData\IstaAgent\config.json`
```json
{
  "bindAddress": "192.168.64.X",
  "port": 8765,
  "tokenFile": "C:\\ProgramData\\IstaAgent\\token.txt",
  "logRoots": ["C:\\ProgramData\\BMW\\ISPI\\logs", "C:\\EDIABAS\\TRACE"],
  "ediabasDll": "C:\\EDIABAS\\BIN\\api32.dll",
  "ediabasConfigOverride": "",
  "conflictProcesses": ["ISTAGUI", "INPALOAD", "Tool32"],
  "jobTimeoutSeconds": 30,
  "maxJobTimeoutSeconds": 60,
  "allowFaultClear": false
}
```
Fill the values from Phase 0. If `bindAddress` is `"auto"`, pick the IPv4 address in `192.168.64.0/24`. Lock down `token.txt` with ACLs (Administrators + Ryan's user only).

### Startup
- If the bind address isn't up yet (VM networking still starting), retry every 5 s; never fall back to `0.0.0.0`.
- Use the WinExe output type, so no console window appears.

### Endpoints
All require `Authorization: Bearer`. All responses are JSON unless noted, and errors use the shape `{ "error": { "code", "message" } }`.

| Method & path | Purpose | Notes |
|---|---|---|
| `GET /health` | agent version, uptime, OS/arch, bind address, whether ISTA/INPA are running, whether the EDIABAS DLL loads, `allowFaultClear` | must never touch the cable |
| `GET /windows` | top-level visible windows: handle, title, process name, PID, bounds, elevated? | |
| `GET /screen/text?window=<handle\|title-substring\|ista>&format=text\|json&maxNodes=4000&includeOffscreen=false` | UIA dump | see "UIA reader" |
| `GET /screen/capture?window=<…\|desktop>&maxEdge=1600&format=png\|jpeg&quality=85` | image bytes (`image/png` or `image/jpeg`) | see "Capture" |
| `GET /logs?root=<index>&glob=*.log&since=<iso>&limit=50` | list files under one configured root, newest first | |
| `GET /logs/read?path=<abs>&tailBytes=65536` or `&offset=&length=` and optional `&grep=<regex>` | read part of a file | cap 256 KB per response; text decoded as UTF-8, falling back to Windows-1252 |
| `GET /ediabas/ecus?filter=<substr>` | list SGBD/group files (`*.prg`, `*.grp`) in the EDIABAS Ecu folder | no cable access |
| `POST /ediabas/job` `{ ecu, job, args?, results?, timeoutSeconds? }` | run one allowlisted job | see "EDIABAS runner" |
| `POST /ediabas/jobs` `{ items: [ {ecu, job, args?} ], timeoutSeconds? }` | up to 20 jobs in one init/end cycle (e.g. a sweep of `FS_LESEN` over many ECUs) | each item policy-checked; one failure doesn't abort the rest |
| `GET /ediabas/vehicle` | battery voltage + ignition via `UTILITY` `STATUS_UBATT` / `STATUS_ZUENDUNG` (verify these job names with `_JOBS` first) | lets Claude check ignition on/battery OK before a sweep |
| `POST /ediabas/clear` `{ ecu, scope: "FS"\|"IS"\|"both" }` | clear fault memory | 403 unless `allowFaultClear`; only `FS_LOESCHEN`/`IS_LOESCHEN` |

### UIA reader
- Use **FlaUI.UIA3**. Run UIA on a dedicated worker thread with a wall-clock timeout (10 s default); if it times out, return what has been collected, with `truncated: true`.
- `window=ista` resolves to the largest visible top-level window of ISTA's process.
- Walk the Control View. For each element emit: `controlType`, `name`, `automationId`, `className`, `value` (ValuePattern), `toggleState`, `isSelected`, `isEnabled`, `isOffscreen`, `bounds`, children.
- **Tables:** for elements supporting GridPattern/TablePattern (DataGrid, ListView), emit `columns` (header names) and `rows` (cell text arrays) instead of walking every cell as a node. ISTA's fault memory and test-plan lists are tables, so this matters most.
- **`format=text` rendering:** indented lines like `[Button] Read fault memory` and `[Edit] Mileage = 195,712`. Render tables as pipe-separated rows under their header. Skip empty, offscreen and pure layout containers (Pane/Group/Custom with no name and a single child) to keep output compact.
- Return `{ window: {title, pid, elevated}, nodeCount, truncated, text | tree }`.
- If ISTA is elevated and the agent isn't, return a specific error that names UIPI and tells Ryan to fix the scheduled task.
- If the desktop is locked or on the secure desktop, return a specific `desktop_unavailable` error.

### Capture
- Window capture: use `PrintWindow` with `PW_RENDERFULLCONTENT` (2), falling back to `BitBlt` of the window rectangle from the screen DC if the result is blank. Handle per-monitor DPI by declaring the process DPI-aware in the manifest.
- `desktop`: capture the primary screen.
- Downscale so the longest edge ≤ `maxEdge` (default 1600). Default to JPEG q85 for screenshots; use PNG only when asked.
- Include headers `X-Window-Title` and `X-Capture-Method`.

### EDIABAS runner
- **P/Invoke:** derive signatures, calling convention and constants **from `api.h` if Phase 0 finds it**. Otherwise:
  - find the exported names with a PE export reader (community bindings use names like `__apiInit`/`__apiInitExt`, `__apiJob`, `__apiState`, `__apiResultSets`, `__apiResultText`, `__apiResultFormat`, `__apiResultName`, `__apiResultNumber`, `__apiErrorCode`, `__apiErrorText`, `__apiBreak`, `__apiEnd`);
  - verify every one against the actual DLL before relying on it.

  Do not guess silently; document what was verified.
- **Serialization:** one dedicated worker thread, a queue and one job at a time. Before each request, check for conflicting processes and return 409 `cable_in_use` if any are running.
- **Per request:**
  1. `apiInit`
  2. For each job: `apiJob(ecu, job, args, results)`. Poll `apiState` every 50 ms until it is no longer busy or the timeout passes; on timeout call `apiBreak` and return `timeout`.
  3. On error, return `{ errorCode, errorText }` from the API.
  4. Read every result set: set 0 holds system results (e.g. `VARIANTE`, `JOB_STATUS`); sets 1..n are job results. For each result, read the name, format and value. Text formats are returned as strings, integers as numbers, reals as numbers and binary as hex.
  5. Always call `apiEnd` in `finally`.
- **Response shape:**
  ```json
  { "ecu": "NVE_65", "job": "FS_LESEN", "variant": "NVE_65", "jobStatus": "OKAY",
    "systemResults": { "...": "..." }, "sets": [ { "F_ORT_NR": 42834, "F_ORT_TEXT": "...", "...": "..." } ],
    "durationMs": 812 }
  ```
- Allow group names (e.g. `D_MOTOR`) as well as SGBD names. Report the resolved variant.
- `JobPolicy` runs before anything touches the DLL. Rejections return 403 `job_not_allowed` with the matched rule.

### install.ps1 (elevated)
1. Create `C:\ProgramData\IstaAgent\`, copy the exe, write `config.json` (detect the 192.168.64.x address) and generate the token with `RandomNumberGenerator` (32 bytes → base64url). ACL the token file.
2. Add the firewall rule: `New-NetFirewallRule -DisplayName 'ista-bridge agent' -Direction Inbound -Protocol TCP -LocalPort 8765 -RemoteAddress 192.168.64.1 -Action Allow -Profile Any`.
3. Register the scheduled task:
   - trigger `-AtLogOn -User $env:USERNAME`;
   - principal `-LogonType Interactive -RunLevel Highest`;
   - settings `-ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries`.

   Then start the task immediately.
4. Disable sleep on AC (`powercfg /change standby-timeout-ac 0`). **Ask Ryan before** turning off the idle screen lock, and explain that a locked screen blocks UIA and screenshots.
5. Print the VM IP, port and token **once**, with instructions to put the token into the Mac Keychain (§7).

`uninstall.ps1` reverses all of this.

---

## 7. Mac MCP server specification

- TypeScript, `@modelcontextprotocol/sdk`, stdio transport, `zod` schemas. Build output goes to `mac-mcp/dist/index.js`.
- **Config:** env `ISTA_AGENT_URL` (e.g. `http://192.168.64.5:8765`). Read the token at startup with `security find-generic-password -s ista-bridge -a agent -w`, falling back to env `ISTA_AGENT_TOKEN` and warning to stderr.
- HTTP timeout: 15 s by default and 75 s for EDIABAS calls. If the agent is unreachable, explain clearly: VM not running, UTM window closed or VM asleep, wrong IP, firewall.
- Log to stderr only; stdout is the MCP channel. Write the audit log to `~/Library/Logs/ista-bridge/audit.log`.

### Tools
Every tool has a precise description and MCP annotations: `readOnlyHint: true` on all except clear, and `destructiveHint: true` on clear.

| Tool | Args | Returns |
|---|---|---|
| `ista_health` | — | agent status, ISTA/INPA running, EDIABAS DLL OK, `allowFaultClear` |
| `vm_list_windows` | — | compact table |
| `ista_read_screen` | `window?` (default `ista`), `format?` (`text` default), `maxNodes?`, `includeOffscreen?` | text rendering (default) or JSON |
| `ista_screenshot` | `window?` (default `ista`; `desktop` allowed), `maxEdge?` | MCP **image** content (base64 JPEG/PNG) plus a one-line caption |
| `vm_logs_list` | `root?`, `glob?`, `since?`, `limit?` | table of path/size/mtime |
| `vm_logs_read` | `path`, `tailBytes?` or `offset?`/`length?`, `grep?` | text |
| `ediabas_list_ecus` | `filter?` | SGBD/group names |
| `ediabas_vehicle_status` | — | battery V, ignition state |
| `ediabas_job` | `ecu`, `job`, `args?`, `results?` | formatted text + JSON (below) |
| `ediabas_read_faults` | `ecus[]` (≤ 20), `includeInfoMemory?` | convenience: `FS_LESEN` (+ `IS_LESEN`) across ECUs in one `/ediabas/jobs` call; per-ECU fault tables |
| `ediabas_clear_faults` | `ecu`, `scope` | **macOS dialog first** (below); then the agent call; then re-reads `FS_LESEN` and returns before/after |

### Formatting (`format.ts`)
- Fault tables: one row per set. Show the code as hex (from `F_ORT_NR`) with its text (`F_ORT_TEXT`), present/stored state, frequency count and logged mileage/conditions where the SGBD provides them. Field names vary by SGBD, so map the known ones and pass the rest through as `key=value`.
- **Mileage:** wherever a result is in km (e.g. `F_UW_KM` or any field whose unit/name indicates km), show miles first with km in brackets, e.g. `121,609 mi (195,712 km)`. Ryan wants distances in miles.
- Append the raw JSON after the text, truncated sensibly.

### Confirmation dialog (`confirm.ts`)
- Run `osascript -e 'display dialog "<text>" with title "ista-bridge" buttons {"Cancel","Clear faults"} default button "Cancel" cancel button "Cancel" with icon caution giving up after 60'`.
- Pass the text safely: escape quotes and backslashes, or pass it via `argv` to a small AppleScript using `on run argv`.
- Proceed only if the result is exactly `button returned:Clear faults` and not `gave up:true`. Anything else returns "cancelled by user".

### Registering with the Claude desktop app
Add to `~/Library/Application Support/Claude/claude_desktop_config.json` (merge; don't clobber existing servers):
```json
{
  "mcpServers": {
    "ista-bridge": {
      "command": "<absolute path to node, e.g. ~/.nvm/versions/node/vXX/bin/node, expanded>",
      "args": ["<absolute path>/ista-bridge/mac-mcp/dist/index.js"],
      "env": { "ISTA_AGENT_URL": "http://192.168.64.X:8765" }
    }
  }
}
```
Use absolute paths: the desktop app doesn't load nvm's PATH. Store the token in the Keychain with:
`security add-generic-password -s ista-bridge -a agent -w '<token>' -U`
Then restart the Claude desktop app.

---

## 8. Build phases and acceptance tests

Work phase by phase. Stop at each acceptance gate and ask Ryan to confirm before moving on.

| Phase | Build | Acceptance (Ryan confirms) |
|---|---|---|
| **0 Discovery** | `discover.ps1` | Output reviewed; open questions in §4 answered; decisions recorded in README |
| **1 Agent skeleton** | Kestrel host, config, auth, `/health`, `/windows`, install/uninstall scripts, audit log | From the Mac: `curl -H "Authorization: Bearer …" http://<vm-ip>:8765/health` → 200; without the token → 401. The agent survives a VM reboot (starts at log-on). `netstat` in the VM shows it listening only on the 192.168.64.x address |
| **2 Mac MCP** | MCP server with `ista_health` and `vm_list_windows`; Keychain token; desktop config | In a Claude chat, `ista_health` works and lists the ISTA window when ISTA is open |
| **3 Screen** | `/screen/text`, `/screen/capture`, `ista_read_screen`, `ista_screenshot` | With ISTA open on a vehicle-identification or fault-memory screen, `ista_read_screen` returns the visible text, including table rows; `ista_screenshot` returns a readable image; elevated-ISTA case works |
| **4 Logs** | `/logs`, `/logs/read`, Mac tools | Newest ISTA log listed and tail readable; `..`/junction escapes rejected (unit test + manual) |
| **5 EDIABAS read** | P/Invoke, runner, policy, `/ediabas/*`, Mac tools, formatter | **At the car, ignition on, ISTA and INPA closed:** `ediabas_vehicle_status` shows battery V and ignition on; `ediabas_job` `IDENT` on the engine ECU returns a variant; `ediabas_read_faults` on the Night Vision SGBD shows the historic A752/A751 pair seen in INPA on 11/09/2026; a denied job (e.g. `STEUERN_…`) is refused by **both** sides; with ISTA open, any job returns `cable_in_use` |
| **6 Fault clear** | `/ediabas/clear`, dialog, before/after re-read | With `allowFaultClear` false → refused. Set to true → dialog appears on the Mac; *Cancel* → nothing sent (audit log confirms); *Clear faults* on a chosen ECU (Ryan picks which) → after-read shows the result |
| **7 Hardening** | README complete, troubleshooting section, version stamp in `/health`, `release/` zip script | Ryan can reinstall from the README alone |

### Automated tests (no VM, no car)
- `vm-agent.Tests` (xUnit, `dotnet test` on the Mac):
  - `JobPolicy`: an allow/deny table covering every rule in §2.3, including case and prefix tricks such as `status_steuern_x`, `FS_LESEN;FS_LOESCHEN` and whitespace;
  - path-safety cases;
  - result-set shaping from a fake API.
- `mac-mcp/test` (vitest):
  - every tool against a stub HTTP agent;
  - `policy.ts` parity with `JobPolicy`, using one shared JSON fixture of test cases that both test suites load;
  - formatter output including km→miles;
  - the confirm flow with `osascript` mocked for Cancel, Clear and timeout.

---

## 9. Out of scope for v1 (don't build)
- Any keyboard/mouse/clipboard input into the VM or ISTA.
- Driving ISTA test plans, service functions, coding, programming or calibration.
- Any EDIABAS job outside the allowlist; raw `api*` access; changing `EDIABAS.INI`.
- Exposing the agent beyond UTM's Shared Network; remote/internet access.
- Moving the K+DCAN cable to the Mac (e.g. via EdiabasLib), which is possible later but not now.

## 10. Possible v2 (note in README only)
- An `ista_click_text` tool gated by the same macOS dialog, for stepping through ISTA screens when Ryan is at the car.
- Auto-detect the VM's IP via UTM's `utmctl ip-address <vm>` on the Mac, if `utmctl` is available.
- A saved "sweep" definition, i.e. a list of ECUs to read faults from, mirroring the 11/09/2026 INPA sweep.

## 11. Working agreements for Claude Code
- Ryan runs everything inside the VM. Give him copy-pasteable, elevated-PowerShell commands, one block at a time, and say what output to expect.
- Don't run anything that talks to the car. Phase 5/6 car tests are Ryan's, at the car.
- When a fact about ISTA/EDIABAS isn't confirmed by Phase 0 output or `api.h`, say so and verify it. Don't hard-code a guess.
- Keep `JobPolicy` and `policy.ts` in lockstep through the shared fixture; a policy change without a fixture update is a bug.
- Commit after each phase with a short message naming the phase.
