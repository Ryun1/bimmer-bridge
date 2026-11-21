# mac-mcp — ediabas-bridge MCP server

Local stdio MCP server for Claude Code and the Claude desktop app. It finds the Windows VM
in UTM, calls the VM agent over authenticated HTTP (`docs/api.md`) and exposes read-only
tools. Everything it does is read-only; it has no way to click, type, clear faults, code or
flash anything.

Tools so far: `vm_health`, `vm_list_windows`.

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

## Finding the VM

Candidates for the VM's MAC come from `/var/db/dhcpd_leases` (latest expiry first), then
`arp -an`, then `utmctl ip-address <name>`. Only an address on the same subnet as a local
`bridge*` interface is ever contacted, and it is accepted only if `GET /health` returns 200
with `"agent": "ediabas-bridge-agent"`. The accepted URL is cached; on a connection failure
the cache is cleared and discovery runs once more.

## Audit log

One tab-separated line per tool call: UTC time, tool, outcome (`ok` or the error code),
duration in ms. A write failure is reported on stderr and does not fail the call.

## Registering with Claude

Use absolute paths, and a stable Node path (not an nvm version directory) so a Node upgrade
does not break it.

- Claude Code: `claude mcp add ediabas-bridge -- <node> <repo>/mac-mcp/dist/index.js`
- Claude desktop app: add to `mcpServers` in
  `~/Library/Application Support/Claude/claude_desktop_config.json`:

  ```json
  "ediabas-bridge": { "command": "<node>", "args": ["<repo>/mac-mcp/dist/index.js"] }
  ```
