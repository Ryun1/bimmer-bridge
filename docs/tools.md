# MCP tools — contract

What the Mac server (`mac-mcp`) offers Claude. The HTTP side is in [api.md](api.md). Skills
are written against this file. Every tool has `readOnlyHint: true`; none is destructive.

| Tool | Arguments | Agent call | Returns |
|---|---|---|---|
| `vm_health` | — | `GET /health` | status text: first `Mac server {version}, started {local time}`, then the agent line (warns "WARNING: The Mac server and the agent are different versions; restart Claude or update the agent." when the two versions differ), session, BMW tools running, EDIABAS DLL and whether it is loaded (or stuck, or held open after a failed apiEnd, until the agent restarts), last cable use, policy version, and `Approved jobs: N pinned, M ignored.` when the agent reports approvals |
| `vm_list_windows` | — | `GET /windows` | table |
| `vm_screenshot` | `window?` (handle, title substring, or `desktop`; default `desktop`), `maxEdge?` (200–4000, default 1600) | `GET /screen/capture` | an image, then one line: title, size, capture method |
| `vm_logs_list` | `root?` (index), `glob?`, `since?` (ISO 8601), `limit?` (1–200) | `GET /logs` | the roots, then path / size / modified, newest first |
| `vm_logs_read` | `path`, then `tailBytes?` (default 16384) or `offset?` + `length?`, `grep?`; at most 65536 bytes per call | `GET /logs/read` | header line (path, range, encoding, truncated) then the text |
| `ediabas_list_ecus` | `filter?` | `GET /ediabas/ecus` | names with type (`prg` / `grp`); at most 500 names, then "N more; use filter to narrow the list." |
| `ediabas_describe` | `ecu`, `job?`, `filter?` (ignored with `job`), `namesOnly?` (job list only; sent as `comments=false`) | `GET /ediabas/describe` | job list with description and whether each is runnable; or one job's description, arguments and results |
| `ediabas_vehicle_status` | — | `POST /ediabas/jobs` (one batch, built on the Mac) | ignition (`UTILITY STATUS_ZUENDUNG`), the cable's supply (`UTILITY STATUS_UBATT`), the interface's KL30/KL15 voltages (`TMODE`), and any `voltageSources` configured on the Mac |
| `ediabas_job` | `ecu`, `job`, `args?`, `results?`, `raw?`, `timeoutSeconds?` (1–60) | `POST /ediabas/job` | formatted result sets; the agent's JSON when `raw: true` |
| `ediabas_jobs` | `jobs` (1–20 of `{ecu, job, args?, results?}`), `raw?`, `timeoutSeconds?` (1–60, for each job) | `POST /ediabas/jobs` | header "N of M ran.", then one block per job in request order; a failed job as `ECU JOB: [code] …` |
| `ediabas_read_faults` | `ecus` (1–20 names), `includeInfoMemory?` | `POST /ediabas/jobs` | one fault table per ECU |

## Rules the server enforces
- **Policy first.** `ediabas_job`, `ediabas_jobs`, `ediabas_read_faults`, `ediabas_vehicle_status`
  (every reading) and `ediabas_describe` (its `ecu`, and `job` for shape only) are checked against `policy/policy.json` on the Mac
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
  still print. An `IS_LESEN` that fails with `ediabas_error` after that ECU's `FS_LESEN` read shows "Info memory: not read (...). Many
  modules have no info memory (no IS_LESEN job); ediabas_describe <ECU> shows whether this one has it."
  Otherwise its error is shown like a fault memory error.
- **`FS_LESEN_FUNKTIONAL` (whole-car faults):** `ediabas_job` on `E60` prints one line per module
  with faults (address, each code in hex and its state), then the modules that could not be read,
  then `Without faults: ...`. Present or stored is inferred from the `F_ART` status byte
  (KWP2000), so the view says so; `raw: true` shows every field. A result with no `F_ANZ` field
  prints the generic view.
- **`IDENT_FUNKTIONAL` (whole-car module list):** `ediabas_job` on `E60` prints `N modules answered, M did not.` and one line per module (address, SGBD, group, name, BMW number, date); `raw: true` shows every field.
- A job status matching `ERROR_ECU_*NOT_SUPPORTED*` is followed by a note that the module refused the job (a negative response; not a bridge, cable or policy fault).
- **Distances are miles first**, km in brackets: `100,000 mi (160,935 km)`. Miles are rounded down.
- <a id="ediabas_vehicle_status"></a>`ediabas_vehicle_status` sends one `/ediabas/jobs` batch, in this
  order: `UTILITY STATUS_ZUENDUNG`, `UTILITY STATUS_UBATT`, each `voltageSources` entry, then
  `TMODE LESE_SPANNUNG_KL30` and `TMODE LESE_SPANNUNG_KL15`. Readings are labelled "Cable supply (UTILITY STATUS_UBATT; from the cable, not a battery reading)" and "Cable KL30 (TMODE LESE_SPANNUNG_KL30; from the cable, not a battery reading)". The result ends with two notes: IGNITION_NOTE (`STAT_ZUENDUNG` and the cable's KL15 as seen with the ignition on at the car, 2026-11-23, and with no car connected, 2026-11-22; ignition off in the car is not recorded yet) and CABLE_NOTE (`STAT_UBATT` and the cable's KL30 read the same with and without the car, `STAT_UBATT=1` and KL30 `E02E0000`, so they show only that the cable is powered; only a module reading, a voltage source, shows the car's supply voltage). Voltage sources are car-specific
  extra readings in the Mac's git-ignored `config.local.json`, e.g.
  `"voltageSources": [ { "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" } ]` (the E60/E61
  night-vision module's supply voltage); none by default, and the result then says "Voltage
  sources: none configured (voltageSources in config.local.json on the Mac)." At most 16 entries,
  each only `ecu` and `job`; a malformed entry is a start-up warning and is not used. An entry the
  job policy refuses is a start-up `config error` in the server's log, and the tool then refuses
  with `job_not_allowed` naming the entry, sending nothing, until it is fixed. The server reads
  the file at start-up: restart Claude after changing it. It prints each reading's result fields
  as returned (`key=value`); the field names are mapped to "ignition on/off" and volts once
  Phase 6 has confirmed them. A failed reading shows its `[code]` in its place.
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
  server started: now <entries, or none>. Only the owner should change it.` (on an error it
  follows the error's `[code]` text instead; at most 20 entries are shown, each cut to 80
  characters, then `... and N more`), and the Mac audit line gets the field `approvals_changed=1`. The agent re-reads
  `config.json` before answering `/health`, so the first call after an edit is the one flagged.
  The first call after the server starts only sets the baseline. An agent that does not report `hash` is never flagged.
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
| `vm_not_running`, `agent_not_answering`, `local_network_blocked`, `unauthorized`, `forbidden`, `no_token` | Mac | the VM or the agent could not be reached, or the token was refused or is missing; the message says what the user does ([install.md, Troubleshooting](install.md#troubleshooting)) |
| `ediabas_request_lost` | Mac | the connection broke or timed out after a request that runs an EDIABAS job (`ediabas_job`, `ediabas_jobs`, `ediabas_read_faults`, `ediabas_vehicle_status`, `ediabas_describe`) may have reached the agent, or the agent's address answered it with an HTTP error without the agent's error format (the message gives the status), so the job may have run. It was not sent again. Run `vm_health`; do not repeat the job unless the user asks |
| `ediabas_busy` | Mac | another EDIABAS call on this Mac was still running after 120 s; this one was not sent. Wait for the other call's answer, then ask again once if the result is still needed |
| `agent_outdated` | Mac | the agent has no such endpoint; the user needs to run the Update line |
| `policy_unavailable` | Mac | the policy file could not be loaded; nothing is sent |
| `policy_mismatch` | Mac | the agent reports a different SHA-256 for its policy file than this Mac loaded; nothing is sent to `/ediabas/*`. The agent or the Mac server is out of date: the user updates the agent (Update line) or restarts Claude. Do not look for another way |
| `invalid_arguments` | Mac | arguments that cannot go together (`tailBytes` with `offset`/`length`, or only one of `offset` and `length`); nothing is sent |
| `wrong_service`, `bad_response`, `config_error`, `internal_error` | Mac | something other than the agent answered, the agent and server are different versions, the agent's address answered with an HTTP error without the agent's error format (`bad_response`; the message gives the status: often the agent's web server refusing the request), or a fault in the server itself |
| `not_run` | agent | the agent did not start the job (a batch item, or a single `ediabas_job` / `ediabas_describe`, HTTP 503), or stopped it. The message passes on the agent's reason: the request's time budget ran out (time spent waiting behind another EDIABAS request counts), a BMW tool was opened during the request (ask the user to close it), an earlier job did not stop after `apiBreak`, or the request was abandoned. Do not retry in a loop |
| `job_not_allowed` | Mac or agent | refused by name; the message names the rule. Do not look for another way |
| `job_needs_review` | agent | the job's description, or the description of its arguments, suggests it may not be a pure read, or is empty or only a label such as `Jobbeschreibung`; or the description of an approved job has changed. The message quotes it. The user decides |
| `cable_in_use` | agent | a BMW tool is open, or was opened while the request ran (the agent checks again before and during every EDIABAS job); the message names it. Ask the user to close it. Do not retry in a loop. In `ediabas_describe`'s job list a tool opened midway does not fail the call: the descriptions not yet read show as not screened |
| `ecu_not_found`, `job_not_found` | agent | use `ediabas_list_ecus` / `ediabas_describe`; never guess names. `job_not_found` comes from `ediabas_describe` only; a run of a job the SGBD lacks gives `ediabas_error` |
| `cable_not_found` | agent | EDIABAS could not initialise the interface (`IFH-0018`): the cable is not connected to the VM. The user plugs it into the Mac and passes it through in UTM. Needed even for `ediabas_describe` of a real SGBD; only `TMODE` and `UTILITY` load without it |
| `ediabas_error` | agent | EDIABAS's own code and text, e.g. no response from the ECU. The hint depends on the code (the `IFH-`/`SYS-` name in the text, else the number: IFH 10–59, BIP 60–89, SYS 90–119, API 120–149, NET 150–249): 16 `IFH-0006` and 48 `IFH-0038`: the cable may not be attached to the VM (after a replug UTM does not hand it back by itself; after the Mac sleeps UTM hands it back to the Mac; the user ticks it in UTM's USB menu), otherwise the ECU did not answer; 12 `IFH-0002` and 13 `IFH-0003`: the cable, not the ECU (check the OBD port and UTM; 13 came from every real SGBD and group file with the cable in the VM but not in the car, rehearsal 2026-11-22); 17 `IFH-0007`, 25 `IFH-0015`, 47 `IFH-0037`: battery voltage wrong or low, check the charger and stop reading; 19 `IFH-0009`: the cable works but the ECU did not answer (ignition, not fitted or asleep, wrong SGBD; no SGBDs tried at random); 20 `IFH-0010`: the ECU's answer was disturbed; 26 `IFH-0016` and 42 `IFH-0032`: the cable sees the ignition as off; 100 `SYS-0010`: the SGBD could not start, some files need the module to answer even to load. Any other code: the ECU often did not answer. When the job finished but its results could not be read, the message says "The job finished but its results could not be read…", without the "ECU did not answer" hint. The message then says to ask the user whether the ignition is on ("ediabas_vehicle_status shows the car is awake only through a configured voltage source (its battery and KL30 readings come from the cable)") and before trying again, except for the cable, voltage and ignition codes (`IFH-0002`, `-0003`, `-0006`, `-0007`, `-0015`, `-0016`, `-0032`, `-0037`, `-0038`), which say only "Ask the user before trying again." (`ediabas_vehicle_status`'s battery and KL30 readings come from the cable and cannot settle them). On a job run (`ediabas_job`, `ediabas_jobs`, `ediabas_read_faults`, `ediabas_vehicle_status`) a code that is not `IFH-` ends with "If the job name may be wrong, check it with ediabas_describe." (a wrong job name gives `SYS-0008`, never an `IFH-` code). |
| `ediabas_unavailable` | agent | DLL missing or EDIABAS would not start; or an earlier job did not stop after `apiBreak`, and the agent must be restarted (restart Windows in the VM) before EDIABAS can be used again |
| `job_timeout` | agent | the job did not finish in time |
| `window_not_found`, `window_minimized`, `desktop_unavailable` | agent | screenshots: wrong title, minimised window (the user restores it), locked screen, secure desktop or nobody logged in |
| `capture_busy` | agent | another screenshot is still being taken in the VM; nothing was captured. Try again in a moment |
| `capture_failed` | agent | the desktop was available but Windows failed to copy the image or read the screen size. Try again once, or capture the desktop instead |
| `bad_request` | agent | the agent rejected a parameter (for example a bad `grep` pattern in `vm_logs_read`, or a grep over the 5 s limit). Fix the argument; do not retry it unchanged |
| `path_not_allowed`, `file_not_found` | agent | logs: outside the allowed folders, or no such file |

## Recording (for fixtures)
The production server never records. The dev entry point `dev/record-server.ts` (register it
instead of the server for a recording session, with `EDIABAS_RECORD_DIR` set) writes every agent
exchange (method, path, query, request body, status, response body or image bytes; never the
token or request headers, and not `/hello`) to that folder; see
[development.md](development.md#recording-and-scrubbing). `dev/replay-agent.ts` serves a
scrubbed copy of such a folder.
