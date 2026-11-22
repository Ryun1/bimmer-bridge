# Architecture

How the pieces fit. The contracts are [api.md](api.md) (agent HTTP), [tools.md](tools.md) (MCP
tools) and [deploy.md](deploy.md) (Mac to VM deployment); this page is the overview.

## Components

| Component | Where it runs | What it is |
|---|---|---|
| MCP server (`mac-mcp/`) | the Mac, started by Claude Code or the desktop app over stdio | TypeScript, Node 20+. Offers the ten tools, finds the VM, holds the policy check on the Mac side, writes the Mac audit log. Built to `mac-mcp/dist/index.js` |
| agent (`vm-agent/`) | the Windows VM in UTM, in Ryan's logged-in session | `EdiabasAgent.exe`, C# on .NET 10, self-contained win-x86 single file (32-bit, because `api32.dll` is). An HTTP server (Kestrel) on port 8765 that calls EDIABAS, lists and captures windows and reads trace files |
| EDIABAS | the VM (`C:\EDIABAS`) | BMW's diagnostic runtime: `api32.dll` loads the SGBD files (`.prg`, `.grp`) from the Ecu folder and talks to the K+DCAN cable |
| job policy (`policy/policy.json`) | both sides | default-deny list of what may run. Embedded in the agent at build time; read by the Mac server at start-up |
| VM scripts (`vm-setup/`) | the VM, pasted into Windows PowerShell | install, update, uninstall, discovery |
| deploy server (`dev/serve.py`) | the Mac, only while deploying | serves the scripts and the agent zip to the VM and takes the token back into the Keychain |
| skills (`skills/`) | Claude | guidance on running a session; they enforce nothing |

The agent runs as a scheduled task (`ediabas-bridge agent`) at Ryan's log-on, so it is in his
interactive session and can see the desktop for screenshots. A second copy exits at once
(a global mutex), so the task's 5-minute repeat only matters after a crash.

## The request path

1. Claude calls a tool, for example `ediabas_read_faults` with `NVE_65`.
2. The MCP server checks the arguments and, for `ediabas_*` tools, the job policy
   (`mac-mcp/src/policy.ts`). A refusal ends here and never reaches the VM.
3. It finds the agent: the VM's address from `/var/db/dhcpd_leases`, then `arp -an`, then
   `utmctl ip-address`, for the VM's MAC (`EDIABAS_VM_MAC`). Only an address on the same subnet
   as a local `bridge*` interface is contacted, and only one whose `GET /health` answers as
   `ediabas-bridge-agent` is used. The URL is cached (`mac-mcp/src/vmAddress.ts`).
4. It sends the HTTP request with the token from the Keychain (`mac-mcp/src/agentClient.ts`).
   Timeouts: 15 s, or 75 s for `/ediabas/*`. A request that runs an EDIABAS job is never sent
   twice; only a refused connection (nothing sent) is retried.
5. In the agent, `vm-agent/EdiabasAgent/Middleware.cs` runs, in order: source address (only
   the VM's default gateway, which is the Mac), token, path, method. Every request, refused or
   not, gets an audit line.
6. An EDIABAS handler (`EdiabasEndpoints.cs`) queues the work on the EDIABAS thread and waits.
7. `vm-agent/EdiabasAgent.Core/EdiabasService.cs` does the work: conflict check; `apiInit`;
   for each job the policy, the Ecu file, the description screen where needed, the conflict
   check again, `apiJob`, polling `apiState` every 50 ms until done or timed out (`apiBreak` on
   timeout), reading the result sets; `apiEnd`.
8. EDIABAS sends the telegrams over the K+DCAN cable (USB, passed through to the VM by UTM)
   to the car's diagnostic socket and the ECU.
9. The agent returns JSON; the MCP server formats it (fault tables, miles first) and writes its
   audit line.

Screenshots (`/screen/capture`), window lists (`/windows`) and log reads (`/logs`,
`/logs/read`) take the same path up to step 5 and do not touch EDIABAS.

## Threads in the agent

- **One EDIABAS thread.** `vm-agent/EdiabasAgent/Ediabas/EdiabasRunner.cs` owns `api32.dll`
  and makes every EDIABAS call. Requests are queued and run one at a time, each from
  `apiInit` to `apiEnd` on that thread. A client that goes away only cancels the request: the
  service stops starting jobs, breaks a running one and still calls `apiEnd`. A request still
  queued when its client has gone is dropped.
- **Idle release.** `apiEnd` alone leaves EDIABAS's `ebas32.exe` running while the DLL is
  loaded, which would keep the interface open. So between requests the thread waits only until
  `ediabasIdleReleaseSeconds` (default 20) have passed since the last EDIABAS request, then
  unloads the DLL. The next request loads it again (about 10 s in this VM). The unload happens
  on the same thread, never during a request.
- **A job that does not stop.** If a job is still busy 2 s after `apiBreak`, the agent starts
  no further job and leaves EDIABAS alone until it restarts: no `apiEnd` on that handle, no
  unload, and every later EDIABAS request is refused with `ediabas_unavailable`. `/health`
  reports `ediabas.stuck: true`. Restarting Windows restarts the agent.
- **Everything else** (`/health`, `/windows`, capture, logs) runs on Kestrel's request threads.
  `/health` never loads the DLL; it reports `ediabas.loaded` and the conflicting processes.
- **Network changes.** `vm-agent/EdiabasAgent/AgentHost.cs` binds one IPv4 address and port,
  rebinds when the network changes, and waits and retries if no suitable address exists. It
  never falls back to another address.

## Where the policy is enforced

| Step | Mac (`mac-mcp/src/policy.ts`) | Agent (`vm-agent/EdiabasAgent.Core/JobPolicy.cs`, `CommentScreen.cs`) |
|---|---|---|
| Loading | at start-up from `policy/policy.json` two folders above `dist/`; invalid file: every `ediabas_*` tool refuses with `policy_unavailable` | embedded at build time; invalid: every EDIABAS job refused |
| Name rules | `ediabas_job`, every ECU of `ediabas_read_faults` (the whole call is refused if one is), shape only for `ediabas_describe` | every job, including the agent's own `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, `_RESULTS` and the `/ediabas/vehicle` jobs |
| Description screen | no | yes, for jobs allowed only by the `STATUS_` pattern; `reviewedJobs` in the VM's config skips it |
| Conflict check | no | before the request opens EDIABAS and before every job |

Both sides run the same test cases (`policy/policy-cases.json`, `policy/policy-parse-cases.json`),
so they cannot drift apart. `vm_health` warns when the agent's policy version differs from the
Mac's. Plain-words rules: [safety.md](safety.md#the-job-policy-in-plain-words).

## Logs and audit

| Side | File | Written by | Holds |
|---|---|---|---|
| VM | `C:\ProgramData\EdiabasBridge\logs\agent.log` | `vm-agent/EdiabasAgent/Logging/AgentLog.cs` | start-up, bind decisions, config problems, errors with details |
| VM | `C:\ProgramData\EdiabasBridge\logs\audit.log` | `vm-agent/EdiabasAgent/Audit/AuditLog.cs` | one tab-separated line per request: time, remote address, method, path, status, error code, duration, ECU, job, rule (refusals only), target (log path or window asked for). Query strings are reduced to their keys; control characters are replaced. Batches add one line per item |
| VM | `C:\ProgramData\EdiabasBridge\logs\selftest.txt` | `--selftest` | the last self-test report |
| Mac | `~/Library/Logs/ediabas-bridge/audit.log` (`EDIABAS_AUDIT_LOG`) | `mac-mcp/src/audit.ts` | one tab-separated line per tool call: time, tool, outcome (`ok` or the error code), duration; `ecu=`, `job=` and, for a policy refusal, `rule=` |
| Mac | stderr (in the desktop app: `~/Library/Logs/Claude/mcp-server-ediabas-bridge.log`) | the MCP server | discovery, failed probes, warnings |
| Mac | a folder of your choice (`EDIABAS_RECORD_DIR`) | `mac-mcp/src/recorder.ts` | off by default; every agent exchange, for fixtures ([development.md](development.md#recording-and-scrubbing)) |

The two VM logs roll over to a `.1` file at 5 MB. The token is never logged on either side, and
the recorder never records request headers.

## Trust boundaries

- **The token.** 32 random bytes, base64url (43 characters), generated by `install.ps1` in
  `C:\ProgramData\EdiabasBridge\token.txt` (access for SYSTEM, Administrators and read for
  the user the agent runs as; the self-test checks that Users, Everyone and Authenticated Users
  get nothing). It reaches the Mac's Keychain (service `ediabas-bridge`, account
  `agent`) through `serve.py --accept-token`, without anyone copying it. The agent keeps only
  its SHA-256 and compares in constant time. With `EDIABAS_AGENT_URL` set, the Mac server uses
  `EDIABAS_AGENT_TOKEN` only and never reads the Keychain, so the real token never goes to an
  address set by hand.
- **The bind address.** The agent listens on one literal IPv4 address on UTM's shared network
  (`bindAddress: "auto"` picks it; never a wildcard, loopback, link-local or public address),
  HTTP/1 only. The Windows firewall rule `ediabas-bridge agent` allows TCP 8765 from the VM's
  gateway only, and the agent itself answers 403 to any other source before looking at the
  token.
- **The Mac side.** The MCP server contacts only on-link addresses on a `bridge*` interface and
  checks `/health` before using one. `serve.py` binds the Mac's bridge address (never
  `0.0.0.0`), serves only the VM's address, and each pasted line runs a script only if its
  SHA-256 matches the one printed on the Mac ([deploy.md](deploy.md)).
- **The log roots.** `/logs` and `/logs/read` read only under `logRoots` in the VM's config
  (by default `C:\EDIABAS\TRACE` and `C:\NCSEXPER\WORK`), on local fixed disks. The path is
  checked before anything is opened and again on the opened handle; `..`, UNC and device
  paths, alternate data streams, device names, links out of the roots, 8.3 short names that
  resolve elsewhere and files with more than one hard link are refused ([api.md](api.md#logs)).
- **Elevation.** UAC is off in this VM, so the agent runs elevated whatever the task's run
  level says, and folder permissions do not constrain it ([phase-results.md](phase-results.md)).
  The limits above are in the agent's code, not in Windows permissions.
