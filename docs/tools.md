# MCP tools — contract

What the Mac server (`mac-mcp`) offers Claude. The HTTP side is in [api.md](api.md). Skills
are written against this file. Every tool has `readOnlyHint: true`; none is destructive.

| Tool | Arguments | Agent call | Returns |
|---|---|---|---|
| `vm_health` | — | `GET /health` | status text: agent, session, BMW tools running, EDIABAS DLL, last cable use, policy version |
| `vm_list_windows` | — | `GET /windows` | table |
| `vm_screenshot` | `window?` (handle, title substring, or `desktop`; default `desktop`), `maxEdge?` (200–4000, default 1600) | `GET /screen/capture` | an image, then one line: title, size, capture method |
| `vm_logs_list` | `root?` (index), `glob?`, `since?` (ISO 8601), `limit?` (1–200) | `GET /logs` | the roots, then path / size / modified, newest first |
| `vm_logs_read` | `path`, then `tailBytes?` or `offset?` + `length?`, `grep?` | `GET /logs/read` | header line (path, range, encoding, truncated) then the text |
| `ediabas_list_ecus` | `filter?` | `GET /ediabas/ecus` | names with type (`prg` / `grp`) |
| `ediabas_describe` | `ecu`, `job?`, `filter?` | `GET /ediabas/describe` | job list with description and whether each is runnable; or one job's arguments and results |
| `ediabas_vehicle_status` | — | `GET /ediabas/vehicle` | ignition, battery presence, voltages, cable-side readings |
| `ediabas_job` | `ecu`, `job`, `args?`, `results?`, `raw?` | `POST /ediabas/job` | formatted result sets; the agent's JSON when `raw: true` |
| `ediabas_read_faults` | `ecus` (1–20 names), `includeInfoMemory?` | `POST /ediabas/jobs` | one fault table per ECU |

## Rules the server enforces
- **Policy first.** `ediabas_job`, `ediabas_read_faults` and `ediabas_describe` (its `ecu`, and
  `job` for shape only) are checked against `policy/policy.json` on the Mac before any request
  leaves it. A refusal never reaches the VM. If the policy file is missing or invalid, every
  `ediabas_*` tool refuses with `policy_unavailable`.
- `ediabas_read_faults` sends `FS_LESEN` per ECU, plus `IS_LESEN` per ECU with
  `includeInfoMemory`, 20 items per request at most (so at most two requests).
- Timeouts: 15 s; 75 s for `ediabas_*` calls.
- No tool retries a refusal.

## Output
- Text from the VM has control characters removed and is length-capped before it is shown.
- **Fault tables:** one row per result set: code in hex (`F_ORT_NR`), text (`F_ORT_TEXT`),
  present or stored, frequency, logged conditions. Field names differ between SGBDs, so known
  names are mapped and every other field is passed through as `key=value`. An ECU with no
  faults says so. A per-ECU error is shown in that ECU's place; the others still print.
- **Distances are miles first**, km in brackets: `121,609 mi (195,712 km)`.
- `ediabas_vehicle_status` prints each reading's result fields as returned (`key=value`); the
  field names are mapped to "ignition on/off" and volts once Phase 6 has confirmed them.

## Errors
A failed tool call returns `isError` and one message that says what happened and what to do.
Codes (also the audit outcome):

| Code | From | Meaning for Claude |
|---|---|---|
| `vm_not_running`, `agent_not_answering`, `local_network_blocked`, `unauthorized`, `forbidden`, `no_token` | Mac | as before |
| `agent_outdated` | Mac | the agent has no such endpoint; Ryan needs to run the Update line |
| `policy_unavailable` | Mac | the policy file could not be loaded; nothing is sent |
| `job_not_allowed` | Mac or agent | refused by name; the message names the rule. Do not look for another way |
| `job_needs_review` | agent | the job's description suggests it may not be a pure read; the message quotes it. Ryan decides |
| `cable_in_use` | agent | a BMW tool is open; the message names it. Ask Ryan to close it. Do not retry in a loop |
| `ecu_not_found`, `job_not_found` | agent | use `ediabas_list_ecus` / `ediabas_describe`; never guess names |
| `ediabas_error` | agent | EDIABAS's own code and text, e.g. no response from the ECU |
| `ediabas_unavailable` | agent | DLL missing or EDIABAS would not start |
| `job_timeout` | agent | the job did not finish in time |
| `window_not_found`, `window_minimized`, `desktop_unavailable` | agent | screenshots: wrong title, minimised window (Ryan restores it), locked screen |
| `path_not_allowed`, `file_not_found` | agent | logs: outside the allowed folders, or no such file |

## Recording (for fixtures)
With `EDIABAS_RECORD_DIR` set, the server writes every agent exchange (method, path, query,
request body, status, response body or image bytes; never the token) to that folder. Off by
default. `dev/replay-agent.ts` serves a scrubbed copy of such a folder.
