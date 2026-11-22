# Deploy and discovery — contract

How files move between the Mac and the VM. `dev/serve.py`, the `vm-setup/*.ps1` scripts and
`vm-agent/build.sh` are all built against this file.

## The Mac-side server: `dev/serve.py`
- Listens on the Mac's UTM bridge address, port **8000**. Never `0.0.0.0`.
- Serves **only the VM's address**; every request from any other address (another VM on the
  same UTM network included) is 403. The address is the one given with `--client <IPv4>`, or
  else the newest DHCP lease for the VM's MAC, the same address the MCP server uses (see
  [Finding the agent](architecture.md#the-request-path)). No lease and no `--client`: it does
  not start. It must be a host address on the bind address's /24. It never trusts whoever
  connects first.
- Per run, at most 10 uploads and 40 MB of uploads in all (each upload at most 20 MB). The
  caps are taken before anything is written; a failed upload keeps its share. Past them,
  413: restart `serve.py`.
- Routes, and nothing else (no directory listing, every other path is 404):

| Request | Behaviour |
|---|---|
| `GET /discover.ps1`, `/install.ps1`, `/update.ps1`, `/uninstall.ps1` | the file of that name from `vm-setup/`, as `text/plain; charset=utf-8` |
| `GET /EdiabasAgent.zip` | `release/EdiabasAgent.zip`; 404 if not built |
| `PUT /upload/<name>` | saved as `discovery/<UTC yyyyMMdd-HHmmss>-<name>`. `<name>` must match `^[A-Za-z0-9._-]{1,80}$`. Body limit 20 MB. Replies 201 `{"saved": "<file name>", "bytes": <n>}` |
| `PUT /token` | only when started with `--accept-token`, otherwise 403. Body is the agent token, which must match `^[A-Za-z0-9_-]{43}$` (surrounding whitespace is ignored). Stored in the macOS Keychain (service `ediabas-bridge`, account `agent`), never written to disk or printed, then read back with `security find-generic-password -w` and compared; a mismatch is 500. Replies 204. Exactly one well-formed token per run, whether or not storing it worked: any later `PUT /token` is 403 until `serve.py` is restarted |

## The pasted lines
`serve.py` prints one line per script (Discovery, Install, Update, Uninstall), with the address
filled in, the zip's SHA-256 as `$sha` for Install and Update, and the SHA-256 of the script
file as it was at start-up. Each line, for Windows PowerShell 5.1:

```powershell
$base='http://<Mac bridge address>:8000'; $sha='<zip sha256>'; $ebFile="$env:TEMP\ediabas-bridge-install.ps1"; Remove-Item -LiteralPath $ebFile -Force -ErrorAction SilentlyContinue; $ebOk=$false; try { iwr "$base/install.ps1" -UseBasicParsing -OutFile $ebFile -ErrorAction Stop; $ebOk=$true } catch { Write-Host 'download failed: the Mac did not serve install.ps1 (is serve.py running?)' -ForegroundColor Red }; if ($ebOk) { $ebBytes=[IO.File]::ReadAllBytes($ebFile); if (([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ebBytes)) -replace '-','') -eq '<install.ps1 sha256>') { iex ([Text.Encoding]::UTF8.GetString($ebBytes)) } else { Write-Host 'install.ps1 is not the file serve.py checked; it was NOT run. Restart serve.py and paste the line it prints.' -ForegroundColor Red } }
```

- Any old copy of the temp file is deleted first. If the download fails, the line stops with
  `download failed: the Mac did not serve <script> (is serve.py running?)`.
- The script is downloaded to a file, its bytes are read once, and those same bytes run (with
  `iex`) only when their SHA-256 is the one in the line. Anything else answering on the
  gateway address (a rogue listener, another VM spoofing the gateway) cannot run a script of
  its own: the trust is in the pasted line, which came from the Mac's terminal.
- The bytes run with `iex`, not as a `.ps1` file: the execution policy does not block them,
  and nothing can swap the file between the check and the run.
- Discovery and Uninstall lines have no `$sha`. With `serve.py --accept-token`, the Install
  line starts with `$SendToken=$true; `. The Uninstall line keeps config, token and logs; to
  remove them too, put `$Purge=$true; ` at its start.
- The scripts are ASCII-only (they are decoded as UTF-8).
- Edit a script, or rebuild the zip, after `serve.py` started, and the line refuses: restart
  `serve.py` and paste the line it prints then.

## Variables the VM scripts read
The scripts run with `iex` in the PowerShell window's scope, so they take input from
variables set earlier on the same pasted line.

| Variable | Used by | Meaning |
|---|---|---|
| `$base` | all | server URL. Optional; default is `http://<IPv4 default gateway>:8000` |
| `$sha` | install, update | expected SHA-256 of `EdiabasAgent.zip`, hex, any case. Required |
| `$Elevated` | install | `$true` to run the agent with highest privileges. Optional, default off |
| `$SendToken` | install | `$true` to send the existing token to the Mac. Optional, default off; a first install always sends it. `serve.py --accept-token` puts it on the Install line |
| `$Purge` | uninstall | `$true` to remove `config.json`, `token.txt` and `logs\` as well. Optional, default off: uninstall keeps them for a later reinstall |
| `$KeepData` | uninstall | ignored: keeping the data is now the default. The script says so if it is set |

Install, update and uninstall must run in an elevated **Windows PowerShell** (5.1) window, not PowerShell 7.

Install and Update download, check and expand the zip in a new folder
`C:\ProgramData\EdiabasBridge-staging-<guid>` that is created with an ACL granting only
SYSTEM and Administrators (inheritance off, set at creation), never in the user's `%TEMP%`.
They hash `EdiabasAgent.exe` there, copy it into place and hash the copy. If the two differ,
Install deletes the copy; Update puts back the previous exe (below). The staging folder is
removed at the end. Install, Update and Uninstall first
remove any `EdiabasBridge-staging-*` folder left behind by an earlier run that was cut short
(a link of that name is left alone).

Update, in order: disable the scheduled task (its 5-minute trigger cannot start the exe
mid-copy) and stop the agent; copy the current exe to `EdiabasAgent.exe.bak` (hash-checked);
copy the new exe into place; add to `config.json`'s `conflictProcesses` any of the seven
defaults (`INPALOAD`, `TOOL32`, `NCSEXPER`, `WINKFPT`, `BMW Coding Tool`, `WINKFP`, `ISTAGUI`)
it lacks, compared without case, after the owner's own entries, and print the names added
(the other keys, `reviewedJobs` included, are kept; a file that does not parse is left alone
with a warning; the file is written as UTF-8 without BOM, re-indented by `ConvertTo-Json`, as
Install writes it); make the firewall rule again from the current default gateway (the same
function as Install); enable and start the task; ask `/health` from inside the VM for up to
60 s. If the new exe cannot be copied, or the agent does not come up (an answer of 200, or
403 because the request came from the VM itself, counts as up), Update puts
`EdiabasAgent.exe.bak` back (hash-checked against the backup's own hash), starts it and prints
`Rolled back to <old version>`. The task is enabled again on every way out. A second Update
with the same zip is a no-op update and must succeed.

## The release artefact
`vm-agent/build.sh` writes:
- `release/EdiabasAgent.zip` — `EdiabasAgent.exe`, `version.txt` and `commit.txt` at the zip
  root. `commit.txt` holds the short git commit hash, ending in `-dirty` when built with
  `--allow-dirty` from a tree with uncommitted changes;
- `release/EdiabasAgent.zip.sha256` — the hex digest, one line.

## What lives in the VM
| Path | Purpose | Agent's access |
|---|---|---|
| `C:\ProgramData\EdiabasBridge\EdiabasAgent.exe` | the agent | read, execute |
| `C:\ProgramData\EdiabasBridge\EdiabasAgent.exe.bak` | the exe before the last update (Update's rollback copy) | not used |
| `C:\ProgramData\EdiabasBridge\config.json` | configuration (the folder grants users read access only; with UAC off, any program the owner runs in the VM can still edit it, so `vm_health` lists the approved jobs and every `ediabas_*` result is flagged `[approvals changed]` when they change) | read |
| `C:\ProgramData\EdiabasBridge\token.txt` | bearer token | read |
| `C:\ProgramData\EdiabasBridge\logs\agent.log` | agent's own log | write |
| `C:\ProgramData\EdiabasBridge\logs\audit.log` | one line per request | write |

## Token hand-off
`install.ps1` sends the agent's token to `PUT $base/token` only on a first install (no
`token.txt` yet) or with `$SendToken=$true`. With `serve.py --accept-token` running, the token
lands in the Mac's Keychain and nobody copies it by hand; `serve.py` then puts
`$SendToken=$true` on the Install line. The token is never printed. If sending fails (without
the flag the server answers 403), the script says: "Could not send the token to the Mac (HTTP
<status>). On the Mac, start: python3 dev/serve.py --accept-token, then paste the Install line
it prints again." To re-send a token, do exactly that.

- Scheduled task name: `ediabas-bridge agent`. Triggers: at log-on, and every 5 minutes (a
  second copy exits at once, so this only matters after a crash).
- Firewall rule display name: `ediabas-bridge agent`.
