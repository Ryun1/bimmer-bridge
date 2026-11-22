# mac-mcp — ediabas-bridge MCP server

Local stdio MCP server for Claude Code and the Claude desktop app. It finds the Windows VM
in UTM, calls the VM agent over authenticated HTTP (`docs/api.md`) and exposes read-only
tools. Everything it does is read-only; it has no way to click, type, clear faults, code or
flash anything.

## Tools

All read-only (`readOnlyHint: true`). The contract is `docs/tools.md`.

| Tool | What it does |
|---|---|
| `vm_health` | this server's version and start time, agent status (warns when the agent's version differs from this server's), BMW tools holding the cable, EDIABAS DLL and whether EDIABAS is loaded now (when the agent reports it), last cable use, agent features, job policy version (warns when the agent's differs from the Mac's), approved jobs (pinned and unpinned) |
| `vm_list_windows` | windows open in the VM |
| `vm_screenshot` | an image of the desktop or one window (`window`, `maxEdge`), then a one-line caption |
| `vm_logs_list` / `vm_logs_read` | log files under the agent's allowed folders; read a tail or a range, optionally grep |
| `ediabas_list_ecus` | SGBD (`prg`) and group (`grp`) names |
| `ediabas_describe` | an SGBD's jobs with whether each is runnable (`namesOnly` skips the descriptions), or one job's description, its hash, arguments and results |
| `ediabas_vehicle_status` | ignition, battery and cable voltage readings, and voltages from `voltageSources` in `config.local.json` when configured; one `/ediabas/jobs` batch, fields as returned |
| `ediabas_job` | one allowed job, result sets as text (or the agent's JSON with `raw: true`); `timeoutSeconds` 1–60 |
| `ediabas_jobs` | 1–20 allowed jobs in one request, one block per job in request order |
| `ediabas_read_faults` | `FS_LESEN` (and `IS_LESEN`) for 1–20 ECUs, one fault table per ECU, distances in miles first |

**Job policy.** `src/policy.ts` loads `policy/policy.json` from the repo root (two levels above
`src/` and `dist/`) once at start-up and evaluates it as `docs/api.md` "Job policy" says.
`ediabas_job`, `ediabas_jobs`, `ediabas_read_faults` and `ediabas_describe` (names only) are checked before
any request leaves the Mac, so a refused job never reaches the VM. If the file is missing or
invalid, every `ediabas_*` tool refuses with `policy_unavailable` (fail closed); restore the
file and restart Claude. `test/policy.test.ts` runs every case in `policy/policy-cases.json`
and `policy/policy-parse-cases.json`, as the agent's tests do.

**Older agent.** A tool whose endpoint the agent does not have (404 `not_found`) reports
`agent_outdated`: install the current agent with the Update line (see "Updating" in the repo README).

**Errors and length.** Every error result starts with its code in brackets, e.g.
`[cable_in_use] …`. No result is longer than 60 000 characters; a longer one is cut at a line
end with a line saying how to narrow the call. `docs/tools.md` lists the codes.

**Timeouts.** 15 s per request, 75 s for `/ediabas/*`. EDIABAS calls are sent one at a time,
in the order they were made, because the agent runs them one at a time; the 75 s starts when a
call is sent. A call that waits more than 120 s behind another one is not sent
(`ediabas_busy`). A connect timeout counts as never sent: the VM's address is looked up again
and the call is sent once more. Any other failure after a call that runs a job (every
`ediabas_*` tool but `ediabas_list_ecus`) may have reached the agent is `ediabas_request_lost`,
and the call is never sent a second time; nor is an `ediabas_list_ecus` call that timed out. The agent unloads EDIABAS after a short idle spell, so the first
`ediabas_*` call after a pause takes about 10 s longer; that is expected.

**No cable.** EDIABAS error 28 (`IFH-0018`) comes back as `cable_not_found`: the cable is not
connected to the VM. Even `ediabas_describe` of a real SGBD needs it (seen at the desk on
2026-11-21); the message asks the user to connect it and not to retry until they have.

## Build and test

Node 20+ (developed on Node 24).

```sh
npm ci
npm run typecheck
npm test
npm run build        # -> dist/index.js (executable, #!/usr/bin/env node)
```

stdout is the MCP channel. All logging goes to stderr.

## Token

The agent token is read from the Keychain (service `ediabas-bridge`, account `agent`) on first
use. It gets there from the VM: start `python3 dev/serve.py --accept-token` and paste the
Install line it prints (`docs/deploy.md`, "Token hand-off").

If the Keychain has none, `EDIABAS_AGENT_TOKEN` is used (with one warning on stderr). With
neither, the server still starts and each tool says how to store the token.

**With `EDIABAS_AGENT_URL` set, only `EDIABAS_AGENT_TOKEN` is used and the Keychain is never
read**, so the VM agent's token is never sent to an address set by hand (the replay agent in
`dev/`, or a mistyped URL). Without `EDIABAS_AGENT_TOKEN` every tool then fails with
`no_token`, saying why, and a 401 says the `EDIABAS_AGENT_TOKEN` value is wrong (not the
Keychain's). After a 401 the
token is re-read once, so a newly stored token is picked up without a restart.

## Environment (all optional)

| Variable | Default | Meaning |
|---|---|---|
| `EDIABAS_AGENT_URL` | — | full base URL; skips discovery (the `/health` agent check still applies). Requires `EDIABAS_AGENT_TOKEN`; the Keychain token is never sent to it |
| `EDIABAS_AGENT_PORT` | `8765` | port used with a discovered address |
| `EDIABAS_VM_MAC` | read from UTM | the VM's MAC address; when unset, read from UTM's config for the VM (see Finding the VM) |
| `EDIABAS_VM_NAME` | `Windows` | UTM VM name, for `utmctl`, messages and finding the VM's MAC in UTM's config |
| `EDIABAS_AGENT_TOKEN` | — | token fallback when the Keychain has none; the only token used with `EDIABAS_AGENT_URL` |
| `EDIABAS_AUDIT_LOG` | `~/Library/Logs/ediabas-bridge/audit.log` | audit log path |
| `EDIABAS_RECORD_DIR` | — | record every agent exchange into this folder (see Recording) |

**`config.local.json`.** Settings for one machine can also go in `config.local.json` at the repo
root (git-ignored; copy `config.local.example.json`). It is read once at start-up (restart Claude
after changing it) and may set `vmName`, `vmMac`, `agentPort` and `voltageSources` (extra
voltage readings for `ediabas_vehicle_status`, see [docs/tools.md](../docs/tools.md#ediabas_vehicle_status));
keys starting with `_` are
comments. An environment variable wins over the file, and the file wins over the defaults. A file
that is not valid JSON, an unknown key or a bad value is a warning on stderr and is ignored; the
server still starts. `dev/serve.py` reads the same file for `vmName` and `vmMac`.

## Finding the VM

The VM's MAC is `EDIABAS_VM_MAC`, else `vmMac` in `config.local.json`, else the first network
card's `MacAddress` in UTM's config for the VM,
`~/Library/Containers/com.utmapp.UTM/Data/Documents/<EDIABAS_VM_NAME>.utm/config.plist` (read
with `plutil` at each discovery). When none of these gives a MAC, the DHCP leases and ARP table
are skipped and only `utmctl` is tried; if that finds nothing, `vm_not_running` says to set
`EDIABAS_VM_MAC`, or `EDIABAS_VM_NAME` if the VM in UTM has another name.

Candidates for the VM's MAC come from `/var/db/dhcpd_leases` (latest expiry first), then
`arp -an`, then `utmctl ip-address <name>`. Only an address on the same subnet as a local
`bridge*` interface is ever contacted, and it is accepted only if `GET /hello` (sent with a
fresh random nonce and without the token) returns 200 with `"agent": "ediabas-bridge-agent"`
and a proof that matches this Mac's token ([docs/api.md](../docs/api.md#get-hello)); the token
is sent only after that. The accepted URL is cached; on a connection failure the cache is
cleared and discovery runs once more, and on an answer that is not the agent's
(`wrong_service`, `bad_response`) the cache is cleared and the next call proves the address
again.

## Troubleshooting: macOS Local Network permission

On recent macOS, connecting to an address on the local network (such as the VM's) needs
Local Network permission. When the Claude desktop app launches the server, macOS treats the
`node` binary as its own app and asks "node would like to find devices on your local
network". Until that is allowed, connects fail at once and the tools report
`local_network_blocked` ("This Mac could not reach the VM's address even though UTM's
network is up..."). Fix: System Settings > Privacy & Security > Local Network, allow `node`,
then ask again.

The permission is tied to the path of the node binary. Upgrading or reinstalling node (for
example a Homebrew upgrade that changes the path) can make macOS ask again.

Every failed probe or request, and every accepted address with its source
(leases / arp / utmctl / env), is logged to stderr, e.g.
`probe 192.168.64.3:8765 failed: unreachable (EHOSTUNREACH)`. The token is never logged.

## Audit log

One tab-separated line per tool call: UTC time, tool, outcome (`ok` or the error code),
duration in ms. `ediabas_*` calls add `ecu=…` and `job=…` (refused ones too) and, for a policy
refusal, `rule=…`. A write failure is reported on stderr and does not fail the call.

## Recording

With `EDIABAS_RECORD_DIR` set, every agent exchange except the `/hello` probe is written to
that folder as one JSON file named
`<UTC time>-<sequence>-<METHOD>-<path>[-<ECU>-<JOB>].json`, e.g.
`20261121T150405123Z-0003-POST-ediabas_job-NVE_65-IDENT.json`:

```json
{ "recordedAt": "…",
  "request": { "method": "POST", "path": "/ediabas/job", "query": {}, "body": { "ecu": "NVE_65", "job": "IDENT" } },
  "response": { "status": 200, "headers": { "content-type": "…" }, "body": { … } } }
```

The response has `body` (JSON), `bodyText` (anything else) or, for a screenshot, `imageFile`,
the name of the `.jpg`/`.png` written next to it. Response headers kept are `content-type` and
the agent's `x-*` headers. Request headers are not recorded, so the token and the
Authorization header never are; any token value in a body is replaced with `[redacted]`.
Recordings contain real vehicle data: scrub them before committing (`dev/`). Off by default;
a recording failure is logged on stderr and never fails the tool call.

## Registering with Claude

Use absolute paths, and a stable Node path (not an nvm version directory) so a Node upgrade
does not break it. The full steps are in the repo README, "Register the MCP server".

- Claude Code: `claude mcp add --scope user ediabas-bridge -- <node> <repo>/mac-mcp/dist/index.js`
- Claude desktop app: add to `mcpServers` in
  `~/Library/Application Support/Claude/claude_desktop_config.json`:

  ```json
  "ediabas-bridge": { "command": "<node>", "args": ["<repo>/mac-mcp/dist/index.js"] }
  ```
