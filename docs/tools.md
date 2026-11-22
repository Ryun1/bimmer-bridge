# MCP tools — contract

What the Mac server (`mac-mcp`) offers Claude. The HTTP side is in [api.md](api.md). Skills
are written against this file. Every tool has `readOnlyHint: true`; none is destructive.

| Tool | Arguments | Agent call | Returns |
|---|---|---|---|
| `vm_health` | — | `GET /health` | status text: first `Mac server {version}, started {local time}`, then the agent line (warns "WARNING: The Mac server and the agent are different versions; restart Claude or update the agent." when the two versions differ), session, BMW tools running, EDIABAS DLL and whether it is loaded (or stuck until the agent restarts), last cable use, policy version, and `Approved jobs: N pinned, M ignored.` when the agent reports approvals |
| `vm_list_windows` | — | `GET /windows` | table |
| `vm_screenshot` | `window?` (handle, title substring, or `desktop`; default `desktop`), `maxEdge?` (200–4000, default 1600) | `GET /screen/capture` | an image, then one line: title, size, capture method |
| `vm_logs_list` | `root?` (index), `glob?`, `since?` (ISO 8601), `limit?` (1–200) | `GET /logs` | the roots, then path / size / modified, newest first |
| `vm_logs_read` | `path`, then `tailBytes?` (default 16384) or `offset?` + `length?`, `grep?`; at most 65536 bytes per call | `GET /logs/read` | header line (path, range, encoding, truncated) then the text |
| `ediabas_list_ecus` | `filter?` | `GET /ediabas/ecus` | names with type (`prg` / `grp`); at most 500 names, then "N more; use filter to narrow the list." |
| `ediabas_describe` | `ecu`, `job?`, `filter?` (ignored with `job`), `namesOnly?` (job list only; sent as `comments=false`) | `GET /ediabas/describe` | job list with description and whether each is runnable; or one job's description, arguments and results |
| `ediabas_vehicle_status` | — | `GET /ediabas/vehicle` | ignition, battery presence, voltages, cable-side readings |
| `ediabas_job` | `ecu`, `job`, `args?`, `results?`, `raw?`, `timeoutSeconds?` (1–60) | `POST /ediabas/job` | formatted result sets; the agent's JSON when `raw: true` |
| `ediabas_jobs` | `jobs` (1–20 of `{ecu, job, args?, results?}`), `raw?`, `timeoutSeconds?` (1–60, for each job) | `POST /ediabas/jobs` | header "N of M ran.", then one block per job in request order; a failed job as `ECU JOB: [code] …` |
| `ediabas_read_faults` | `ecus` (1–20 names), `includeInfoMemory?` | `POST /ediabas/jobs` | one fault table per ECU |

## Rules the server enforces
- **Policy first.** `ediabas_job`, `ediabas_jobs`, `ediabas_read_faults` and `ediabas_describe`
  (its `ecu`, and `job` for shape only) are checked against `policy/policy.json` on the Mac
  before any request leaves it. A refusal never reaches the VM. If the policy file is missing or
  invalid (including unknown or duplicate keys, which the agent also refuses), every `ediabas_*`
  tool refuses with `policy_unavailable`. Each `ediabas_*` call also reads the agent's `/health`
  first and refuses with `policy_mismatch` when the agent's policy file hash differs from this Mac's.
- `ediabas_read_faults` sends `FS_LESEN` per ECU, plus `IS_LESEN` per ECU with
  `includeInfoMemory`, 20 items per request at most (so at most two requests). Every item is
  policy-checked before anything is sent; if any ECU is refused, the whole call is refused and
  nothing is sent. `ediabas_jobs` works the same way: one refused job and nothing is sent.
- Timeouts: 15 s; 75 s for `ediabas_*` calls.
- EDIABAS calls (every `ediabas_*` tool) are sent to the agent one at a time, in the order they
  were made. The 75 s timeout starts when a call is sent, not while it waits. A call that waits
  more than 120 s behind another one is not sent (`ediabas_busy`).
- A connect timeout counts as never sent: the VM's address is looked up again and the call is
  sent once more. Any other failure after a call that runs a job may have reached the agent is
  `ediabas_request_lost`, and the call is never sent a second time.
- No tool retries a refusal.

## Output
- Text from the VM has control characters removed and is length-capped before it is shown.
- No result is longer than 60 000 characters. A longer one is cut at a line end, and a last line
  says how much was cut and how to narrow the call (for example "Use filter.").
- **Fault tables:** one row per result set: code in hex (`F_ORT_NR`), text (`F_ORT_TEXT`),
  present or stored, frequency, logged conditions. Field names differ between SGBDs, so known
  names are mapped and every other field is passed through as `key=value`. An ECU with no
  faults says so. A per-ECU error is shown in that ECU's place, with its `[code]`; the others
  still print. A module whose SGBD has no `IS_LESEN` shows "Info memory: no info memory on this
  module." (not a failure).
- **Distances are miles first**, km in brackets: `100,000 mi (160,935 km)`. Miles are rounded down.
- `ediabas_vehicle_status` prints each reading's result fields as returned (`key=value`); the
  field names are mapped to "ignition on/off" and volts once Phase 6 has confirmed them.
- `ediabas_describe` with `namesOnly` adds the line "(names only: not screened, verdicts by
  name)". With `job`, for a screened job (review is not `not_required`) it prints
  `Description hash: <hash>` under the description, and when the job needs review also:
  `To approve this job as it is described now, the user adds "ECU:JOB@<hash>" to reviewedJobs in C:\ProgramData\EdiabasBridge\config.json in the VM (no restart needed).`
- `vm_health`, when the agent reports approvals: `Approved jobs: N pinned, M ignored.`, then
  each pinned entry on its own line, and when M > 0 also `M approval entries are ignored
  (unpinned, malformed or not upper case); ediabas_describe prints the pinned entry for each.`
  (`1 approval entry is ignored ...` for one). A 0.4.0 agent's counts print as `Approved jobs: N pinned,
  M unpinned.`, and when M > 0 also `M unpinned approvals skip the description screen on this
  older agent and approve nothing from 0.5.0; ediabas_describe prints the pinned entry for each.`
- **Approvals changed.** Every `ediabas_*` call, and `vm_health`, reads `/health` first (one
  extra round trip; it never loads the DLL; if it fails, the tool fails as its own request
  would). The Mac server remembers the agent's `approvals.hash`; when it differs from the last
  one seen, the result starts with `[approvals changed] reviewedJobs in the VM changed since this
  server started: now <entries, or none>. Only the owner should change it.` (the result may be an
  error too), and the Mac audit line gets the field `approvals_changed=1`. The first call after
  the server starts only sets the baseline. An agent that does not report `hash` is never flagged.
- **Car session.** The results of `ediabas_job`, `ediabas_jobs`, `ediabas_read_faults` and
  `ediabas_vehicle_status` (errors too) end with "Car session: X min Y s since the first read
  from the car (aim to finish within 10 min)." The session starts at the first successful read
  from a module other than `TMODE` or `UTILITY`, and ends after 5 minutes without one. It
  measures time since that first read, not since the ignition was switched on. No line is
  printed when no session is running.

## Errors
A failed tool call returns `isError` and one message that says what happened and what to do.
The message starts with the code in brackets, e.g. `[cable_in_use] The diagnostic cable is in
use …`. Codes (also the audit outcome):

| Code | From | Meaning for Claude |
|---|---|---|
| `vm_not_running`, `agent_not_answering`, `local_network_blocked`, `unauthorized`, `forbidden`, `no_token` | Mac | the VM or the agent could not be reached, or the token was refused or is missing; the message says what the user does (README, Troubleshooting) |
| `ediabas_request_lost` | Mac | the connection broke or timed out after a request that runs an EDIABAS job (`ediabas_job`, `ediabas_jobs`, `ediabas_read_faults`, `ediabas_vehicle_status`, `ediabas_describe`) may have reached the agent, so the job may have run. It was not sent again. Run `vm_health`; do not repeat the job unless the user asks |
| `ediabas_busy` | Mac | another EDIABAS call on this Mac was still running after 120 s; this one was not sent. Wait for the other call's answer, then ask again once if the result is still needed |
| `agent_outdated` | Mac | the agent has no such endpoint; the user needs to run the Update line |
| `policy_unavailable` | Mac | the policy file could not be loaded; nothing is sent |
| `policy_mismatch` | Mac | the agent reports a different SHA-256 for its policy file than this Mac loaded; nothing is sent to `/ediabas/*`. The agent or the Mac server is out of date: the user updates the agent (Update line) or restarts Claude. Do not look for another way |
| `invalid_arguments` | Mac | arguments that cannot go together (`tailBytes` with `offset`/`length`, or only one of `offset` and `length`); nothing is sent |
| `wrong_service`, `bad_response`, `config_error`, `internal_error` | Mac | something other than the agent answered, the agent and server are different versions, or a fault in the server itself |
| `not_run` | agent | the agent did not start the job (a batch item, or a single `ediabas_job` / `ediabas_describe`, HTTP 503), or stopped it. The message passes on the agent's reason: the request's time budget ran out (time spent waiting behind another EDIABAS request counts), a BMW tool was opened during the request (ask the user to close it), an earlier job did not stop after `apiBreak`, or the request was abandoned. Do not retry in a loop |
| `job_not_allowed` | Mac or agent | refused by name; the message names the rule. Do not look for another way |
| `job_needs_review` | agent | the job's description, or the description of its arguments, suggests it may not be a pure read, or is empty or only a label such as `Jobbeschreibung`; or the description of an approved job has changed. The message quotes it. The user decides |
| `cable_in_use` | agent | a BMW tool is open, or was opened while the request ran (the agent checks again before and during every EDIABAS job); the message names it. Ask the user to close it. Do not retry in a loop. In `ediabas_describe`'s job list a tool opened midway does not fail the call: the descriptions not yet read show as not screened |
| `ecu_not_found`, `job_not_found` | agent | use `ediabas_list_ecus` / `ediabas_describe`; never guess names. `job_not_found` can come from `ediabas_describe`, `ediabas_job`, an `ediabas_jobs` or `ediabas_read_faults` item, and `ediabas_vehicle_status` |
| `cable_not_found` | agent | EDIABAS could not initialise the interface (`IFH-0018`): the cable is not connected to the VM. The user plugs it into the Mac and passes it through in UTM. Needed even for `ediabas_describe` of a real SGBD; only `TMODE` and `UTILITY` load without it |
| `ediabas_error` | agent | EDIABAS's own code and text, e.g. no response from the ECU. The hint says the ECU often did not answer; for error 16 (`IFH-0006: COMMAND NOT ACCEPTED`) it first says the cable may not be attached to the VM (after a replug UTM does not hand it back by itself; the user ticks it in UTM's USB menu). When the job finished but its results could not be read, the message says "The job finished but its results could not be read…", without the "ECU did not answer" hint |
| `ediabas_unavailable` | agent | DLL missing or EDIABAS would not start; or an earlier job did not stop after `apiBreak`, and the agent must be restarted (restart Windows in the VM) before EDIABAS can be used again |
| `job_timeout` | agent | the job did not finish in time |
| `window_not_found`, `window_minimized`, `desktop_unavailable` | agent | screenshots: wrong title, minimised window (the user restores it), locked screen, secure desktop or nobody logged in |
| `capture_busy` | agent | another screenshot is still being taken in the VM; nothing was captured. Try again in a moment |
| `capture_failed` | agent | the desktop was available but Windows failed to copy the image or read the screen size. Try again once, or capture the desktop instead |
| `bad_request` | agent | the agent rejected a parameter (for example a bad `grep` pattern in `vm_logs_read`, or a grep over the 5 s limit). Fix the argument; do not retry it unchanged |
| `path_not_allowed`, `file_not_found` | agent | logs: outside the allowed folders, or no such file |

## Recording (for fixtures)
With `EDIABAS_RECORD_DIR` set, the server writes every agent exchange (method, path, query,
request body, status, response body or image bytes; never the token) to that folder. Off by
default. `dev/replay-agent.ts` serves a scrubbed copy of such a folder.
