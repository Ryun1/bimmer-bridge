# Install, update and uninstall

Setting the bridge up from scratch, keeping it up to date, removing it, and what to do when
something goes wrong. How files and the token move between the Mac and the VM (the server, the
pasted lines, the variables the scripts read) is the contract in [deploy.md](deploy.md).

## Setup from scratch

What you start with:
- a Mac with this repo and [UTM](https://mac.getutm.app/) (tested on Apple Silicon; the paths
  below are Homebrew's Apple Silicon ones);
- a Windows VM in UTM with network mode **Shared Network** (tested: Windows 11 on ARM, where the
  32-bit BMW tools run under x86 emulation), and a Windows user who is an administrator;
- BMW Standard Tools (EDIABAS, with the SGBD files for your car, and usually INPA, Tool32 and
  NCS Expert) already installed in the VM and working with your car;
- a K+DCAN USB cable passed through to the VM with UTM's USB menu (tested), or another
  interface EDIABAS is set up for (untested).

Do the steps in order. Every command on the Mac is run from the repo root unless it says
otherwise.

**Pointing the bridge at your VM.** By default the bridge looks for the UTM VM named `Windows`
and reads its MAC address from UTM's own config for that VM
(`~/Library/Containers/com.utmapp.UTM/Data/Documents/Windows.utm/config.plist`). If your VM is
called `Windows`, there is nothing to set. Otherwise the easy way is a `config.local.json` at
the repo root (git-ignored), read by both the MCP server and `dev/serve.py`:

```sh
cp config.local.example.json config.local.json   # then edit it
```

```json
{ "vmName": "My BMW VM" }
```

`vmName` is the VM's name as UTM shows it; `vmMac` (optional) is its MAC address (UTM > the VM >
Network), for when the MAC cannot be read from UTM's config. The environment variables
`EDIABAS_VM_NAME` and `EDIABAS_VM_MAC` override the file, for example `export` before
`serve.py`, or `-e` on `claude mcp add`. The order: environment variable, then
`config.local.json`, then the MAC read from UTM's config for the named VM, with `Windows` as the
default name. Other settings: [mac-mcp/README.md](../mac-mcp/README.md).

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

**From a GitHub release** (no .NET SDK needed): clone the tag, since a "Source code" download
has no `.git` and `release.sh` stops without one, then use the release's prebuilt agent zip and
build only the Mac server:

```sh
git clone --branch v0.5.0 https://github.com/Ryun1/ediabas-bridge && cd ediabas-bridge
mkdir -p release   # put EdiabasAgent.zip and EdiabasAgent.zip.sha256 from the release here
shasum -a 256 release/EdiabasAgent.zip; cat release/EdiabasAgent.zip.sha256   # must match
(cd mac-mcp && npm ci && npm run build)
```

Then continue at step 3. **From source:**

```sh
./release.sh
```

It checks that the versions agree, runs every test suite, builds `release/EdiabasAgent.zip`
and `mac-mcp/dist/index.js`, and ends with `Release <version> built and tested.` and the zip's
SHA-256. It stops at the first failure with `release.sh: FAILED: <reason>`. Details in
[Releasing](development.md#releasing). `./release.sh --dry-run` shows the steps without running them.

### 3. Install the agent in the VM

1. Start the VM in UTM and log in to Windows. The agent runs in your logged-in
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
     $SendToken=$true; $base='http://<Mac bridge address>:8000'; $sha='<zip sha256>'; $ebFile="$env:TEMP\ediabas-bridge-install.ps1"; Remove-Item -LiteralPath $ebFile -Force -ErrorAction SilentlyContinue; $ebOk=$false; try { iwr "$base/install.ps1" -UseBasicParsing -OutFile $ebFile -ErrorAction Stop; $ebOk=$true } catch { Write-Host 'download failed: the Mac did not serve install.ps1 (is serve.py running?)' -ForegroundColor Red }; if ($ebOk) { $ebBytes=[IO.File]::ReadAllBytes($ebFile); if (([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ebBytes)) -replace '-','') -eq '<install.ps1 sha256>') { iex ([Text.Encoding]::UTF8.GetString($ebBytes)) } else { Write-Host 'install.ps1 is not the file serve.py checked; it was NOT run. Restart serve.py and paste the line it prints.' -ForegroundColor Red } }
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
is not its own link. Upload the two zips in claude.ai's Skills settings for the desktop app and
cloud sessions. A GitHub release has both zips attached.

Optional: if you keep a car-specific skill of your own (one that interprets fault codes and
keeps a fault log), [skills/bmw-e61-skill-addition.md](../skills/bmw-e61-skill-addition.md) is a short
section to paste into it by hand so that it hands over to this bridge. It was written for the
original owner's `bmw-e61` skill; adapt the names.

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
   `serve.py` printed. It verifies the hash, replaces the exe (keeping the old one as
   `EdiabasAgent.exe.bak`), adds to `config.json`'s `conflictProcesses` any of the seven
   default BMW tools that this `config.json` was never offered and does not list, and records
   all seven in `conflictDefaultsOffered`, so a default you remove from `conflictProcesses`
   stays removed (it prints the names it added; everything else in the file, approvals
   included, is kept, the previous file is kept as `config.json.bak`, and a file it cannot
   read is left alone with a warning), makes the firewall rule again for the current
   gateway, restarts the agent and waits up to 60 s for it to answer `/health` (`Agent is
   up.`, always with the 403 note, as at install). It ends with
   `Updated to ediabas-bridge agent <version>`. If the copy fails, or the new agent does not
   come up, it puts the backup back, starts it and says so (`Rolled back to <old version>`),
   asks `/health` once more for up to 60 s and says plainly when the restored agent does not
   answer either (then the fault is in `config.json` or the network, not the new exe), then a
   red `ERROR:`. The token, ACLs and task settings are not touched. Each step, in
   order: [What Update does](#what-update-does). Running the same Update line a second time is
   a no-op update and must also succeed (it does copy the running exe to
   `EdiabasAgent.exe.bak`, replacing the older backup). If it says the agent is not installed, or the
   scheduled task is missing, use the Install line instead.
4. Stop `serve.py` with Ctrl-C.
5. **Restart Claude** so it starts the new MCP server from the rebuilt `mac-mcp/dist`: quit and
   reopen the desktop app, and start a new Claude Code session. An already-running server keeps
   the old code.
6. Skills: Claude Code sees changes at once through the links. For claude.ai, run
   `skills/install.sh` again and upload the new zips.
7. Check with `vm_health`: the Mac server's version and the agent's should both be the new
   one, with no version or policy-version warning.

## Upgrading to 0.5.0

For the owner, from 0.4.0. Do the steps in order. Anything touching the cable is done with the
cable in the VM and **not in the car** (confirm this first).

**A. Before updating (0.4.0 still installed).**
1. Run `vm_health`. Expected: the approvals show as unpinned (5 in the original VM).
2. Run `ediabas_describe` for each approved job. Each prints its pinned `ECU:JOB@hash` line.
3. In the VM, replace each entry in `C:\ProgramData\EdiabasBridge\config.json` `reviewedJobs`
   with that printed line. Unpinned entries approve nothing from 0.5.0.
4. Run `ediabas_list_ecus` (0.4.0 re-reads the file only at an EDIABAS request), then
   `vm_health`. Expected on 0.4.0: `N pinned, 0 unpinned`.

**B. `voltageSources`.** If the VM's `config.json` has a non-empty `voltageSources`, move it to
the Mac's `config.local.json` and delete the key from the VM file (0.5.0 warns while it is there).

**C. Update.** Follow [Updating](#updating) (`./release.sh`, `python3 dev/serve.py`, paste the
Update line in an administrator PowerShell, restart Claude). Then `vm_health`. Expected: Mac
server and agent both 0.5.0, policy version 4 on both sides, the policy hashes match, the
approvals listed, 0 ignored.

**D. Self-test.** In the VM run `EdiabasAgent.exe --selftest` (in `C:\ProgramData\EdiabasBridge`
or wherever the exe is installed). Expected: PASS, including "link to a device is refused".

**E. Update checks.**
1. Right after C, compare `config.json` with `config.json.bak` in `C:\ProgramData\EdiabasBridge`:
   `reviewedJobs` and the other keys are intact and the only differences are the
   `conflictProcesses` defaults that C printed as added (`WINKFP`, `ISTAGUI` on a 0.4.0 file
   that lists the five 0.4.0 defaults) and the new `conflictDefaultsOffered` key listing all
   seven defaults (C printed `Recorded in conflictDefaultsOffered in config.json: ...`).
   If C printed `config.json has been offered every default conflict process before`, C did
   not write `config.json.bak` and there is nothing to compare.
2. Run the Update line a second time. Expected: `Updated to ediabas-bridge agent 0.5.0` and
   `config.json has been offered every default conflict process before
   (conflictDefaultsOffered); not changed.` It is a no-op for the agent, but
   it copies the running 0.5.0 exe to `EdiabasAgent.exe.bak`, replacing the 0.4.0 backup, and it
   leaves `config.json` and `config.json.bak` as they are.
3. Optional: a forced rollback. Update reads and writes only the live
   `C:\ProgramData\EdiabasBridge\config.json`, so this check changes that file. Copy it first
   (for example to `config.json.keep`), then set its `bindAddress` to an address the VM does not
   have, such as `"192.0.2.1"`, and run the Update line. Expected: `The new agent did not come up
   within 60 s ... Rolling back.`, then `Rolled back to 0.5.0`: the version of the exe that was
   in place when this run started, which this run's backup step copied to `EdiabasAgent.exe.bak`.
   `config.json` is restored only when that same run rewrote it (not the case
   here, so the line does not mention it and the wrong `bindAddress` stays). The restored exe
   reads the same `config.json`, so it does not come up either, and Update says so:
   `The restored agent (0.5.0) does not answer at ... either`, then a red `ERROR:`. To undo:
   copy `config.json.keep` back over `config.json` and run the Update line again. Expected:
   `Updated to ediabas-bridge agent 0.5.0`, and `vm_health` from the Mac works.

**F. Desk checks** (cable not in the car, confirmed).
1. `TMODE INFO` runs.
2. `ediabas_vehicle_status` returns the TMODE lines.
3. Open INPA while a `TMODE _JOBS` describe runs. Expected: `cable_in_use` or `not_run`.
4. Edit `reviewedJobs` in the VM. Expected: the next `vm_health` or `ediabas_*` result (whichever
   comes first) starts `[approvals changed]`, and `vm_health` lists the entries as the file has
   them now.
5. `vm_screenshot` of INPA while it loads a script. Expected: a readable image.
6. `ediabas_describe` on `E60` shows `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` as allowed.
7. Re-record the skill references' job verdicts under policy version 4
   (`dev/record-server.ts`, see [docs/development.md](development.md)).

**G. Release.** Then publish the GitHub release with the files `release.sh` printed and merge
`release-0.5.0`. Record what passed in the README Status.

---

## Uninstalling

Start `python3 dev/serve.py` on the Mac first, then in the VM, in **Windows PowerShell run as
administrator**, paste the **Uninstall** line `serve.py` printed (it checks `uninstall.ps1`'s
SHA-256 before running it).

It stops and removes the scheduled task, the firewall rule and the program files in
`C:\ProgramData\EdiabasBridge`, and keeps `config.json`, `token.txt` and `logs\` for a later
reinstall. To remove those too, put `$Purge=$true; ` at the start of that line. (`$KeepData` is
no longer needed and is ignored.)

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
without `$Purge` and mean to reinstall: the kept `token.txt` still matches it.

---

## The scripts in the VM

PowerShell scripts in `vm-setup/`, run in the VM by pasting one line that `dev/serve.py` prints
on the Mac. Each line downloads its script, checks its SHA-256 against the value printed on the
Mac, and only then runs it ([deploy.md](deploy.md#the-pasted-lines)). Install, Update and
Uninstall must run in an elevated **Windows PowerShell** (5.1) window, not PowerShell 7.

| Script | Does | Window |
|---|---|---|
| `install.ps1` | installs or upgrades the agent in `C:\ProgramData\EdiabasBridge`: exe, `config.json` and `token.txt` (both kept if present), firewall rule and scheduled task `ediabas-bridge agent`, sleep on AC off; sends the token to the Mac on a first install or with `$SendToken=$true` (never prints it) | Windows PowerShell 5.1, as administrator |
| `update.ps1` | replaces the exe (previous one kept as `EdiabasAgent.exe.bak`, put back if the copy fails or the new agent does not answer `/health` within 60 s), adds to `config.json` the default conflict processes it was never offered (recorded in `conflictDefaultsOffered`, so one you removed stays removed), makes the firewall rule again for the current gateway, and restarts the scheduled task (disabled during the copy) | Windows PowerShell 5.1, as administrator |
| `uninstall.ps1` | removes the task, the firewall rule and the program files; keeps `config.json`, `token.txt` and `logs\` by default (`$Purge=$true; ` at the start of the line removes them too; `$KeepData` is ignored) | Windows PowerShell 5.1, as administrator |
| `discover.ps1` | read-only report of the VM (used once, for Phase 0); uploads it into `discovery/` on the Mac | Windows PowerShell 5.1 |

Running the Install line over an existing install keeps `config.json` as it is, so it does not
add new default conflict processes to it; the Update line does. A `config.json` that Install
writes lists every default in both `conflictProcesses` and `conflictDefaultsOffered`.

Install and Update download, check and expand the zip in a new folder
`C:\ProgramData\EdiabasBridge-staging-<guid>` that is created with an ACL granting only
SYSTEM and Administrators (inheritance off, set at creation), never in the user's `%TEMP%`.
They hash `EdiabasAgent.exe` there, copy it into place and hash the copy. If the two differ,
Install deletes the copy; Update puts back the previous exe (below). The staging folder is
removed at the end. Install, Update and Uninstall first
remove any `EdiabasBridge-staging-*` folder left behind by an earlier run that was cut short
(a link of that name is left alone).

### What Update does

Update, in order: disable the scheduled task (its 5-minute trigger cannot start the exe
mid-copy) and stop the agent; copy the current exe to `EdiabasAgent.exe.bak` (hash-checked);
copy the new exe into place; add to `config.json`'s `conflictProcesses` each of the seven
defaults (`INPALOAD`, `TOOL32`, `NCSEXPER`, `WINKFPT`, `BMW Coding Tool`, `WINKFP`, `ISTAGUI`)
that is neither on its `conflictDefaultsOffered` list nor already on `conflictProcesses`,
compared without case, after the owner's own entries, print the names added, and add every
default to `conflictDefaultsOffered` (keeping any other names on it), so no default is offered
twice and one the owner removes from `conflictProcesses` stays removed. `conflictDefaultsOffered`
is written only by Install and Update; the agent does not read it. A file without the key
(written before 0.5.0) counts as offered the five 0.4.0 defaults (`INPALOAD`, `TOOL32`,
`NCSEXPER`, `WINKFPT`, `BMW Coding Tool`), so a 0.4.0 file gains `WINKFP` and `ISTAGUI` once; a
file without a `conflictProcesses` array gets every default it lacks. The other keys,
`reviewedJobs` included, are kept; a file that does not parse, or whose
`conflictDefaultsOffered` is not an array, is left alone with a warning; the new text must
parse back with `conflictProcesses` and `conflictDefaultsOffered` arrays equal to the merged
lists, every original top-level key present and no other new key, and as many `reviewedJobs`
entries, or `config.json` is left untouched; it is written as UTF-8 without BOM, re-indented by
`ConvertTo-Json`, as Install writes it, and the previous file is kept as `config.json.bak`; an
unexpected error in this step is a warning, `config.json` is left untouched and Update carries
on);
make the firewall rule again from the current default gateway (the same
function as Install; if that fails, the error says the new exe is in place and the rule is
missing, and to run the Install line again); enable and start the task; ask `/health` from inside the VM for up to
60 s. If the new exe cannot be copied, or the agent does not come up (an answer of 200, or
403 with the agent's `forbidden` error because the request came from the VM itself, counts as
up), Update puts `EdiabasAgent.exe.bak` back (hash-checked against the backup's own hash), and
`config.json.bak` too if it rewrote `config.json`, starts it and prints
`Rolled back to <old version>` (naming `config.json` when it was restored). It then asks
`/health` once more for up to 60 s; when the restored agent does not answer either, it says so
(the cause is then `config.json`'s `bindAddress` or `port`, or the network, not the new exe),
and the red `ERROR:` says the same. The task is enabled
again on every way out; if it was disabled before the update, Update warns that it has been
enabled and started. A second Update
with the same zip is a no-op update and must succeed; its backup step replaces
`EdiabasAgent.exe.bak` with the exe already running.

Rules for editing the scripts: they must stay ASCII-only and Windows PowerShell 5.1 compatible,
because they run with `iex`; and `serve.py` must be restarted after any change, since the
hashes it prints are those of the files when it started.

## What lives in the VM

| Path | Purpose | Agent's access |
|---|---|---|
| `C:\ProgramData\EdiabasBridge\EdiabasAgent.exe` | the agent | read, execute |
| `C:\ProgramData\EdiabasBridge\EdiabasAgent.exe.bak` | the exe before the last update (Update's rollback copy) | not used |
| `C:\ProgramData\EdiabasBridge\config.json.bak` | `config.json` before Update last changed its `conflictProcesses` or `conflictDefaultsOffered` | not used |
| `C:\ProgramData\EdiabasBridge\config.json` | configuration (the folder grants users read access only; with UAC off, any program the owner runs in the VM can still edit it, so `vm_health` lists the approved jobs and every `ediabas_*` result is flagged `[approvals changed]` when they change) | read |
| `C:\ProgramData\EdiabasBridge\token.txt` | bearer token | read |
| `C:\ProgramData\EdiabasBridge\logs\agent.log` | agent's own log | write |
| `C:\ProgramData\EdiabasBridge\logs\audit.log` | one line per request | write |

- Scheduled task name: `ediabas-bridge agent`. Triggers: at log-on, and every 5 minutes (a
  second copy exits at once, so this only matters after a crash).
- Firewall rule display name: `ediabas-bridge agent`.

---

## Troubleshooting

Tool errors carry a code (in **bold** below) and a message that already says what to do. The
codes are listed in [tools.md](tools.md#errors).

| Symptom | Cause | Fix |
|---|---|---|
| **vm_not_running**: "The VM does not appear to be running" | The VM is off, UTM's shared network is not up on the Mac, or the bridge is looking for a different VM: the VM is not named `Windows` and `vmName` / `EDIABAS_VM_NAME` is not set, or `vmMac` / `EDIABAS_VM_MAC` is not this VM's MAC, or the VM has no DHCP lease yet | Start the VM in UTM and log in. If it is running, set `vmName` (or `vmMac`) in `config.local.json` ([Setup from scratch](#setup-from-scratch)) and restart Claude |
| **agent_not_answering**: "The VM is up but the agent is not answering" | Nobody is logged in to Windows (the agent starts at log-on), or the agent is not installed or has stopped | Log in to Windows and wait about 30 s. If it persists, read `agent.log` in the VM (see logs below) and re-run the Install line |
| **local_network_blocked**, or macOS asked "node would like to find devices on your local network" | macOS Local Network permission for `node`. The desktop app starts the server so that macOS treats `node` as its own app | System Settings > Privacy & Security > Local Network: allow `node`, then ask again. The permission follows the node binary's real path, so after a Homebrew upgrade of node it can ask again |
| **wrong_service**: "answered as the ediabas-bridge agent but could not prove it holds this Mac's token", or **unauthorized**: "The agent rejected the token" | The Keychain token does not match the VM's `token.txt` (for example after an uninstall with `$Purge=$true` and a new install). The Mac checks the agent's `/hello` proof before it sends the token, so a mismatch usually shows as the first; the token was not sent | On the Mac `python3 dev/serve.py --accept-token`, then paste the Install line again in the VM. It keeps the VM's token and sends it to the Keychain. Nothing else needs a restart: the server re-reads the token after a failed proof (and uses it at once if the proof then matches) or a 401 (the next call proves the address with it first) |
| **no_token** | Nothing in the Keychain (the first install's hand-off failed) | As for `unauthorized` |
| **forbidden** | The agent or its firewall refused the Mac's address: requests are accepted only from the VM's default gateway | Re-run the Install or the Update line; both recreate the firewall rule for the VM's current gateway. `agent.log` in the VM shows the address and gateway the agent chose |
| **agent_outdated**, or `vm_health` says "Agent features: not reported" | The agent in the VM is an older build that lacks that endpoint | [Updating](#updating): run the Update line |
| `vm_health` warns that the agent's job policy differs from the Mac's | The agent and the Mac server were built from different versions of `policy/policy.json` | Run `./release.sh`, the Update line, and restart Claude |
| **bad_response** | The agent and the MCP server are different versions | As above |
| **bad_response** naming an HTTP status, or **ediabas_request_lost** naming one | The agent's address answered with an HTTP error that is not in the agent's format: usually the agent's web server refusing the request before the agent handled it | Run `vm_health`. A job request is not sent again by itself, as the job may have run; ask again if the result is still needed |
| **policy_unavailable** | `policy/policy.json` could not be loaded by the Mac server; every `ediabas_*` tool refuses | Restore the file from git, then restart Claude |
| **policy_mismatch** | The agent in the VM and the Mac server carry different `policy/policy.json` files (compared by SHA-256), so every `ediabas_*` tool refuses | Run `./release.sh`, the Update line, and restart Claude |
| **cable_in_use**, naming a process | A BMW tool that uses the cable is open in the VM (INPA, Tool32, NCS Expert, WinKFP, BMW Coding Tool, or anything with `api32.dll` loaded). There is no override, by design | Close that tool in the VM, then ask once more. Do not retry in a loop |
| **cable_not_found**: EDIABAS error 28, `IFH-0018: INITIALIZATION ERROR` | The K+DCAN cable is not connected to the VM. Needed even for `ediabas_describe` of a real SGBD; only `TMODE` and `UTILITY` load without it | Plug the cable into the Mac and pass it through to the VM in UTM (the USB button in the VM's toolbar), then ask again |
| **ediabas_error** or **job_timeout** | EDIABAS reached the interface but the ECU did not answer: ignition off, cable not in the car, or that ECU is not fitted | Check `ediabas_vehicle_status`, then decide before trying again |
| **ediabas_error** 16 `IFH-0006: COMMAND NOT ACCEPTED` on every real SGBD, after the cable was unplugged from the Mac and plugged back in | UTM does not hand a replugged cable back to the VM by itself | Tick the cable for the VM in UTM's USB menu, then ask again |
| **ediabas_error** 48 `IFH-0038: INTERFACE COMMAND NOT IMPLEMENTED`, after the Mac slept (`vm_health` still works) | Sleeping hands the cable back to the Mac (it shows as `/dev/cu.usbserial-*` there again) | Tick the cable for the VM in UTM's USB menu, then ask again |
| The first EDIABAS call after a pause takes about 10 s | Expected. The agent unloads EDIABAS after `ediabasIdleReleaseSeconds` (default 20 s) with no EDIABAS request, so that nothing holds the interface open; the next call loads it again | Nothing. The Mac waits up to 75 s for an EDIABAS call, counted from when it is sent. For a session at the car see [at-the-car.md](at-the-car.md) |
| **job_needs_review**, quoting a job's description | A `STATUS_` job whose description suggests it may do more than read, or has none | The owner decides. To allow that one job, `ediabas_describe` on it prints the entry to add to `reviewedJobs` in `C:\ProgramData\EdiabasBridge\config.json` in the VM (`"ECU:JOB@hash"`, pinned to the description as it is now); the agent picks it up at the next EDIABAS call, no restart. With UAC off, any program the owner runs in the VM can edit that file, so `vm_health` lists the approved jobs as the file has them now, and the next `vm_health` or `ediabas_*` result after any change starts with `[approvals changed]`; if you did not make it, remove the entry and find out what edited the file. Steps in [safety.md](safety.md#approving-one-job). Never make this routine |
| **ediabas_unavailable**, or `vm_health` says "EDIABAS: STUCK" | A job did not stop after `apiBreak`; the agent then leaves EDIABAS alone until it restarts (`/health` `ediabas.stuck`). The same code also means `api32.dll` is missing or EDIABAS would not start | Restart Windows in the VM, which restarts the agent. If `vm_health` says "EDIABAS DLL: NOT found", check `ediabasDll` in the VM's `config.json` |
| **not_run** | The agent did not start the job, or stopped it: the request's time budget ran out, a BMW tool was opened during the request, an earlier job did not stop, or the request was abandoned. The message says which | Act on the message (close the tool, describe with a `filter`, ...). Do not retry in a loop |
| **ediabas_request_lost**: the job may have run | The connection broke or timed out after the request may have reached the agent. Such requests are never sent a second time | Call `vm_health`; ask again only if the result is still needed |
| **ediabas_busy** | Another EDIABAS call from this Mac was still running after 120 s; this one was not sent | Wait for the other call's answer, then ask once more |
| `vm_health` warns "The Mac server and the agent are different versions" | Claude is running a Mac server from before an update, or the agent in the VM was not updated | Restart Claude, or run the Update line ([Updating](#updating)) |
| **job_not_allowed** | The job policy refused the job by name | By design. Do not look for another job or tool that does the same; writes are done by hand in INPA or NCS Expert |
| **desktop_unavailable** (screenshots) | Windows is locked, showing a secure desktop (for example a UAC prompt), or nobody is logged in | Log in to or unlock Windows. The installer does not change the screen lock |
| **window_minimized** | The window asked for is minimised; the agent never restores windows | Restore it in the VM, then ask again |
| Script says `This script must run in "Windows PowerShell" (5.1), not PowerShell 7` | The line was pasted into PowerShell 7 | Open **Windows PowerShell** as administrator and paste it again. Nothing was changed |
| Script says `This script must run in an elevated PowerShell` | The window was not run as administrator | Right-click Windows PowerShell, Run as administrator, paste again |
| Script says `SHA-256 mismatch` | The zip was rebuilt after `serve.py` printed its lines, or the line was mistyped | Stop and restart `serve.py` and paste the line it prints now. Nothing was changed |
| The line prints `... is not the file serve.py checked; it was NOT run` | The script in `vm-setup/` changed after `serve.py` started, or something other than `serve.py` answered on the Mac's address | Restart `serve.py` and paste the line it prints now. If it happens again, stop: something else is answering on port 8000. Nothing was run |
| `iwr` cannot connect to the Mac | `serve.py` is not running, or the address in `$base` is wrong | Start `serve.py` and copy its line exactly |
| `iwr` gets `403 Forbidden` (`only the VM's address is served`) | `serve.py` took a different address for the VM (an old DHCP lease) | Restart it with `python3 dev/serve.py --client <VM address>` (`ipconfig` in the VM shows it) |
| Install prints `Could not send the token to the Mac (HTTP 403)` | `serve.py` was started without `--accept-token` | Restart it as `python3 dev/serve.py --accept-token` and paste the Install line it prints again. The token is never printed |
| The line prints `download failed: the Mac did not serve <script> (is serve.py running?)` | `serve.py` is not running, or it is not answering on the address in the line | Start `serve.py` and paste the line it prints now. Nothing was run |
| Install prints `The agent did not answer within 20 s` | The agent could not start or bind | Read `agent.log` in the VM, and run the `--selftest` check from [step 3](#3-install-the-agent-in-the-vm) |
| **agent_not_answering** after `config.json` was edited and the agent restarted | The config is invalid (for example `maxJobTimeoutSeconds` above 60, or `jobTimeoutSeconds` above it). The agent does not exit: it logs the problem and re-reads the config and token every 30 s | Read `agent.log` in the VM, fix `config.json`; the agent starts within 30 s |
| `serve.py` says `no UTM shared-network interface (bridge1xx) ... is up` | The VM is not running, so the Mac has no bridge address to bind | Start the VM, then `serve.py` |
| `serve.py` says `no DHCP lease for the VM's MAC` | The VM has not taken an address yet, or the MAC it looked for is not this VM's (wrong `vmName` / `vmMac`, or the environment variables) | Start the VM and log in, check `config.local.json`, or pass `--client <VM address>` |
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
kept). What each audit line holds: [architecture.md](architecture.md#logs-and-audit).
In Claude Code, `/mcp` shows whether the server is connected.

