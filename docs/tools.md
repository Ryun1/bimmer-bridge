# MCP tools — contract

What the Mac server (`mac-mcp`) offers Claude. The HTTP side is in [api.md](api.md). Skills
are written against this file. Every tool has `readOnlyHint: true`; none is destructive.

| Tool | Arguments | Agent call | Returns |
|---|---|---|---|
| `vm_health` | — | `GET /health` | status text: agent, session, BMW tools running, EDIABAS DLL and whether it is loaded (or stuck until the agent restarts), last cable use, policy version |
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
  leaves it. A refusal never reaches the VM. If the policy file is missing or invalid (including
  unknown or duplicate keys, which the agent also refuses), every `ediabas_*` tool refuses with
  `policy_unavailable`.
- `ediabas_read_faults` sends `FS_LESEN` per ECU, plus `IS_LESEN` per ECU with
  `includeInfoMemory`, 20 items per request at most (so at most two requests). Every item is
  policy-checked before anything is sent; if any ECU is refused, the whole call is refused and
  nothing is sent.
- Timeouts: 15 s; 75 s for `ediabas_*` calls.
- No tool retries a refusal.

## Output
- Text from the VM has control characters removed and is length-capped before it is shown.
- **Fault tables:** one row per result set: code in hex (`F_ORT_NR`), text (`F_ORT_TEXT`),
  present or stored, frequency, logged conditions. Field names differ between SGBDs, so known
  names are mapped and every other field is passed through as `key=value`. An ECU with no
  faults says so. A per-ECU error is shown in that ECU's place; the others still print.
- **Distances are miles first**, km in brackets: `121,609 mi (195,712 km)`. Miles are rounded down.
- `ediabas_vehicle_status` prints each reading's result fields as returned (`key=value`); the
  field names are mapped to "ignition on/off" and volts once Phase 6 has confirmed them.

## Errors
A failed tool call returns `isError` and one message that says what happened and what to do.
Codes (also the audit outcome):

| Code | From | Meaning for Claude |
|---|---|---|
| `vm_not_running`, `agent_not_answering`, `local_network_blocked`, `unauthorized`, `forbidden`, `no_token` | Mac | as before |
| `agent_not_answering` (EDIABAS job lost) | Mac | the connection broke or timed out after a request that runs an EDIABAS job (`ediabas_job`, `ediabas_read_faults`, `ediabas_vehicle_status`, `ediabas_describe`) may have reached the agent. It is never sent a second time; only a refused connect (nothing sent) is retried. The message says the job may have run: check `vm_health` before asking again |
| `agent_outdated` | Mac | the agent has no such endpoint; Ryan needs to run the Update line |
| `policy_unavailable` | Mac | the policy file could not be loaded; nothing is sent |
| `invalid_arguments` | Mac | arguments that cannot go together (`tailBytes` with `offset`/`length`); nothing is sent |
| `wrong_service`, `bad_response`, `config_error`, `internal_error` | Mac | something other than the agent answered, the agent and server are different versions, or a fault in the server itself |
| `not_run` | agent | the agent did not start the job (a batch item, or a single `ediabas_job` / `ediabas_describe`, HTTP 503), or stopped it. The message passes on the agent's reason: the request's time budget ran out, a BMW tool was opened during the request (ask Ryan to close it), an earlier job did not stop after `apiBreak`, or the request was abandoned. Do not retry in a loop |
| `job_not_allowed` | Mac or agent | refused by name; the message names the rule. Do not look for another way |
| `job_needs_review` | agent | the job's description suggests it may not be a pure read, or is empty or only a label such as `Jobbeschreibung`; the message quotes it. Ryan decides |
| `cable_in_use` | agent | a BMW tool is open, or was opened while the request ran (the agent checks again before every EDIABAS job); the message names it. Ask Ryan to close it. Do not retry in a loop. In `ediabas_describe`'s job list a tool opened midway does not fail the call: the descriptions not yet read show as not screened |
| `ecu_not_found`, `job_not_found` | agent | use `ediabas_list_ecus` / `ediabas_describe`; never guess names |
| `cable_not_found` | agent | EDIABAS could not initialise the interface (`IFH-0018`): the cable is not connected to the VM. Ryan plugs it into the Mac and passes it through in UTM. Needed even for `ediabas_describe` of a real SGBD; only `TMODE` and `UTILITY` load without it |
| `ediabas_error` | agent | EDIABAS's own code and text, e.g. no response from the ECU. The hint says the ECU often did not answer; for error 16 (`IFH-0006: COMMAND NOT ACCEPTED`) it first says the cable may not be attached to the VM (after a replug UTM does not hand it back by itself; Ryan ticks it in UTM's USB menu) |
| `ediabas_unavailable` | agent | DLL missing or EDIABAS would not start; or an earlier job did not stop after `apiBreak`, and the agent must be restarted (restart Windows in the VM) before EDIABAS can be used again |
| `job_timeout` | agent | the job did not finish in time |
| `window_not_found`, `window_minimized`, `desktop_unavailable` | agent | screenshots: wrong title, minimised window (Ryan restores it), locked screen |
| `path_not_allowed`, `file_not_found` | agent | logs: outside the allowed folders, or no such file |

## Recording (for fixtures)
With `EDIABAS_RECORD_DIR` set, the server writes every agent exchange (method, path, query,
request body, status, response body or image bytes; never the token) to that folder. Off by
default. `dev/replay-agent.ts` serves a scrubbed copy of such a folder.
