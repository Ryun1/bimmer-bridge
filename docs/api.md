# Agent HTTP API — contract

The contract between `vm-agent` (server) and `mac-mcp` (client). Both sides are built against
this file. Change it here first.

The first part covers `/health` and `/windows` (Phases 1 and 3). The part from "Phases 2, 4, 5
and 6" on adds the error details, the `/health` additions, the job policy and the EDIABAS,
screenshot and log endpoints.

## Transport
- Plain HTTP, port **8765**, on the VM's shared-network IPv4 address only.
- Every request, including `/health`, needs `Authorization: Bearer <token>`.
- The token is 32 random bytes, base64url without padding (43 characters).
- Responses are `application/json; charset=utf-8`. Property names are camelCase.
  Absent values are `null`, never omitted.

## Errors
Any non-2xx response has this body:

```json
{ "error": { "code": "unauthorized", "message": "Missing or invalid bearer token." } }
```

| Status | `code` | When |
|---|---|---|
| 403 | `forbidden` | the request did not come from the VM's network gateway (the Mac). Checked first, before the token is looked at |
| 401 | `unauthorized` | header missing, malformed, or token wrong. Sent before any routing, so an unauthenticated caller cannot tell which paths exist |
| 404 | `not_found` | unknown path (authenticated callers only) |
| 405 | `method_not_allowed` | known path, wrong method |
| 500 | `internal_error` | unhandled failure; the message is generic, details go to the agent log |

A 401 also carries `WWW-Authenticate: Bearer`.

Order of checks: source address (403), token (401), path (404), method (405). Requests that
Kestrel rejects itself (malformed HTTP, oversized headers, HTTP/2) never reach these checks
and get Kestrel's own response.

## `GET /health`
Never touches the cable and never loads the EDIABAS DLL.

```json
{
  "agent": "ediabas-bridge-agent",
  "version": "0.4.0",
  "startedAt": "2026-11-21T15:04:05Z",
  "uptimeSeconds": 512,
  "os": {
    "description": "Microsoft Windows 10.0.26100",
    "osArchitecture": "Arm64",
    "processArchitecture": "X86"
  },
  "user": "owner",
  "elevated": false,
  "sessionId": 1,
  "bindAddress": "192.168.64.3",
  "port": 8765,
  "gateway": "192.168.64.1",
  "conflictingProcesses": [ { "name": "INPALOAD", "pid": 4312 } ],
  "ediabas": { "dllPath": "C:\\EDIABAS\\BIN\\api32.dll", "dllFound": true },
  "lastCableUseAt": null,
  "approvals": { "pinned": 1, "unpinned": 0 }
}
```

- `agent` is always the literal `ediabas-bridge-agent`. Clients use it to confirm they
  reached the right service.
- `conflictingProcesses` lists running processes whose name matches the configured conflict
  list (case-insensitive, without `.exe`). Empty array when none.
- `sessionId` is the Windows session the agent runs in. `0` means it is not in an
  interactive session and screenshots will not work.
- `ediabas.dllFound` is a file-existence check only.
- `lastCableUseAt`, `policyVersion`, `capabilities`, `ediabas.loaded`, `ediabas.stuck` and
  `approvals` are described under "`GET /health` additions" below.

## `GET /windows`
Visible top-level windows with a non-empty title, in z-order (front first).

```json
{
  "windows": [
    {
      "handle": "0x000A0B2C",
      "title": "INPA - BMW Group",
      "processName": "INPALOAD",
      "pid": 4312,
      "bounds": { "x": 0, "y": 0, "width": 1280, "height": 800 },
      "minimized": false
    }
  ]
}
```

- `handle` is the HWND as `0x` plus 8 upper-case hex digits.
- `processName` has no `.exe`; `null` if the process cannot be queried.
- `bounds` are screen pixels. For a minimised window they are whatever Windows reports.
- Cloaked windows (hidden UWP shells) are left out.

---

# Phases 2, 4, 5 and 6

## Error body, extended
Errors may carry structured details. `details` is `null` when there are none.

```json
{ "error": { "code": "job_not_allowed", "message": "…", "details": { "ecu": "NVE_65", "job": "FS_LOESCHEN", "rule": "deny.substring:LOESCH" } } }
```

| Status | `code` | When | `details` |
|---|---|---|---|
| 400 | `bad_request` | missing or malformed parameter or JSON body; body over 64 KB | `{ "parameter": "…" }` or `null` |
| 403 | `job_not_allowed` | the job policy refused the ECU/job/args | `{ ecu, job, rule }` |
| 403 | `job_needs_review` | a job allowed by the `STATUS_` pattern has a description that fails the screen, or has no description; or it is called with `args` and the description of its arguments fails the screen or is missing; or it has a pinned approval and its description has changed, is empty or could not be read (see "Approvals") | `{ ecu, job, description, matched: ["start"] }` (`matched` is `[]` when the description is empty or only labels, and for a pinned approval that no longer holds; see "Description screen"). For the argument screen `description` holds the argument text |
| 403 | `path_not_allowed` | log path outside the configured roots, or an escape attempt | `{ reason }` |
| 404 | `ecu_not_found` | no `.prg` or `.grp` of that name in the Ecu folder | `{ ecu }` |
| 404 | `job_not_found` | `describe` was asked for a job the SGBD does not have; or, from `/ediabas/job`, a `/ediabas/jobs` item or a `/ediabas/vehicle` item, the job failed with `ediabas_error` and the SGBD's own `_JOBS` list does not have it (see "Job not in the SGBD"). Message: "{ECU} has no job {JOB}." | `{ ecu, job }` |
| 404 | `window_not_found` | no visible window matches | `null` |
| 404 | `file_not_found` | log file does not exist | `null` |
| 409 | `cable_in_use` | a BMW tool or another `api32.dll` user is running. No override | `{ processes: [ { name, pid } ] }` |
| 409 | `window_minimized` | the window is minimised; the agent never restores it | `{ handle, title }` |
| 502 | `cable_not_found` | EDIABAS could not initialise the interface: error 28, `IFH-0018: INITIALIZATION ERROR` | `{ errorCode: 28, errorText }` |
| 502 | `ediabas_error` | EDIABAS reported an error for the job; or the job finished but its results could not be read (message "The job finished but its results could not be read.", with whatever EDIABAS reported in `details` and `resultsUnread: true` there) | `{ errorCode: 159, errorText: "IFH-0009: …" }`; plus `resultsUnread: true` only in the unread-results case |
| 503 | `ediabas_unavailable` | `api32.dll` missing, an expected export missing, `apiInit` failed, or an earlier job did not stop after `apiBreak` (the agent must be restarted) | `{ reason }` |
| 500 | `capture_failed` | the desktop was available but the copy failed or the screen size could not be read | `null` |
| 503 | `desktop_unavailable` | only a locked screen, the secure desktop, or session 0 | `null` |
| 503 | `capture_busy` | another capture is still running; nothing was captured. Try again | `null` |
| 503 | `not_run` | `/ediabas/job` or describe started no job, or stopped one: the request's `maxJobTimeoutSeconds` budget ran out before the job (or the screen before it) could start, or the client gave up (the running job was stopped with `apiBreak`). The message says which, e.g. "The request was abandoned before {JOB} started." A request that spent the whole budget queued behind another EDIABAS request gets "The request waited behind another EDIABAS request for the whole {max} s limit; nothing was started." on `/ediabas/job`, `/ediabas/jobs`, `/ediabas/vehicle` and both describe modes, and EDIABAS is not loaded for it. Also a per-item code in `/ediabas/jobs` and `/ediabas/vehicle` (see there) | `null` |
| 504 | `job_timeout` | the job was still busy at its timeout; `apiBreak` was called | `{ timeoutSeconds }` |


**Settled at the desk (2026-11-21, cable unplugged):** loading a real SGBD (`NVE_65`, even for
`_JOBS`) with no cable in the VM fails with error 28, `IFH-0018: INITIALIZATION ERROR`; `TMODE`
and `UTILITY` load without the interface. The agent reports error 28 as `cable_not_found`,
both as a request error and as a batch item error. Every other EDIABAS error stays
`ediabas_error`. Which code a busy port gives is not known; a BMW tool holding the cable is
caught by the conflict check before EDIABAS is called.

## `GET /health` additions
- `lastCableUseAt`: UTC time the runner last called `apiInit`, else `null`.
- `policyVersion`: the `version` of the policy file built into the agent (integer).
- `capabilities`: array of strings, the endpoint groups this build has:
  `"ediabas"`, `"capture"`, `"logs"`.
- `ediabas.loaded`: boolean, whether `api32.dll` is loaded right now (see "Idle release").
- `ediabas.stuck`: boolean, true once a job did not stop after `apiBreak` (see "A job that
  does not stop"). Stays true until the agent restarts.
- `approvals`: `{ "pinned": n, "unpinned": m }`, the number of distinct `reviewedJobs` entries
  of each form in force now (see "Approvals"). Entries of the wrong shape are not counted.

## Job policy
`policy/policy.json` is the single source. The agent embeds it at build time; the Mac server
reads the same file. Both evaluate it the same way and both run `policy/policy-cases.json`.
Both also load it the same way: a file with an unknown or duplicate key, a `version` below 1, an
empty name, no `descriptionScreen.terms`, or a missing or bad `descriptionScreen.ignore` (it must
be a list of non-empty lower-case strings, none listed twice; it may be empty) is invalid. So is
a file where an allow name, a deny substring or token, a restricted ECU key or one of its job
names is not upper-case; an `allow.patterns` entry has a backslash or a non-ASCII character;
`version` or `maxArgLength` is not written as a plain integer (`3.0` and `3e0` are refused); or
any text has a lone surrogate. Deny entries are compared as written. The agent then refuses every job
(`job_not_allowed`, rule `policy_unavailable`) and the Mac every `ediabas_*` tool. Both run
`policy/policy-parse-cases.json`, which lists policy texts each side must accept or refuse.

Evaluation, in this order. The first rule that decides wins; `rule` is the string returned.

1. **Shape.** `ecu` must fully match `namePattern`, else `shape.ecu`. `job` likewise, else
   `shape.job`. `args` and `results`, when present, must be at most `maxArgLength` characters,
   each in U+0020..U+007E, else `shape.args` / `shape.results`. "Fully match" means the whole
   string: a trailing newline does not pass.
2. Upper-case `ecu` and `job` (invariant culture). Everything below uses the upper-cased names.
3. **Deny.** For each entry of `deny.substrings`, in file order: if `job` contains it →
   `deny.substring:<entry>`. Then split `job` on `_`, dropping empty tokens; for each entry of
   `deny.tokens`, in file order: if it equals a token → `deny.token:<entry>`.
4. **Restricted ECU.** If `ecu` is a key of `allow.restrictedEcus`: allowed with `allow.ecu`
   if `job` is in that ECU's list, else `ecu_restricted`. Nothing else applies to that ECU.
5. `job` in `allow.globalExact` → `allow.global`.
6. `job` fully matches one of `allow.patterns` → `allow.pattern`, and the verdict is marked
   `screen: true`.
7. Otherwise `default_deny`.

The file is at `version` 3. Version 2 added `descriptionScreen.ignore`, which a version 1 file
does not have (so this build refuses it). Version 3:
- makes `E60` a restricted ECU like `TMODE` and `UTILITY`: only `INFO`, `IDENT_FUNKTIONAL`,
  `FS_LESEN_FUNKTIONAL` (whole-car ident and fault reads, KWP $1A and $18) and the `_` info jobs;
- allows `C_FA_LESEN` (vehicle order, KWP $22) by name on every ECU that is not restricted;
- changes the screen term `programm` to `program` and adds `einstell`, `verstell`, `aender`,
  `änder` and `adjust`. `steuer` and `control` are deliberately not terms: they match
  "Steuergerät" and "control unit". More `STATUS_` jobs need review than under version 2.

None of the version 3 additions has been run at the car yet. `GET /health` reports the agent's
version as `policyVersion`; `vm_health` warns when it differs from the Mac's.

**Description screen (agent only).** For a verdict with `screen: true`, the agent reads the
job's description (`_JOBCOMMENTS`) and refuses with `job_needs_review` if:
- the description is empty or whitespace; or
- after every case-insensitive occurrence of a `descriptionScreen.ignore` entry is replaced by
  one space (a space, so that removing it cannot join the text around it into a term), it
  contains, case-insensitively, any entry of `descriptionScreen.terms`. `matched` lists the
  terms found in that text; or
- nothing else is left: once every whitespace-separated word that contains an ignore entry is
  set aside, no letter or digit remains (a description that is only `Jobbeschreibung:` is as
  good as none). `matched` is then `[]`.

The ignore entries are labels seen in real descriptions that only look like a term:
`beschreibung` (from `Jobbeschreibung:`, which starts every CAS description and contains
`schreib`) and `umsetzer` (`AD-Umsetzers`, an A/D converter, contains `setz`). Nothing broader
is ignored: `EEPROM beschreiben` still fails on `schreib`, `Zaehler zuruecksetzen` on `setz`, and
`Beschreibungschreiben` on `schreib`. A job approved in the agent config's `reviewedJobs` skips
the screen, never the name rules (see "Approvals").

**Argument screen (agent only).** A job allowed only by the pattern, not approved in
`reviewedJobs`, and called with non-empty `args` gets one more call after its description
passed: `_ARGUMENTS` for that job. Every `ARG` name and `ARGCOMMENT` goes through the same
screen. It fails with `job_needs_review` and one of these messages:
- "The argument description of {JOB} mentions: {terms}. The user can approve it in the agent config."
- "{JOB} takes arguments but they are not described, so the call cannot be shown to be a read.
  The user can approve it in the agent config." (an EDIABAS error on `_ARGUMENTS`, or no rows)

`details.description` then holds the argument text. Call order: `_JOBCOMMENTS`, `_ARGUMENTS`,
the job.

## Approvals (`reviewedJobs`)
`reviewedJobs` in the agent config holds the owner's approvals of single jobs that the description
screen refuses. Two entry forms:
- `"ECU:JOB"` approves the job by name, whatever its description says.
- `"ECU:JOB@hhhhhhhhhhhh"` (12 lower-case hex digits) approves it only while its description
  is unchanged. This pinned form is the recommended one.

ECU and JOB are matched exactly, upper case, with the ECU name as it is asked for. With both
forms for one job the pinned one wins; with several pins any matching one approves. An entry of
any other shape is a warning in `agent.log` and approves nothing; it does not stop the agent.
An approval only ever applies to a job the policy allows by the `STATUS_` pattern.

The hash is the first 12 hex digits (lower case) of the SHA-256 of the UTF-8 bytes of the
description, trimmed: every value of the `_JOBCOMMENTS` sets, one per line, joined by `\n`.
`describe` with `job` returns it as `descriptionHash`.

For a pinned job the agent reads `_JOBCOMMENTS`, then runs the job; there is no `_ARGUMENTS`
call. An unpinned approval reads nothing. A pin that no longer holds is `job_needs_review`:
- "The description of {JOB} has changed since it was approved (now {hash}). The user can approve
  it again in the agent config."
- "{JOB} has no description now, so its approval cannot be checked. The user can approve it again
  in the agent config."
- "The description of {JOB} could not be read, so its approval cannot be checked."

**No restart.** At the start of every EDIABAS request the agent checks config.json's last-write
time. When it has changed, it re-reads `reviewedJobs` and nothing else. A file that is missing,
unreadable or not a valid config keeps the previous list and logs one warning per change; a
later good save is picked up.

## EDIABAS endpoints
Every one of these runs the conflict check first and returns `cable_in_use` if it fails,
including the ones that only read SGBD files. One `apiInit` … `apiEnd` cycle per request; no
handle outlives the request. Requests are served one at a time.

**Time in the queue counts.** A request's `maxJobTimeoutSeconds` budget starts when it arrives,
so time spent waiting behind another EDIABAS request counts against it. `durationMs` still
covers only the run. A request that waited the whole budget gets `not_run` and EDIABAS is not
loaded for it.

**Job not in the SGBD.** When a job from `/ediabas/job`, a `/ediabas/jobs` item or a
`/ediabas/vehicle` item fails with `ediabas_error`, the agent runs `_JOBS` on that SGBD. If the
list does not have the job, the answer is `job_not_found` instead. Only for a `.prg` file: for a
`.grp` name, a job starting with `_`, or a failed `_JOBS`, the original error stays.

**apiEnd failure.** If `apiEnd` fails after a job ran, the client still gets the result. The
agent logs it and does not release `api32.dll` again until it is restarted.

**Conflict check during a request.** The check runs again before every EDIABAS job the agent
sends within a request: each batch item, the description screen's `_JOBCOMMENTS`, the argument
screen's `_ARGUMENTS`, the job itself and a follow-up `_JOBS`, and each of describe's `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS` and `_RESULTS`. A check
less than 250 ms old is reused (the process scan is not free, and describe on a large SGBD sends
hundreds of short jobs); each batch item after the first always scans afresh. Once a BMW tool is
seen, no further job starts in that request:
- `/ediabas/jobs` and `/ediabas/vehicle`: the item and every later item get `not_run`; items
  already done are kept.
- describe without `job`: the job list is kept, comments already read are kept, and the rest are
  `not_checked`. If the tool appears before `_JOBS` runs, the request is `cable_in_use`.
- `/ediabas/job` and describe with `job`: the whole request is `cable_in_use`, with the tools in
  `details.processes`.

**Idle release.** `apiEnd` alone leaves EDIABAS's host process `ebas32.exe` running for as long
as the agent has `api32.dll` loaded (seen at the desk). So the agent unloads the DLL once no
EDIABAS request has arrived for `ediabasIdleReleaseSeconds` (agent config, default 20, range
5..600), and loads it again on the next request. The first request after a release is slow
(about 10 s in this VM). `/health` reports `ediabas.loaded` (boolean): whether the DLL is
loaded right now.

**A job that does not stop.** A job still busy at its timeout (or when the client gives up) is
stopped with `apiBreak`. If `apiState` still says busy 2 s later, the agent starts no further
job in that request and from then on leaves EDIABAS alone until it is restarted: no `apiEnd` on
that handle (it could block for good), no idle release of `api32.dll` (it could unload code
under the running job), and every later request that would use EDIABAS gets 503
`ediabas_unavailable` with `reason` "an earlier job did not stop after apiBreak; the agent must
be restarted". `/health` reports `ediabas.stuck: true`. Restarting Windows in the VM restarts
the agent.

### `GET /ediabas/ecus?filter=<substr>`
File listing of the Ecu folder; does not call EDIABAS. `filter` is a case-insensitive
substring of the name, optional.

```json
{ "ecuPath": "C:\\EDIABAS\\ECU", "count": 2, "truncated": false,
  "files": [ { "name": "NVE_65", "type": "prg" }, { "name": "D_MOTOR", "type": "grp" } ] }
```
Sorted by name. At most 5000 entries; `truncated` says whether more matched.

### `GET /ediabas/describe?ecu=<name>[&job=<name>][&filter=<substr>][&comments=true|false]`
Reads the SGBD file through EDIABAS (`_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`, `_RESULTS`).

Without `job` — the job list. `comments` defaults to `true`; `filter` narrows by name substring.
```json
{ "ecu": "NVE_65", "variant": "NVE_65", "jobCount": 2,
  "jobs": [
    { "name": "FS_LESEN", "comment": "Fehlerspeicher lesen",
      "verdict": { "allowed": true, "rule": "allow.global", "review": "not_required" } },
    { "name": "STEUERN_X", "comment": "…",
      "verdict": { "allowed": false, "rule": "deny.substring:STEUER", "review": "not_required" } } ],
  "durationMs": 412 }
```
`review` is `not_required` (no screen applies), `passed`, `needs_review`, `reviewed` (an
unpinned `reviewedJobs` entry, or a pinned one that matches the description), or `not_checked`
(`comments=false`, or the comment was not fetched because the
request's time budget ran out, an earlier job did not stop after `apiBreak`, or a BMW tool
started during the request). A pin that does not match gives `needs_review`; a pin with
`comments=false` gives `not_checked`. Describe gives the same answer a run would. `comment` is `null` when not fetched or
absent. A job is runnable only when `allowed` is true and `review` is `not_required`,
`passed` or `reviewed`.

With `job` — one job. The job name passes shape rules only; describing a denied job is allowed.
```json
{ "ecu": "NVE_65", "variant": "NVE_65", "job": "FS_LESEN", "comment": "…",
  "descriptionHash": "3f1c0a9b2d4e",
  "verdict": { "allowed": true, "rule": "allow.global", "review": "not_required" },
  "arguments": [ { "name": "…", "type": "…", "comment": "…" } ],
  "results": [ { "name": "F_ORT_NR", "type": "long", "comment": "…" } ],
  "durationMs": 95 }
```
`descriptionHash` is the hash a pinned approval holds (see "Approvals"); `null` when the
description is empty or could not be read.

### `POST /ediabas/job`
Body `{ "ecu": "NVE_65", "job": "FS_LESEN", "args": "", "results": "", "timeoutSeconds": 30 }`.
`args`, `results` and `timeoutSeconds` are optional. `timeoutSeconds` defaults to the config's
`jobTimeoutSeconds` and is clamped to `maxJobTimeoutSeconds`. The agent refuses to start with a
`maxJobTimeoutSeconds` above 60: a request can last that long plus the 2 s `apiBreak` grace, and
the Mac stops waiting for an EDIABAS call after 75 s.

200:
```json
{ "ecu": "NVE_65", "job": "FS_LESEN", "variant": "NVE_65", "jobStatus": "OKAY",
  "systemResults": { "VARIANTE": "NVE_65", "SAETZE": 1 },
  "sets": [ { "F_ORT_NR": 42834, "F_ORT_TEXT": "…" } ],
  "durationMs": 812 }
```
- `ecu` and `job` are the upper-cased request names. `variant` is the system result
  `VARIANTE` (the SGBD a group name resolved to), else `null`.
- `systemResults` is result set 0. `sets` are sets 1..n in order.
- `jobStatus` is the `JOB_STATUS` result of the first set that has one, else `null`.
- Values: text formats as strings; char, byte, integer, word, long, dword and real as JSON
  numbers; binary as an upper-case hex string with no separators. A result that cannot be read
  is `null`.
- A job that ran but reports `JOB_STATUS` other than `OKAY` is still a 200.

### `POST /ediabas/jobs`
Body `{ "items": [ { "ecu", "job", "args"?, "results"? } ], "timeoutSeconds"?: 30 }`, 1 to 20
items. `timeoutSeconds` is per item. An item gets `not_run` (with `details: null` and a message
saying why) when it was not started, or was stopped, because:
- the request's `maxJobTimeoutSeconds` budget ran out;
- a BMW tool started during the request (see "Conflict check during a request");
- an earlier item's job did not stop after `apiBreak` (see "A job that does not stop");
- the client gave up on the request (a running job is stopped with `apiBreak`).

200:
```json
{ "items": [
    { "ok": true,  "result": { "ecu": "…", "job": "…", "variant": "…", "jobStatus": "OKAY", "systemResults": {}, "sets": [], "durationMs": 300 }, "error": null },
    { "ok": false, "result": null, "error": { "code": "job_not_allowed", "message": "…", "details": { } } } ],
  "durationMs": 1290 }
```
Items come back in request order. Each item is policy-checked and screened on its own; one
failure does not stop the rest. Per-item error codes: `job_not_allowed`, `job_needs_review`,
`ecu_not_found`, `job_not_found`, `cable_not_found`, `ediabas_error`, `job_timeout`, `not_run`. Whole-request failures
(`bad_request`, `cable_in_use`, `ediabas_unavailable`) are HTTP errors as above.

### `GET /ediabas/vehicle`
Runs, in one cycle and through the same policy as any other job: `UTILITY STATUS_ZUENDUNG`,
`UTILITY STATUS_UBATT`, every `voltageSources` entry, `TMODE LESE_SPANNUNG_KL30` and
`TMODE LESE_SPANNUNG_KL15`. Returns each as an item of the `/ediabas/jobs` shape; the agent
does not interpret result names (they are confirmed at the car in Phase 6). `voltageSources`
in the agent config is empty by default, so `voltages` is `[]`; the example below shows one
entry an E60/E61 owner might add (the night-vision module's supply voltage).

```json
{ "ignition": { "ok": true, "result": { }, "error": null },
  "ubatt": { "ok": true, "result": { }, "error": null },
  "voltages": [ { "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG", "ok": true, "result": { }, "error": null } ],
  "cableKl30": { "ok": false, "result": null, "error": { } },
  "cableKl15": { "ok": false, "result": null, "error": { } },
  "durationMs": 2100 }
```

## `GET /screen/capture?window=<handle|title-substring|desktop>&maxEdge=1600&format=jpeg|png`
- `window` defaults to `desktop` (the primary screen). A value of the form `0x` + hex digits
  is a window handle. Anything else is a case-insensitive title substring; the front-most
  visible match wins.
- `maxEdge` 200..4000, default 1600: the image is scaled down so its longer edge is at most
  this. Never scaled up. `format` defaults to `jpeg` (quality 85).
- 200: the image bytes, `Content-Type: image/jpeg` or `image/png`, with headers
  `X-Window-Title` (UTF-8, percent-encoded; `desktop` for the screen), `X-Window-Handle`
  (`desktop` for the screen),
  `X-Capture-Method` (`printwindow`, `bitblt` or `screen`), `X-Image-Width`, `X-Image-Height`.
- Errors: `bad_request`, `window_not_found`, `window_minimized`, `desktop_unavailable` (only a
  locked screen, the secure desktop or session 0), `capture_busy` (another capture is still
  running; nothing was captured), `capture_failed` (the copy failed or the screen size could not
  be read).
- The agent never restores, focuses, moves or resizes a window.

## Logs
Read-only, under the config's `logRoots` only. `/logs` and `/logs/read` use the same roots: a
root whose resolved path is a share, a bare drive or not a plain path, or that is not on a local
fixed disk, shows `exists: false` in `/logs` and is never walked or read. A `logRoots` entry the
path rules refuse is logged in `agent.log` at start-up as not used, with the reason.

### `GET /logs?root=<index>&glob=<pattern>&since=<iso8601>&limit=50`
All parameters optional. Without `root`, every root is listed. `glob` matches the file name
with `*` and `?` only, case-insensitively. `limit` 1..200. Subfolders are searched to a depth
of 3; reparse points are not followed.

```json
{ "roots": [ { "index": 0, "path": "C:\\EDIABAS\\TRACE", "exists": true } ],
  "files": [ { "path": "C:\\EDIABAS\\TRACE\\api.trc", "root": 0, "sizeBytes": 20480, "modifiedAt": "2026-11-21T16:40:02Z" } ],
  "truncated": false }
```
Newest first.

### `GET /logs/read?path=<absolute>[&tailBytes=65536][&offset=<n>&length=<n>][&grep=<regex>]`
- Either `tailBytes` (default 65536) or `offset` + `length`. At most 262144 bytes per call.
- Decoded as UTF-8 if valid, else Windows-1252. Opened so that a tool still writing the file
  is not disturbed (`FileShare.ReadWrite | Delete`).
- `grep`: at most 200 characters, compiled with `RegexOptions.NonBacktracking`; only matching
  lines of the selected range are returned. A bad pattern is `bad_request`. One 5 s limit covers
  the whole request's grep; past it the answer is `bad_request` "grep took too long; use a
  simpler pattern or a smaller range."
- A path outside the roots is always `path_not_allowed`, whether or not the file exists. A
  file under a root that exists but cannot be opened is 500 `internal_error`. Files with more
  than one hard link and paths with forward slashes are refused.
- Two checks, both required. First, before anything is opened, the path as written must lie
  under a log root, as configured or where that root resolves: a path on another drive (a
  mapped network drive too), a link elsewhere that points into a root, or a short-name
  spelling of the root itself (`C:\EDIABA~1\TRACE\…`) is refused as `outside_roots` without
  being touched. Then, on the opened handle, the file must resolve, after following links, to a
  file under a log root. A root configured on, or leading to, a drive that is not a local fixed
  disk (network, removable, optical) grants nothing and is never opened (`/logs` shows it with
  `exists: false`). Refused with `path_not_allowed`: `..` segments, relative paths, UNC and
  device paths (`\\?\`, `\\.\`), alternate data streams, DOS device names (`CON`, `NUL`,
  `COM1`, …), junction or symlink escapes, and 8.3 short names that resolve outside the roots.

```json
{ "path": "C:\\EDIABAS\\TRACE\\api.trc", "sizeBytes": 20480, "modifiedAt": "2026-11-21T16:40:02Z",
  "offset": 0, "length": 20480, "encoding": "utf-8", "truncated": false,
  "grep": null, "text": "…" }
```
`truncated` is true when the range was cut to the cap or when more of the file lies before
(`tailBytes`) or after (`offset`) the returned range. With `grep`, `grep` is
`{ "pattern": "…", "matchedLines": 12 }`.

## Audit
One line per request as before, now also carrying `ecu`, `job`, for refusals the policy
`rule` (or the path refusal reason), and `target` (the log path or window asked for). Batch
requests write one line per item plus one for the request. Control characters and Unicode
format characters (U+202E, U+200B, U+2066 …) in a field are replaced with `_`.

## `ediabasd` stdio (native engine)

`ediabasd` (`engine/ediabasd-net`, stage 1 of the native engine) runs the same service as the
VM agent's `/ediabas/*` endpoints, on EdiabasLib and the cable layer `libkline` instead of
`api32.dll` in a VM. It has no listener: the MCP server starts `ediabasd serve` as a child
process and speaks JSON lines on its stdin and stdout, one request at a time. A job is never
resent. Log lines go to stderr.

Request, one per line: `{"id": <any JSON value>, "method": "<name>", "params": {…}}`.
Response, one per line: `{"id": <the same>, "ok": true, "result": {…}}` or
`{"id": <the same>, "ok": false, "error": {"code", "message", "details"}}`. A line that is not a
request object gets `bad_request` with `"id": null`; the server carries on.

| `method` | `params` | `result` |
|---|---|---|
| `ecus` | `{ "filter"? }` | as `GET /ediabas/ecus` |
| `describe` | `{ "ecu", "job"?, "filter"?, "comments"?: bool }` | as `GET /ediabas/describe` |
| `job` | the body of `POST /ediabas/job` | as `POST /ediabas/job` |
| `jobs` | the body of `POST /ediabas/jobs` | as `POST /ediabas/jobs` |
| `vehicle` | `{}` | as `GET /ediabas/vehicle` |
| `health` | `{}` | `{ "engine": "ediabasd-net", "version", "kline", "ecuPath", "cable": { "state": "absent" \| "present" \| "several" \| "unknown", "port" }, "policyVersion" }` |

Unknown params are `bad_request`. `health` never opens the cable: it reads USB identities only.

**What differs from the VM.**
- **Describe never touches the cable.** `_` jobs (`_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS`,
  `_RESULTS`, …) run on an offline EdiabasLib instance that does not run `INITIALISIERUNG`.
  Real EDIABAS runs it first even for `_JOBS`; for an SGBD whose INIT sends a telegram, the
  native describe therefore works without the car where the VM's does not. Deliberate.
- **Group files** (`.grp`): describe uses the variant the group last resolved to at the car,
  cached in `$XDG_STATE_HOME/bimmer-bridge/variants.json` and keyed by the `.grp` file's hash.
  With no entry it fails with `needs_car`. A `job`, `jobs` or `vehicle` request on a group
  resolves it at the car (EdiabasLib runs its `IDENTIFIKATION`, as EDIABAS does) and stores it.
- No process conflict check: the cable's lock (`flock` and exclusive open) does that job, and a
  cable held by another program is `cable_in_use`.

**Errors only the native engine gives** (the reason comes from `libkline`):

| `code` | When |
|---|---|
| `cable_unsupported` | the serial device is not an FTDI FT232R (USB `0403:6001`) |
| `cable_permission` | the device cannot be opened for lack of permission (on Linux, the udev rule) |
| `frame_refused` | the cable layer's frame gate refused a telegram that could write to the car or reconfigure the cable. The whole request fails, whatever the SGBD made of the send error, and nothing more is sent on that port until it closes |
| `needs_car` | a describe on a group file whose variant is not cached yet |

An `ediabas_error` for IFH-0003 with no cable-layer reason says to check the cable switch is on
K-line. In `details`, `errorCode` is EDIABAS's code, or one of 9001–9007 when the cable layer
gave the reason (`no_device`, `locked`, `not_ftdi`, `permission`, `gate_refused`, a group with
no cached variant, an echo mismatch).

**A job that does not stop.** If a job is still busy 2 s after it was broken off, `ediabasd`
writes that request's response and exits with code 3; the next request starts a fresh one.

**Config** (`$XDG_CONFIG_HOME/bimmer-bridge/config.json`, default `~/.config/…`): `ecuPath`
(required), `port` (optional when exactly one cable is present), `jobTimeoutSeconds` (30),
`maxJobTimeoutSeconds` (60, at most 60), `voltageSources`, `reviewedJobs` (read at start),
`traceDir`, `recordDir`. Unknown keys are errors. The audit log is
`$XDG_STATE_HOME/bimmer-bridge/audit.log` (default `~/.local/state/…`), in the format below,
with `stdio` or `cli` as the remote address and the method as the request.

The same methods run from a shell: `ediabasd ecus [FILTER]`, `ediabasd describe ECU [JOB]
[--filter X] [--no-comments]`, `ediabasd job ECU JOB [--args A] [--results R] [--timeout N]`,
`ediabasd vehicle`, `ediabasd health`, each with `--config PATH`. Exit 0 ok, 1 error response,
2 usage, 3 stuck, 78 config error.
