# ediabas-bridge errors

Every error the Mac MCP server can return today, from `vm_health` or `vm_list_windows`. A
failed call is a tool error whose text is the message shown below. The code is what appears in
the Mac audit log (`~/Library/Logs/ediabas-bridge/audit.log`).

General rule for all of them: tell Ryan what to do, do not retry in a loop, and call `vm_health`
once more after he says it is fixed. Never fill in results that were not returned.

## Contents
- Reaching the VM: `vm_not_running`, `agent_not_answering`, `wrong_service`, `config_error`
- Authentication: `no_token`, `unauthorized`, `forbidden`
- Responses: `bad_response`, agent-reported errors, `internal_error`
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

## Responses

### `bad_response`
**Message:** "The agent's response to GET <path> did not match the expected format. The agent
and this server may be different versions."

**Likely cause:** the VM agent and the Mac server were built from different versions.
**Tell Ryan:** update the agent in the VM with the Update line (see Deploy steps).

### Agent-reported errors
**Message:** "The agent reported an error: <code> — <message> (HTTP <status>)."

The code is the agent's own. Today it can be:
- `not_found` (404) — the agent does not have the endpoint the server asked for; usually an
  older agent. Update it (see Deploy steps).
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
