# Deploy and discovery — contract

How files move between the Mac and the VM. `dev/serve.py`, the `vm-setup/*.ps1` scripts and
`vm-agent/build.sh` are all built against this file.

## The Mac-side server: `dev/serve.py`
- Listens on the Mac's UTM bridge address, port **8000**. Never `0.0.0.0`.
- Routes, and nothing else (no directory listing, every other path is 404):

| Request | Behaviour |
|---|---|
| `GET /discover.ps1`, `/install.ps1`, `/update.ps1`, `/uninstall.ps1` | the file of that name from `vm-setup/`, as `text/plain; charset=utf-8` |
| `GET /EdiabasAgent.zip` | `release/EdiabasAgent.zip`; 404 if not built |
| `PUT /upload/<name>` | saved as `discovery/<UTC yyyyMMdd-HHmmss>-<name>`. `<name>` must match `^[A-Za-z0-9._-]{1,80}$`. Body limit 20 MB. Replies 201 `{"saved": "<file name>", "bytes": <n>}` |
| `PUT /token` | only when started with `--accept-token`, otherwise 403. Body is the agent token, which must match `^[A-Za-z0-9_-]{43}$` (surrounding whitespace is ignored). Stored in the macOS Keychain (service `ediabas-bridge`, account `agent`), never written to disk or printed. Replies 204. One token per run |

## Variables the VM scripts read
The scripts are run with `irm <url> | iex`, so they take input from variables set earlier on
the same pasted line.

| Variable | Used by | Meaning |
|---|---|---|
| `$base` | all | server URL. Optional; default is `http://<IPv4 default gateway>:8000` |
| `$sha` | install, update | expected SHA-256 of `EdiabasAgent.zip`, hex, any case. Required |
| `$Elevated` | install | `$true` to run the agent with highest privileges. Optional, default off |
| `$KeepData` | uninstall | `$true` to keep `config.json`, `token.txt` and `logs\`. Optional, default off |

All three scripts must run in an elevated **Windows PowerShell** (5.1) window, not PowerShell 7.

## The release artefact
`vm-agent/build.sh` writes:
- `release/EdiabasAgent.zip` — `EdiabasAgent.exe` and `version.txt` at the zip root;
- `release/EdiabasAgent.zip.sha256` — the hex digest, one line.

## What lives in the VM
| Path | Purpose | Agent's access |
|---|---|---|
| `C:\ProgramData\EdiabasBridge\EdiabasAgent.exe` | the agent | read, execute |
| `C:\ProgramData\EdiabasBridge\config.json` | configuration | read |
| `C:\ProgramData\EdiabasBridge\token.txt` | bearer token | read |
| `C:\ProgramData\EdiabasBridge\logs\agent.log` | agent's own log | write |
| `C:\ProgramData\EdiabasBridge\logs\audit.log` | one line per request | write |

## Token hand-off
`install.ps1` sends the agent's token to `PUT $base/token` on every run. With
`serve.py --accept-token` running, the token lands in the Mac's Keychain and nobody copies it
by hand. Without the flag the server answers 403: a first install then prints the token and
the `security` command once; a later run prints only a note. To re-send a token, start
`serve.py --accept-token` and run the install line again.

- Scheduled task name: `ediabas-bridge agent`. Triggers: at log-on, and every 5 minutes (a
  second copy exits at once, so this only matters after a crash).
- Firewall rule display name: `ediabas-bridge agent`.
