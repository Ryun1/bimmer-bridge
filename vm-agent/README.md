# vm-agent: the agent in the VM

`EdiabasAgent.exe`, the HTTP agent that runs in the Windows VM and calls EDIABAS. C# on .NET 10,
published as a self-contained win-x86 single file (32-bit, like `api32.dll`). Its HTTP contract
is [docs/api.md](../docs/api.md); how it fits with the rest is
[docs/architecture.md](../docs/architecture.md).

| Path | What |
|---|---|
| `EdiabasAgent.Core/` | everything that can be tested on the Mac: config, job policy (embeds `policy/policy.json`), description screen, EDIABAS service against an `IEdiabasApi`, path rules, result shaping, audit line format |
| `EdiabasAgent/` | the Windows host: Kestrel, request pipeline, P/Invoke into `api32.dll`, the EDIABAS thread, window capture, log access, `--selftest` |
| `EdiabasAgent.Tests/` | xUnit tests of `EdiabasAgent.Core` |
| `Directory.Build.props` | the version (`<Version>`), which must match `VERSION` |
| `build.sh` | writes `release/EdiabasAgent.zip` (exe, `version.txt`, `commit.txt`) and its `.sha256`; stops on uncommitted changes unless `--allow-dirty` |

```sh
(cd vm-agent && dotnet test EdiabasAgent.Tests)   # tests, on the Mac
vm-agent/build.sh                                 # the release zip
```

Both find the .NET 10 SDK in the Homebrew folder if `dotnet` is not on `PATH`. The agent is
installed and updated in the VM with the scripts in [vm-setup/](../vm-setup/README.md).

## Command line

| | |
|---|---|
| no arguments | run the agent (the scheduled task does this) |
| `--selftest` | run the checks, print PASS/FAIL, write `logs\selftest.txt`; exit 0 or 1 |
| `--version` | print the version |
| `--config <path>` | use another config file |

## Config: `C:\ProgramData\EdiabasBridge\config.json`

Written by `install.ps1` on first install and never overwritten. Read at start-up: restart the
agent (restart Windows) after changing it. The one exception is `reviewedJobs`: the agent
re-reads it, and nothing else, at every EDIABAS request, so a new approval needs no
restart. Missing keys take the defaults; comments and
trailing commas are allowed. An unknown top-level key is ignored with a warning in `agent.log`
(`config: unknown key "reviewJobs" is ignored (a typo?)`), and so is a `logRoots` entry the
path rules refuse (logged as not used, with the reason). An invalid config does not make the
agent exit: it logs why in `agent.log` and re-reads the file every 30 s.

| Key | Default | Meaning |
|---|---|---|
| `bindAddress` | `auto` | `auto` (the shared-network address) or one literal IPv4 address; never a wildcard |
| `port` | 8765 | |
| `tokenFile` | `C:\ProgramData\EdiabasBridge\token.txt` | |
| `ediabasDll` | `C:\EDIABAS\BIN\api32.dll` | |
| `ecuPath` | `C:\EDIABAS\ECU` | the SGBD folder |
| `logDirectory` | `C:\ProgramData\EdiabasBridge\logs` | `agent.log`, `audit.log`, `selftest.txt` |
| `logRoots` | `C:\EDIABAS\TRACE`, `C:\NCSEXPER\WORK` | the only folders `/logs` may read |
| `conflictProcesses` | `INPALOAD`, `TOOL32`, `NCSEXPER`, `WINKFPT`, `BMW Coding Tool`, `WINKFP`, `ISTAGUI` | process names that hold the cable (any process with `api32.dll` loaded counts too) |
| `voltageSources` | empty | extra voltage-reading jobs `/ediabas/vehicle` runs, as `{ "ecu": …, "job": … }` objects, on top of the UTILITY and TMODE voltages it always reads; car-specific, e.g. `{ "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" }` (the E60/E61 night-vision module) |
| `reviewedJobs` | empty | approvals that skip the description screen: `ECU:JOB@<12 hex>` (pinned to the description; recommended) or `ECU:JOB` (by name). An entry of another shape is a warning and approves nothing ([docs/safety.md](../docs/safety.md#approving-one-job)) |
| `jobTimeoutSeconds` | 30 | default per job; at most `maxJobTimeoutSeconds` |
| `maxJobTimeoutSeconds` | 60 | a request's time budget; at most 60, because the Mac stops waiting after 75 s |
| `ediabasIdleReleaseSeconds` | 20 | unload `api32.dll` after this long without an EDIABAS request (5 to 600; another value is a config error). Read at start-up only |
