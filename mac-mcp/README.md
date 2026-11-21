# mac-mcp — ediabas-bridge MCP server

Local stdio MCP server for Claude Code and the Claude desktop app. It finds the Windows VM
in UTM, calls the VM agent over authenticated HTTP (`docs/api.md`) and exposes read-only
tools. Everything it does is read-only; it has no way to click, type, clear faults, code or
flash anything.

## Tools

All read-only (`readOnlyHint: true`). The contract is `docs/tools.md`.

| Tool | What it does |
|---|---|
| `vm_health` | agent status, BMW tools holding the cable, EDIABAS DLL, last cable use, agent features, job policy version (warns when the agent's differs from the Mac's) |
| `vm_list_windows` | windows open in the VM |
| `vm_screenshot` | an image of the desktop or one window (`window`, `maxEdge`), then a one-line caption |
| `vm_logs_list` / `vm_logs_read` | log files under the agent's allowed folders; read a tail or a range, optionally grep |
| `ediabas_list_ecus` | SGBD (`prg`) and group (`grp`) names |
| `ediabas_describe` | an SGBD's jobs with whether each is runnable, or one job's arguments and results |
| `ediabas_vehicle_status` | ignition, battery and cable voltage readings, fields as returned |
| `ediabas_job` | one allowed job, result sets as text (or the agent's JSON with `raw: true`) |
| `ediabas_read_faults` | `FS_LESEN` (and `IS_LESEN`) for 1–20 ECUs, one fault table per ECU, distances in miles first |

**Job policy.** `src/policy.ts` loads `policy/policy.json` from the repo root (two levels above
`src/` and `dist/`) once at start-up and evaluates it as `docs/api.md` "Job policy" says.
`ediabas_job`, `ediabas_read_faults` and `ediabas_describe` (names only) are checked before
any request leaves the Mac, so a refused job never reaches the VM. If the file is missing or
invalid, every `ediabas_*` tool refuses with `policy_unavailable` (fail closed); restore the
file and restart Claude. `test/policy.test.ts` runs every case in `policy/policy-cases.json`.

**Older agent.** A tool whose endpoint the agent does not have (404 `not_found`) reports
`agent_outdated`: install the current agent with the Update line (see "Installing or updating the agent in the VM" in the repo README).

**Timeouts.** 15 s per request, 75 s for `/ediabas/*`. An EDIABAS call that timed out is never
sent a second time.

## Build and test

Node 20+ (developed on Node 24).

```sh
npm install
npm run typecheck
npm test
npm run build        # -> dist/index.js (executable, #!/usr/bin/env node)
```

stdout is the MCP channel. All logging goes to stderr.

## Token

The agent token is read from the Keychain on first use:

```sh
security add-generic-password -s ediabas-bridge -a agent -w '<token>' -U
```

If the Keychain has none, `EDIABAS_AGENT_TOKEN` is used (with one warning on stderr). With
neither, the server still starts and each tool says how to store the token. After a 401 the
token is re-read once, so a newly stored token is picked up without a restart.

## Environment (all optional)

| Variable | Default | Meaning |
|---|---|---|
| `EDIABAS_AGENT_URL` | — | full base URL; skips discovery (the `/health` agent check still applies) |
| `EDIABAS_AGENT_PORT` | `8765` | port used with a discovered address |
| `EDIABAS_VM_MAC` | `F2:E5:81:1E:82:07` | the VM's MAC address |
| `EDIABAS_VM_NAME` | `Windows` | UTM VM name, for `utmctl` and messages |
| `EDIABAS_AGENT_TOKEN` | — | token fallback when the Keychain has none |
| `EDIABAS_AUDIT_LOG` | `~/Library/Logs/ediabas-bridge/audit.log` | audit log path |
| `EDIABAS_RECORD_DIR` | — | record every agent exchange into this folder (see Recording) |

## Finding the VM

Candidates for the VM's MAC come from `/var/db/dhcpd_leases` (latest expiry first), then
`arp -an`, then `utmctl ip-address <name>`. Only an address on the same subnet as a local
`bridge*` interface is ever contacted, and it is accepted only if `GET /health` returns 200
with `"agent": "ediabas-bridge-agent"`. The accepted URL is cached; on a connection failure
the cache is cleared and discovery runs once more.

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

With `EDIABAS_RECORD_DIR` set, every agent exchange (discovery probes included) is written to
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
does not break it.

- Claude Code: `claude mcp add ediabas-bridge -- <node> <repo>/mac-mcp/dist/index.js`
- Claude desktop app: add to `mcpServers` in
  `~/Library/Application Support/Claude/claude_desktop_config.json`:

  ```json
  "ediabas-bridge": { "command": "<node>", "args": ["<repo>/mac-mcp/dist/index.js"] }
  ```
