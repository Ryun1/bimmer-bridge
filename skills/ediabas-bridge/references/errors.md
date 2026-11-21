# ediabas-bridge errors

Every error code the bridge's tools can return (the list in the project's `docs/tools.md`). A
failed call is a tool error whose text is one message saying what happened and what to do. The
code is what appears in the Mac audit log (`~/Library/Logs/ediabas-bridge/audit.log`).
Messages quoted below are from the server as built; codes without a quoted message are
described by meaning, and the server's own message is the one to relay.

General rule for all of them: tell Ryan what to do, do not retry in a loop, and call `vm_health`
once more after he says it is fixed. Never fill in results that were not returned. A refusal is
the answer: never look for another tool, job or route (and never computer use on the VM
window) that would get round it.

## Contents
- Reaching the VM: `vm_not_running`, `local_network_blocked`, `agent_not_answering`, `wrong_service`, `config_error`
- Authentication: `no_token`, `unauthorized`, `forbidden`
- Versions: `agent_outdated`, `bad_response`, agent-reported errors, `internal_error`
- Job policy: `policy_unavailable`, `job_not_allowed`, `job_needs_review`
- EDIABAS: `cable_in_use`, `ecu_not_found`, `job_not_found`, `ediabas_error`, `ediabas_unavailable`, `job_timeout`, `not_run`, `bad_request`
- Screenshots: `window_not_found`, `window_minimized`, `desktop_unavailable`
- Logs: `path_not_allowed`, `file_not_found`
- Deploy steps (installer, update, token)

---

## Reaching the VM

### `vm_not_running`
**Message:** "The VM does not appear to be running. Start the 'Windows' VM in UTM and log in."
Sometimes followed by a detail in brackets:
- "(no UTM shared-network bridge interface is up on this Mac)" — UTM has no shared network up,
  which normally means no VM is running.
- "(its MAC address is not in the DHCP leases or ARP table)" — the Mac has not seen the VM on
  the network.
- "(no address for it answered as the agent on the UTM network)" — addresses were found but
  none answered as the agent.

**Likely cause:** the VM is off, suspended, or still booting.
**Tell Ryan:** start the `Windows` VM in UTM and log in to Windows. If the VM is plainly running
and logged in, treat it like `agent_not_answering`.

### `local_network_blocked`
**Message:** "This Mac could not reach the VM's address even though UTM's network is up. macOS
may be blocking it: open System Settings > Privacy & Security > Local Network and allow 'node'
(or answer the 'node would like to find devices on your local network' prompt with Allow),
then ask again. If that is already allowed, the VM may still be starting: log in to Windows
and try again."

**Likely cause:** macOS Local Network privacy. The Claude desktop app starts the server in a
way that makes macOS treat `node` as its own app, so the first call shows a permission prompt
and fails until Ryan answers it. The permission is tied to the `node` binary's path, so a
Homebrew upgrade of node can bring the prompt back.
**Tell Ryan:** allow `node` under System Settings > Privacy & Security > Local Network, then ask
again. Do not say the VM is down. Only if that is already allowed: check the VM has finished
starting and he is logged in to Windows.

### `agent_not_answering`
**Message:** "The VM is up but the agent is not answering. Log in to Windows (the agent starts
at log-on), or re-run the installer."

**Likely cause:** nobody is logged in to Windows (the agent is started by a scheduled task at
log-on), the agent has crashed or was never installed, or the Windows firewall is blocking it.
**Tell Ryan:** log in to Windows first. If he is logged in, re-run the installer (see Deploy
steps below).

### `wrong_service`
**Message:** "Something answered at <address>, but it is not the ediabas-bridge agent. Check
EDIABAS_AGENT_URL."

**Likely cause:** `EDIABAS_AGENT_URL` is set in the MCP server's environment and points at the
wrong place, or another service is on that address and port.
**Tell Ryan:** check or unset `EDIABAS_AGENT_URL` in the MCP server's registration, then restart
the server (restart Claude Code or the desktop app). Without it, the server finds the VM itself.

### `config_error`
**Message:** "EDIABAS_AGENT_URL is not a valid http(s) URL. Fix or unset it in the MCP server's
environment."

**Likely cause:** a typo in `EDIABAS_AGENT_URL`.
**Tell Ryan:** fix or remove it in the MCP server's registration and restart the server.

---

## Authentication

### `no_token`
**Message:** "No agent token found. Store the VM's token in the Keychain with: security
add-generic-password -s ediabas-bridge -a agent -w '<token>' -U (the token is in the VM
installer's output), or set EDIABAS_AGENT_TOKEN in the server's environment."

**Likely cause:** the token was never stored on this Mac.
**Tell Ryan:** the easy fix is to re-run the installer with `serve.py --accept-token` running
(see Deploy steps); it sends the token to the Keychain itself. Otherwise he runs that
`security` command on the Mac with the token the installer printed in the VM. Never ask him
to paste the token into the chat, and never write it into a file.

### `unauthorized`
**Message:** "The agent rejected the token. The Keychain token does not match the VM's." followed
by the same `security add-generic-password ...` fix as `no_token`.

**Likely cause:** the agent was reinstalled with a new token, or the wrong token was stored. The
server already re-reads the Keychain once before reporting this.
**Tell Ryan:** re-send the VM's current token: start `serve.py --accept-token` on the Mac and run
the Install line in the VM again (see Deploy steps). It replaces the Keychain entry. No
restart is needed; call `vm_health` again afterwards.

### `forbidden`
**Message:** "The agent refused this Mac's address (firewall or gateway check)."

**Likely cause:** the agent only accepts requests from the VM's default gateway, which is the
Mac. A changed UTM network, or a request coming from somewhere other than the Mac, fails this.
**Tell Ryan:** the agent's firewall or gateway check is rejecting the Mac. Re-running the
installer re-creates the firewall rule for the current gateway (see Deploy steps). Do not
suggest loosening the firewall.

---

## Versions

### `agent_outdated`
**Meaning:** the agent in the VM has no endpoint for the tool that was called. The Mac server
was updated with a tool the installed agent does not have yet.
**Tell Ryan:** that tool is not installed in the VM yet; the agent needs the Update line (see
Deploy steps), and only if a newer agent build exists. Then stop: do not try to get the same
result with another tool.

### Policy version warning in `vm_health`
Not an error code. `vm_health` shows the agent's policy version and warns when it differs from
the Mac's. The two sides may then disagree about which jobs are allowed (each still refuses
what its own copy refuses). Tell Ryan the agent needs the Update line with a build made from
the current policy.

### `bad_response`
**Message:** "The agent's response to GET <path> did not match the expected format. The agent
and this server may be different versions."

**Likely cause:** the VM agent and the Mac server were built from different versions.
**Tell Ryan:** update the agent in the VM with the Update line (see Deploy steps).

### Agent-reported errors
**Message:** "The agent reported an error: <code> — <message> (HTTP <status>)."

The code is the agent's own. Codes with their own section in this file are listed there;
the generic ones are:
- `not_found` (404) — the agent does not have the endpoint the server asked for; usually an
  older agent. For tools the server knows this is reported as `agent_outdated`. Update the
  agent (see Deploy steps).
- `method_not_allowed` (405) — a version mismatch between the server and the agent. Update the
  agent.
- `internal_error` (500) — the agent failed; details are in its log in the VM at
  `C:\ProgramData\EdiabasBridge\logs\agent.log`. Tell Ryan, and call `vm_health` once more. If
  it persists, he can look at that log.
- `http_<status>` — the agent returned an error with no code. Report it as is.

### `internal_error` (Mac server)
**Message:** "Internal error in the ediabas-bridge MCP server: <detail>"

**Likely cause:** a bug in the Mac MCP server. The detail is written to its stderr log.
**Tell Ryan:** report the message as is. Do not retry repeatedly.

---

## Job policy

The policy (`policy/policy.json` in the repo) is default-deny: a job runs only if its name is
on the allowed list or matches an allowed read pattern, and no part of it looks like a write.
The Mac checks it before anything is sent; the agent checks it again.

### `policy_unavailable`
**Meaning:** the Mac server could not load the policy file, so every `ediabas_*` tool refuses
and nothing is sent to the VM. This is the safe failure.
**Tell Ryan:** the policy file is missing or invalid in the repo on the Mac; the message says
which. It needs fixing in the repo, not working round.

### `job_not_allowed`
**Meaning:** the policy refused the ECU or job by name; the message names the rule (for
example a deny word such as `LOESCH`, or `default_deny` for a job not on the list).
**Cause:** the job may write, or simply is not on the allowed list.
**Tell Ryan:** which job was refused and by which rule. Do not try a similar job name, a
different ECU, Tool32, INPA or anything else to get the same effect. If Ryan believes a job is
a pure read that should be allowed, that is a change to the policy files in the repo, made
deliberately and with its test cases, not something to do mid-session.

### `job_needs_review`
**Meaning:** the job's name looks like a read (`STATUS_...`), but its own description in the
SGBD contains a word that suggests it may do something (start, activate, write, clear, learn
...) or it has no description. The message quotes the description and the words matched.
**Tell Ryan:** show him the description as quoted, say in plain English what it seems to say,
and that the bridge will not run it unless he decides it is a pure read. That decision is his
alone. The safe default is to skip it; never present approving it as a routine step.

---

## EDIABAS

### `cable_in_use`
**Meaning:** a BMW tool (INPA, Tool32, NCS Expert, WinKFP, BMW Coding Tool) or another program
using EDIABAS is running in the VM; the message names it. Only one program can use the cable,
and there is no override.
**Tell Ryan:** name the tool and ask him to close it when he has finished with it; then wait
for him to say so. Do not retry in a loop. If it is WinKFP or NCS Expert, say it must not be
closed or killed mid-flash or mid-coding: an interrupted flash can brick an ECU.

### `ecu_not_found`
**Meaning:** there is no SGBD or group file of that name in the VM's EDIABAS ECU folder.
**What to do:** search with `ediabas_list_ecus` (a `filter` substring) and pick from what it
lists. Do not try variations of the name blind.

### `job_not_found`
**Meaning:** the SGBD has no job of that name.
**What to do:** list its jobs with `ediabas_describe`. Never guess job names.

### `ediabas_error`
**Meaning:** EDIABAS ran the request and reported its own error; the message carries
EDIABAS's code and text. Typical reasons are an ECU that does not answer (ignition off, module
not fitted, wrong SGBD for this car) or a cable problem; which EDIABAS codes mean which on this
install has not been confirmed yet.
**Tell Ryan:** the code and text as returned, and for which ECU. Do not interpret the code
beyond what the text says. In `ediabas_read_faults` this appears in that ECU's place and the
other ECUs still print.

### `ediabas_unavailable`
**Meaning:** the agent could not start EDIABAS: the DLL is missing or failed to initialise.
`vm_health` shows whether the DLL was found.
**Tell Ryan:** EDIABAS would not start in the VM; give him the reason from the message. The
bridge never changes EDIABAS's configuration, so the fix is his.

### `job_timeout`
**Meaning:** the job was still running when its time ran out, and the agent stopped it.
**Tell Ryan:** which job timed out. Do not rerun it straight away; each attempt costs
ignition-on time. Check `ediabas_vehicle_status` or ask Ryan whether the ignition is on.

### `not_run`
**Meaning:** (per ECU in `ediabas_read_faults`) the batch ran out of time before this item
started.
**Tell Ryan:** which ECUs were not read. Read them in a later, smaller call only if they are
still needed, keeping the session's ignition time in mind.

### `bad_request`
**Meaning:** an argument was missing or malformed. Usually a mistake in the call; fix the
arguments (check them with `ediabas_describe`) rather than retrying the same call.

---

## Screenshots

### `window_not_found`
No visible window matches the title or handle. Call `vm_list_windows` and use a title from it.

### `window_minimized`
The window is minimised and the agent never restores windows. Ask Ryan to restore it.

### `desktop_unavailable`
The screen is locked, on the secure desktop, or the agent is not in an interactive session.
Ask Ryan to log in or unlock Windows.

---

## Logs

### `path_not_allowed`
The path is outside the folders the agent may read, or tries to escape them. Use
`vm_logs_list` and pick a path from it. Do not try other spellings of the path.

### `file_not_found`
No such file. List again with `vm_logs_list`; trace files are rewritten by the tools.

---

## Deploy steps

Use these only when the fix above calls for them. Ryan runs everything inside the VM; give him
one line at a time and say what to expect.

1. On the Mac, with the VM running, start the deploy server from the repo:
   `python3 dev/serve.py --accept-token`
   (`--accept-token` lets the installer hand the agent's token straight to the Mac's Keychain.)
   It prints the lines to paste into the VM, including **Install** and **Update** lines of the
   form `$base='...'; $sha='...'; irm $base/install.ps1 | iex`. Copy them exactly from its
   output; the hash in the line is what the script checks the download against, so do not
   type or reuse an old one. If it says `release/EdiabasAgent.zip` is not built, the agent has
   to be built first (`vm-agent/build.sh`).
2. In the VM, open **Windows PowerShell** as administrator (not PowerShell 7) and paste the
   **Install** line (first install, to repair, or to re-send the token) or the **Update**
   line (new build of an installed agent).
3. The installer sends the token to the Mac and prints "token stored in the Mac's Keychain".
   If it could not, a first install prints the token and a `security` command for Ryan to run
   on the Mac; a later run prints a note instead, and running the Install line again with
   `serve.py --accept-token` started re-sends it.
4. Stop `serve.py` (Ctrl-C) when finished; it should not be left running.
5. Call `vm_health` to confirm.
