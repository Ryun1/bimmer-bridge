# ediabas-bridge

A read-only bridge that lets Claude read BMW diagnostics from Ryan's 2005 E61 550i. The car is
reached through a Windows VM in UTM on the Mac, where BMW Standard Tools (INPA, Tool32, NCS
Expert, WinKFP) and EDIABAS talk to the car over a K+DCAN cable. A small agent in the VM
(`vm-agent/`, C#) runs allowlisted read-only EDIABAS jobs, lists windows, takes screenshots and
reads trace files. A local MCP server on the Mac (`mac-mcp/`, TypeScript) finds the VM, calls
the agent and offers ten tools to Claude Code and the Claude desktop app. Skills in `skills/`
teach Claude how to run a diagnostic session.

## What it does and does not do

It does:
- read fault memory (`FS_LESEN`, `IS_LESEN`) from up to 20 ECUs in one call;
- run one allowlisted read-only job (identification, `STATUS_` reads) and show its results;
- list the SGBD files in the VM and describe an SGBD's jobs, saying which are runnable;
- report ignition and voltage readings;
- list the windows open in the VM and take a screenshot of one, or of the desktop;
- list and read trace files under the configured log folders (by default `C:\EDIABAS\TRACE`
  and `C:\NCSEXPER\WORK`).

It does not, and has no endpoint, tool, flag or setting that could:
- click, type or send any other input to the VM;
- clear faults, code, flash, or run activations or adaptations;
- send raw telegrams or change the EDIABAS configuration;
- use the cable while a BMW tool is open in the VM (there is no override).

Anything that writes to the car is done by Ryan, by hand, in INPA or NCS Expert. How each of
these limits is enforced: [docs/safety.md](docs/safety.md).

No BMW software, SGBD files or headers are stored in this repo (see `.gitignore`).

## How a request flows

```
Claude (Claude Code or the desktop app)
  |  MCP over stdio
  v
MCP server on the Mac (mac-mcp)     job policy check, audit line; finds the VM; token from the Keychain
  |  HTTP on UTM's shared network, port 8765, Bearer token
  v
agent in the VM (EdiabasAgent.exe)  source address and token check, job policy check, description
  |                                 screen, BMW-tool conflict check, audit line
  v
EDIABAS (api32.dll)  ->  K+DCAN cable (USB, passed through to the VM by UTM)  ->  car  ->  ECU
```

A refused job stops on the Mac and never reaches the VM; the agent checks again on its own. One
EDIABAS request runs at a time. Details: [docs/architecture.md](docs/architecture.md).

## Where to find what

| Doc | What it holds |
|---|---|
| this README | status, setup from scratch, updating, uninstalling, troubleshooting, logs, releasing |
| [docs/architecture.md](docs/architecture.md) | components, the request path, the agent's threads, where the policy runs, logs, trust boundaries |
| [docs/safety.md](docs/safety.md) | each read-only guarantee and what enforces it; the job policy in plain words; approving one job |
| [docs/at-the-car.md](docs/at-the-car.md) | checklist for a session at the car (Phase 6) |
| [docs/development.md](docs/development.md) | building and testing without the car, the replay agent, recording fixtures, policy fixtures |
| [docs/api.md](docs/api.md) | the agent's HTTP contract, including the exact job policy rules |
| [docs/tools.md](docs/tools.md) | the ten MCP tools and their error codes |
| [docs/deploy.md](docs/deploy.md) | how files and the token move between the Mac and the VM (`dev/serve.py`, the pasted lines) |
| [mac-mcp/README.md](mac-mcp/README.md) | the MCP server: environment variables, finding the VM, token, recording |
| [vm-agent/README.md](vm-agent/README.md) | the agent: projects, config file, command-line options |
| [vm-setup/README.md](vm-setup/README.md) | the four PowerShell scripts run in the VM |
| [policy/README.md](policy/README.md) | the job policy file and its shared test cases |
| [dev/README.md](dev/README.md) | the deploy server, the replay agent and the fixture scrubber |
| [dev/fixtures/README.md](dev/fixtures/README.md) | recorded fixtures: layout, recording and scrubbing a scenario |
| [skills/README.md](skills/README.md) | the two skills, their evals and how to install them |
| [docs/bmw-e61-skill-addition.md](docs/bmw-e61-skill-addition.md) | a section to paste into the separate `bmw-e61` skill |
| [docs/phase-results.md](docs/phase-results.md) | historical: Phase 0 VM discovery, Phase 1 to 5 acceptance results |
| [PLAN.md](PLAN.md) | historical: build plan v2 |
| [docs/plan-review.md](docs/plan-review.md) | historical: review of the original plan and why v2 differs |
| [docs/plan-v1-original.md](docs/plan-v1-original.md) | historical: the original (ISTA-based) plan |

<!-- STATUS: maintained by the coordinator -->
## Status

Version 0.2.2 (agent, MCP server and `VERSION`), job policy version 2. The build work is merged
to `main`.

| Phase | State |
|---|---|
| 0 Discovery | done (2026-11-21) |
| 1 Agent skeleton | done (2026-11-21): installed in the VM, self-test passes, came back after a Windows restart |
| 2 EDIABAS at the desk | done (2026-11-21, agent 0.2.0): every desk check passes. Agent 0.2.2 deployed and used at the desk on 2026-11-22 |
| 3 Mac MCP | done: all ten tools. Checked in Claude Code and in the desktop app (`ediabas_describe` on `NVE_65`, 2026-11-21) |
| 4 Screenshots | done (2026-11-21): INPA screenshot readable, locked screen refused |
| 5 Logs | done (2026-11-21): listing, reading and every path refusal pass, in the VM self-test too |
| 6 At the car | built (vehicle status, fault tables). **Not run: this is Ryan's, at the car** |
| 7 Skills | both skills installed; replay agent with `idle` and `inpa-open` fixtures; evals 02, 05, 07 and 08 pass with the skills; skill references filled from the desk recordings of 2026-11-21/22 |
| 8 Hardening | done: this README, `VERSION`, `release.sh` |

Open:
- **Phase 6 at the car**, Ryan's: [docs/at-the-car.md](docs/at-the-car.md).
- **Evals 01, 03, 04 and 06** wait for the `nve-faults`, `needs-review` and `battery` fixtures,
  recorded at the car ([dev/fixtures/README.md](dev/fixtures/README.md)).
- **Skill reference entries that need car recordings**: the vehicle-status field names, the
  engine SGBD, and the SGBDs that can only be described with the control unit answering.
- **Restart checks on 0.2.x**: that the agent comes back after the VM reboots, and that the
  bridge works again after the Mac sleeps. The reboot check passed on the Phase 1 agent only.

The acceptance results of Phases 0 to 5 are in [docs/phase-results.md](docs/phase-results.md).
<!-- /STATUS -->

## Layout

| Path | What |
|---|---|
| `vm-agent/` | the Windows agent (C#, .NET 10, win-x86). `vm-agent/build.sh` writes `release/EdiabasAgent.zip` |
| `vm-setup/` | `discover.ps1`, `install.ps1`, `update.ps1`, `uninstall.ps1`, run in the VM |
| `mac-mcp/` | the local MCP server Claude starts (TypeScript); built to `mac-mcp/dist/index.js` |
| `policy/` | `policy.json`, the job policy both sides load, and its shared test cases `policy-cases.json` and `policy-parse-cases.json` |
| `dev/serve.py` | Mac-side server that hands the scripts and the agent to the VM and takes the token back |
| `dev/replay-agent.ts` | a stand-in agent that answers from recorded fixtures (no VM, no car) |
| `dev/scrub-fixtures.ts` | copies a raw recording into `dev/fixtures/` with identifiers removed |
| `skills/` | Claude skills for using the tools, and their evals; `skills/install.sh` installs them |
| `release.sh` | checks versions, runs every test suite, builds the agent zip and the MCP server |

---

## Setup from scratch

What you start with: this repo on the Mac, UTM with the Windows VM named `Windows` (network
mode **Shared Network**), BMW Standard Tools already installed in the VM, and a Windows user
who is an administrator. Do the steps in order. Every command on the Mac is run from the repo
root unless it says otherwise.

### 1. Prerequisites on the Mac

```sh
brew install node          # Node 24 or later (the MCP server needs 20+, the dev tools 24+);
                           # also gives the stable path /opt/homebrew/bin/node
brew install dotnet@10     # .NET 10 SDK, to build and test the agent
python3 --version          # Python 3, for dev/serve.py (the macOS one is enough)
```

`dotnet@10` is keg-only, so it is not on `PATH` by default. `release.sh` and `vm-agent/build.sh`
find it in the Homebrew folder by themselves; to run `dotnet` by hand, add these to your shell
profile:

```sh
export DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec
export PATH=/opt/homebrew/opt/dotnet@10/bin:$PATH
```

Install the MCP server's dependencies once (`release.sh` stops if they are missing):

```sh
(cd mac-mcp && npm ci)
```

### 2. Build and test

```sh
./release.sh
```

It checks that the versions agree, runs every test suite, builds `release/EdiabasAgent.zip`
and `mac-mcp/dist/index.js`, and ends with `Release <version> built and tested.` and the zip's
SHA-256. It stops at the first failure with `release.sh: FAILED: <reason>`. Details in
[Releasing](#releasing). `./release.sh --dry-run` shows the steps without running them.

### 3. Install the agent in the VM

1. Start the `Windows` VM in UTM and log in to Windows. The agent runs in your logged-in
   session, so someone must be logged in for it to work at all.
2. On the Mac, start the deploy server so that it will accept the agent's token:

   ```sh
   python3 dev/serve.py --accept-token
   ```

   It binds to the Mac's UTM bridge address on port 8000, serves only the VM's address (the
   latest DHCP lease for the VM's MAC, or `--client <VM IPv4>`), and prints the lines to
   paste, with the address, the zip's SHA-256 and each script's SHA-256 filled in. Each line
   downloads its script, checks the script's SHA-256 and only then runs it. The Install line
   looks like this (one line; copy it from the terminal, do not type it):

   ```
   Install:
     $base='http://<Mac bridge address>:8000'; $sha='<zip sha256>'; $ebFile="$env:TEMP\ediabas-bridge-install.ps1"; iwr "$base/install.ps1" -UseBasicParsing -OutFile $ebFile; $ebBytes=[IO.File]::ReadAllBytes($ebFile); if (([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ebBytes)) -replace '-','') -eq '<install.ps1 sha256>') { iex ([Text.Encoding]::UTF8.GetString($ebBytes)) } else { Write-Host 'install.ps1 is not the file serve.py checked; it was NOT run. Restart serve.py and paste the line it prints.' -ForegroundColor Red }
   ```

   The Update, Discovery and Uninstall lines have the same shape. If it says
   `error: no UTM shared-network interface (bridge1xx) ... is up`, the VM is not running yet.
   If it says `error: no DHCP lease for the VM's MAC`, start the VM, or pass `--client` with
   the address `ipconfig` shows in the VM. If it says `(release/EdiabasAgent.zip not built;
   install/update lines not shown)`, run step 2 first. Start `serve.py` after the build and
   after any change to `vm-setup/`: the hashes it prints are those of the files at start-up.
3. In the VM, open **Windows PowerShell** (not PowerShell 7) with **Run as administrator**,
   and paste the **Install** line.
4. Success looks like this (blue `==>` lines, no red `ERROR:`):
   - `SHA-256 OK: ...`
   - `Generating a new token` (first install) or `token.txt already exists; keeping it`
   - `Firewall: inbound TCP 8765 from <gateway> only`
   - `Agent is up.` Usually with the note that it answered 403 because the request came from
     the VM itself; that is expected and proves the listener works.
   - `token stored in the Mac's Keychain`
   - `Done. ediabas-bridge agent <version> listens on port 8765.`

   On the Mac, `serve.py` logs `PUT /token 204 token stored`. The token went straight into the
   Keychain (service `ediabas-bridge`, account `agent`); nobody copies it.

   If something else happens, see [Troubleshooting](#troubleshooting). The common ones at this
   step are PowerShell 7 or a non-elevated window (the script refuses and changes nothing), a
   `SHA-256 mismatch` (the zip was rebuilt after `serve.py` started), and `Could not send the
   token to the Mac` (`serve.py` was started without `--accept-token`).
5. Stop `serve.py` with Ctrl-C. It should not run when you are not deploying.

What the installer did, so you know what to look for later: it created
`C:\ProgramData\EdiabasBridge\` (exe, `config.json`, `token.txt`, `logs\`), a firewall rule
and a scheduled task both named `ediabas-bridge agent` (at log-on, and every 5 minutes in case
the agent crashed), and turned off sleep on AC power. It did not touch the screen lock.
Re-running it is safe: it upgrades in place and keeps `config.json` and `token.txt`.

Optional check in the VM (elevated Windows PowerShell), with the agent running:

```powershell
& 'C:\ProgramData\EdiabasBridge\EdiabasAgent.exe' --selftest
Get-Content 'C:\ProgramData\EdiabasBridge\logs\selftest.txt'
```

It checks the config, the job policy built into the agent, the token file's permissions, the
bind address, the firewall rule, the listener, that `api32.dll` loads and has every export the
agent uses (nothing in it is called; skipped while a BMW tool or the running agent has it
loaded), the log-path refusals and a desktop capture. It sends no network traffic and does not
use the cable, and writes the same report to `selftest.txt`.

### 4. Register the MCP server

Use a stable Node path, `/opt/homebrew/bin/node`, not a path inside an nvm version folder, so
that a Node upgrade does not break the registration. The server must stay at
`mac-mcp/dist/index.js` inside the repo: it loads `policy/policy.json` from two folders up.

**Claude Code** (all projects), from the repo root:

```sh
claude mcp add --scope user ediabas-bridge -- /opt/homebrew/bin/node "$PWD/mac-mcp/dist/index.js"
```

**Claude desktop app:** quit the app, then add an `ediabas-bridge` entry under `mcpServers` in
`~/Library/Application Support/Claude/claude_desktop_config.json`. Keep every other key in the
file as it is; create the `mcpServers` key if it is not there. `<repo>` is the absolute path of
this repo (`pwd` in the repo root prints it):

```json
{
  "mcpServers": {
    "ediabas-bridge": {
      "command": "/opt/homebrew/bin/node",
      "args": ["<repo>/mac-mcp/dist/index.js"]
    }
  }
}
```

Start the app again. The first time the desktop app's server reaches the VM, macOS asks "node
would like to find devices on your local network": choose **Allow**. If you missed it, the
tools report `local_network_blocked`; see [Troubleshooting](#troubleshooting).

The token needs no configuration: the server reads it from the Keychain.

### 5. Install the skills

```sh
skills/install.sh
```

It links each skill (`ediabas-bridge`, `bmw-standard-tools`) into `~/.claude/skills/` for
Claude Code, so edits in the repo take effect at once, and builds `skills/dist/<skill>.zip`.
It refuses, and leaves alone, anything already in `~/.claude/skills/` under the same name that
is not its own link. Upload the two zips to claude.ai for the desktop app and cloud sessions
(the exact upload menu was not recorded when this was written).

`docs/bmw-e61-skill-addition.md` is a short section to paste into the separate `bmw-e61`
skill by hand, so that it hands over to this bridge.

### 6. First check

Start a new Claude Code session (or a new chat in the desktop app) and ask "is the BMW VM up?".
Claude should call `vm_health` and report the agent's version, the Windows session it runs in,
any BMW tools that are open, whether the EDIABAS DLL was found, the agent's features and the
job policy version on both sides. In Claude Code, `/mcp` shows whether `ediabas-bridge` is
connected.

If it reports an error, the message says what to do; the [Troubleshooting](#troubleshooting)
table has the same cases.

---

## Updating

After a code change, or to install a newer build:

1. On the Mac: `./release.sh`. It prints the new SHA-256.
2. Start the VM and log in if it is not running, then on the Mac: `python3 dev/serve.py`
   (no `--accept-token` needed; the token does not change on an update).
3. In the VM, in **Windows PowerShell run as administrator**, paste the **Update** line that
   `serve.py` printed. It verifies the hash, stops the agent, replaces the exe, starts the
   scheduled task and ends with `Updated to ediabas-bridge agent <version>`. It touches nothing
   else (config, token, firewall, task settings). If it says the agent is not installed, use the
   Install line instead.
4. Stop `serve.py` with Ctrl-C.
5. **Restart Claude** so it starts the new MCP server from the rebuilt `mac-mcp/dist`: quit and
   reopen the desktop app, and start a new Claude Code session. An already-running server keeps
   the old code.
6. Skills: Claude Code sees changes at once through the links. For claude.ai, run
   `skills/install.sh` again and upload the new zips.
7. Check with `vm_health`: the agent's version should be the new one, with no policy-version
   warning.

## Uninstalling

Start `python3 dev/serve.py` on the Mac first, then in the VM, in **Windows PowerShell run as
administrator**, paste the **Uninstall** line `serve.py` printed (it checks `uninstall.ps1`'s
SHA-256 before running it).

It stops and removes the scheduled task, the firewall rule and `C:\ProgramData\EdiabasBridge`.
To keep `config.json`, `token.txt` and `logs\` for a later reinstall, put `$KeepData=$true; `
at the start of that line.

It leaves the sleep setting as the installer set it, and prints how to restore it
(for example `powercfg /change standby-timeout-ac 30`).

On the Mac:

```sh
claude mcp remove --scope user ediabas-bridge              # Claude Code
rm ~/.claude/skills/ediabas-bridge ~/.claude/skills/bmw-standard-tools   # the skill links only
security delete-generic-password -s ediabas-bridge -a agent # the token in the Keychain
```

For the desktop app, delete the `ediabas-bridge` entry from `mcpServers` in
`~/Library/Application Support/Claude/claude_desktop_config.json` and restart the app. Remove
the uploaded skills in claude.ai if you uploaded them. Keep the Keychain item if you uninstalled
with `$KeepData=$true` and mean to reinstall: the kept `token.txt` still matches it.

---

## Troubleshooting

Tool errors carry a code (in **bold** below) and a message that already says what to do. The
codes are listed in [docs/tools.md](docs/tools.md#errors).

| Symptom | Cause | Fix |
|---|---|---|
| **vm_not_running**: "The VM does not appear to be running" | The VM is off, or UTM's shared network is not up on the Mac | Start the `Windows` VM in UTM and log in |
| **agent_not_answering**: "The VM is up but the agent is not answering" | Nobody is logged in to Windows (the agent starts at log-on), or the agent is not installed or has stopped | Log in to Windows and wait about 30 s. If it persists, read `agent.log` in the VM (see logs below) and re-run the Install line |
| **local_network_blocked**, or macOS asked "node would like to find devices on your local network" | macOS Local Network permission for `node`. The desktop app starts the server so that macOS treats `node` as its own app | System Settings > Privacy & Security > Local Network: allow `node`, then ask again. The permission follows the node binary's real path, so after a Homebrew upgrade of node it can ask again |
| **unauthorized**: "The agent rejected the token" | The Keychain token does not match the VM's `token.txt` (for example after a reinstall without `$KeepData`) | On the Mac `python3 dev/serve.py --accept-token`, then paste the Install line again in the VM. It keeps the VM's token and sends it to the Keychain. Nothing else needs a restart: the server re-reads the token after a 401 |
| **no_token** | Nothing in the Keychain (the first install's hand-off failed) | As for `unauthorized` |
| **forbidden** | The agent or its firewall refused the Mac's address: requests are accepted only from the VM's default gateway | Re-run the Install line; it recreates the firewall rule for the VM's current gateway. `agent.log` in the VM shows the address and gateway the agent chose |
| **agent_outdated**, or `vm_health` says "Agent features: not reported" | The agent in the VM is an older build that lacks that endpoint | [Updating](#updating): run the Update line |
| `vm_health` warns that the agent's job policy differs from the Mac's | The agent and the Mac server were built from different versions of `policy/policy.json` | Run `./release.sh`, the Update line, and restart Claude |
| **bad_response** | The agent and the MCP server are different versions | As above |
| **policy_unavailable** | `policy/policy.json` could not be loaded by the Mac server; every `ediabas_*` tool refuses | Restore the file from git, then restart Claude |
| **cable_in_use**, naming a process | A BMW tool that uses the cable is open in the VM (INPA, Tool32, NCS Expert, WinKFP, BMW Coding Tool, or anything with `api32.dll` loaded). There is no override, by design | Close that tool in the VM, then ask once more. Do not retry in a loop |
| **cable_not_found**: EDIABAS error 28, `IFH-0018: INITIALIZATION ERROR` | The K+DCAN cable is not connected to the VM. Needed even for `ediabas_describe` of a real SGBD; only `TMODE` and `UTILITY` load without it | Plug the cable into the Mac and pass it through to the `Windows` VM in UTM (the USB button in the VM's toolbar), then ask again |
| **ediabas_error** or **job_timeout** | EDIABAS reached the interface but the ECU did not answer: ignition off, cable not in the car, or that ECU is not fitted | Check `ediabas_vehicle_status`, then decide before trying again |
| **ediabas_error** 16 `IFH-0006: COMMAND NOT ACCEPTED` on every real SGBD, after the cable was unplugged from the Mac and plugged back in | UTM does not hand a replugged cable back to the VM by itself | Tick the cable for the VM in UTM's USB menu, then ask again |
| The first EDIABAS call after a pause takes about 10 s | Expected. The agent unloads EDIABAS after about 20 s with no EDIABAS request, so that nothing holds the interface open; the next call loads it again | Nothing. The Mac waits up to 75 s for EDIABAS calls |
| **job_needs_review**, quoting a job's description | A `STATUS_` job whose description suggests it may do more than read, or has none | Ryan decides. To allow that one job, add `"ECU:JOB"` (upper case) to `reviewedJobs` in `C:\ProgramData\EdiabasBridge\config.json` in the VM, then restart Windows (the agent reads its config at start-up); steps in [docs/safety.md](docs/safety.md#approving-one-job). Never make this routine. Approved so far (2026-11-22, in the VM's config only): `CAS:STATUS_WAKEUP_HISTORY`, `CAS:STATUS_KL15_ABSCHALTUNG`, `CAS:STATUS_KLEMMENVERHINDERER`, `CAS:STATUS_WUP`, `MPM_60:STATUS_AD_WERTE_LESEN` |
| **ediabas_unavailable**, or `vm_health` says "EDIABAS: STUCK" | A job did not stop after `apiBreak`; the agent then leaves EDIABAS alone until it restarts (`/health` `ediabas.stuck`). The same code also means `api32.dll` is missing or EDIABAS would not start | Restart Windows in the VM, which restarts the agent. If `vm_health` says "EDIABAS DLL: NOT found", check `ediabasDll` in the VM's `config.json` |
| **not_run** | The agent did not start the job, or stopped it: the request's time budget ran out, a BMW tool was opened during the request, an earlier job did not stop, or the request was abandoned. The message says which | Act on the message (close the tool, describe with a `filter`, ...). Do not retry in a loop |
| **agent_not_answering** after a call that runs an EDIABAS job, saying the job may have run | The connection broke or timed out after the request may have reached the agent. Such requests are never sent a second time | Call `vm_health` before asking again |
| **job_not_allowed** | The job policy refused the job by name | By design. Do not look for another job or tool that does the same; writes are done by hand in INPA or NCS Expert |
| **desktop_unavailable** (screenshots) | Windows is locked, showing a secure desktop (for example a UAC prompt), or nobody is logged in | Log in to or unlock Windows. The installer does not change the screen lock |
| **window_minimized** | The window asked for is minimised; the agent never restores windows | Restore it in the VM, then ask again |
| Script says `This script must run in "Windows PowerShell" (5.1), not PowerShell 7` | The line was pasted into PowerShell 7 | Open **Windows PowerShell** as administrator and paste it again. Nothing was changed |
| Script says `This script must run in an elevated PowerShell` | The window was not run as administrator | Right-click Windows PowerShell, Run as administrator, paste again |
| Script says `SHA-256 mismatch` | The zip was rebuilt after `serve.py` printed its lines, or the line was mistyped | Stop and restart `serve.py` and paste the line it prints now. Nothing was changed |
| The line prints `... is not the file serve.py checked; it was NOT run` | The script in `vm-setup/` changed after `serve.py` started, or something other than `serve.py` answered on the Mac's address | Restart `serve.py` and paste the line it prints now. If it happens again, stop: something else is answering on port 8000. Nothing was run |
| `iwr` cannot connect to the Mac | `serve.py` is not running, or the address in `$base` is wrong | Start `serve.py` and copy its line exactly |
| `iwr` gets `403 Forbidden` (`only the VM's address is served`) | `serve.py` took a different address for the VM (an old DHCP lease) | Restart it with `python3 dev/serve.py --client <VM address>` (`ipconfig` in the VM shows it) |
| Install prints `Could not send the token to the Mac (HTTP 403)` and a `security add-generic-password ...` command | `serve.py` was started without `--accept-token` | Either run the printed `security` command once on the Mac (it contains the token: do not paste it anywhere else), or restart `serve.py --accept-token` and run the Install line again |
| Install prints `The agent did not answer within 20 s` | The agent could not start or bind | Read `agent.log` in the VM, and run the `--selftest` check from [step 3](#3-install-the-agent-in-the-vm) |
| **agent_not_answering** after `config.json` was edited and the agent restarted | The config is invalid (for example `maxJobTimeoutSeconds` above 60, or `jobTimeoutSeconds` above it). The agent does not exit: it logs the problem and re-reads the config and token every 30 s | Read `agent.log` in the VM, fix `config.json`; the agent starts within 30 s |
| `serve.py` says `no UTM shared-network interface (bridge1xx) ... is up` | The VM is not running, so the Mac has no bridge address to bind | Start the VM, then `serve.py` |
| `serve.py` says `no DHCP lease for the VM's MAC` | The VM has not taken an address yet, or `EDIABAS_VM_MAC` is not its MAC | Start the VM and log in, or pass `--client <VM address>` |
| A tool you expect (screenshots, logs, EDIABAS) is not offered, after an update | Claude is still running the old MCP server | Quit and reopen the desktop app; start a new Claude Code session |

### Logs and audit files

| Where | File | What |
|---|---|---|
| VM | `C:\ProgramData\EdiabasBridge\logs\agent.log` | the agent's start-up, bind decisions and errors |
| VM | `C:\ProgramData\EdiabasBridge\logs\audit.log` | one line per request: endpoint, ECU, job, outcome, rule for refusals |
| VM | `C:\ProgramData\EdiabasBridge\logs\selftest.txt` | the last `--selftest` report |
| Mac | `~/Library/Logs/ediabas-bridge/audit.log` | one line per tool call: tool, outcome, duration, ECU and job |
| Mac | `~/Library/Logs/Claude/mcp-server-ediabas-bridge.log` | the MCP server's stderr when the desktop app runs it (discovery, failed probes; never the token) |
| Mac | the terminal running `serve.py` | one line per request it served, including `PUT /token` |

`agent.log` and `audit.log` in the VM each roll over to a `.1` file at 5 MB (one old file is
kept). What each audit line holds: [docs/architecture.md](docs/architecture.md#logs-and-audit).
In Claude Code, `/mcp` shows whether the server is connected.

---

## Safety rules

The ten hard rules of [PLAN.md section 2](PLAN.md#2-hard-safety-rules-non-negotiable) are
enforced in code on both sides, not only in tool descriptions or skills. Which code enforces
each one, how the job policy decides, and how to approve a single job are in
[docs/safety.md](docs/safety.md). Check any change against that list.

**Anything that runs against the car is Ryan's, at the car.** Claude Code may run only offline
jobs (`TMODE`, the `_JOBS` family) after Ryan confirms the cable is unplugged.

---

## Releasing

```sh
./release.sh                 # everything
./release.sh --no-build      # versions and tests only
./release.sh --skip-dotnet   # without the .NET tests and agent build (not a release)
./release.sh --dry-run       # check the versions and print the steps
```

`release.sh`:
1. checks that these versions are the same and are `x.y.z`: `VERSION`, `<Version>` in
   `vm-agent/Directory.Build.props`, and `version` in `mac-mcp/package.json` and
   `mac-mcp/package-lock.json`. To bump a version, change all of them (for the lock file,
   `npm --prefix mac-mcp version <x.y.z> --no-git-tag-version` updates both `mac-mcp` files);
2. runs the agent tests (`dotnet test vm-agent/EdiabasAgent.Tests`), the MCP server's
   typecheck and tests, the deploy server's tests (`python3 -m unittest dev/test_serve.py`) and
   the dev tools' typecheck and tests;
3. builds `release/EdiabasAgent.zip` and `release/EdiabasAgent.zip.sha256`
   (`vm-agent/build.sh`) and `mac-mcp/dist` (`npm --prefix mac-mcp run build`);
4. checks that the zip's `version.txt` is the release version and that the `.sha256` file
   matches the zip, then prints the SHA-256 and the Update line.

It stops at the first failure and never pushes, tags or deploys anything.

To run one part by hand:

```sh
(cd vm-agent && dotnet test EdiabasAgent.Tests)
vm-agent/build.sh
(cd mac-mcp && npm test && npm run build)
python3 -m unittest dev/test_serve.py
(cd mac-mcp && npm run test:dev)
```

To exercise the MCP server and the skills with no VM and no car, use the replay agent:
[dev/fixtures/README.md](dev/fixtures/README.md).

`serve.py` also prints a **Discovery** line: it runs `vm-setup/discover.ps1`, which only reads,
and uploads its report into `discovery/` (git-ignored). It was used once, for Phase 0
([docs/phase-results.md](docs/phase-results.md)).
