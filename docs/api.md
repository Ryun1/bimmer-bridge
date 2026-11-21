# Agent HTTP API — contract

The contract between `vm-agent` (server) and `mac-mcp` (client). Both sides are built against
this file. Change it here first.

Covers what exists after Phases 1 and 3. Later phases add endpoints.

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
  "version": "0.1.0",
  "startedAt": "2026-11-21T15:04:05Z",
  "uptimeSeconds": 512,
  "os": {
    "description": "Microsoft Windows 10.0.26100",
    "osArchitecture": "Arm64",
    "processArchitecture": "X86"
  },
  "user": "ryan",
  "elevated": false,
  "sessionId": 1,
  "bindAddress": "192.168.64.3",
  "port": 8765,
  "gateway": "192.168.64.1",
  "conflictingProcesses": [ { "name": "INPALOAD", "pid": 4312 } ],
  "ediabas": { "dllPath": "C:\\EDIABAS\\BIN\\api32.dll", "dllFound": true },
  "lastCableUseAt": null
}
```

- `agent` is always the literal `ediabas-bridge-agent`. Clients use it to confirm they
  reached the right service.
- `conflictingProcesses` lists running processes whose name matches the configured conflict
  list (case-insensitive, without `.exe`). Empty array when none.
- `sessionId` is the Windows session the agent runs in. `0` means it is not in an
  interactive session and screenshots will not work.
- `ediabas.dllFound` is a file-existence check only.
- `lastCableUseAt` is `null` until Phase 2.

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
| 403 | `job_needs_review` | a job allowed by the `STATUS_` pattern has a description that fails the screen, or has no description | `{ ecu, job, description, matched: ["start"] }` (`matched` is `[]` when the description is empty) |
| 403 | `path_not_allowed` | log path outside the configured roots, or an escape attempt | `{ reason }` |
| 404 | `ecu_not_found` | no `.prg` or `.grp` of that name in the Ecu folder | `{ ecu }` |
| 404 | `job_not_found` | `describe` was asked for a job the SGBD does not have | `{ ecu, job }` |
| 404 | `window_not_found` | no visible window matches | `null` |
| 404 | `file_not_found` | log file does not exist | `null` |
| 409 | `cable_in_use` | a BMW tool or another `api32.dll` user is running. No override | `{ processes: [ { name, pid } ] }` |
| 409 | `window_minimized` | the window is minimised; the agent never restores it | `{ handle, title }` |
| 502 | `cable_not_found` | EDIABAS could not initialise the interface: error 28, `IFH-0018: INITIALIZATION ERROR` | `{ errorCode: 28, errorText }` |
| 502 | `ediabas_error` | EDIABAS reported an error for the job | `{ errorCode: 159, errorText: "IFH-0009: …" }` |
| 503 | `ediabas_unavailable` | `api32.dll` missing, an expected export missing, `apiInit` failed, or an earlier job did not stop after `apiBreak` (the agent must be restarted) | `{ reason }` |
| 503 | `desktop_unavailable` | locked screen, secure desktop, or session 0 | `null` |
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

## Job policy
`policy/policy.json` is the single source. The agent embeds it at build time; the Mac server
reads the same file. Both evaluate it the same way and both run `policy/policy-cases.json`.

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

**Description screen (agent only).** For a verdict with `screen: true`, the agent reads the
job's description (`_JOBCOMMENTS`) and refuses with `job_needs_review` if the description is
empty or contains, case-insensitively, any entry of `descriptionScreen.terms`. A job listed as
`ECU:JOB` (upper-case) in the agent config's `reviewedJobs` skips the screen, never the name
rules.

## EDIABAS endpoints
Every one of these runs the conflict check first and returns `cable_in_use` if it fails,
including the ones that only read SGBD files. One `apiInit` … `apiEnd` cycle per request; no
handle outlives the request. Requests are served one at a time.

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
`review` is `not_required` (no screen applies), `passed`, `needs_review`, `reviewed` (listed in
`reviewedJobs`), or `not_checked` (`comments=false`). `comment` is `null` when not fetched or
absent. A job is runnable only when `allowed` is true and `review` is `not_required`,
`passed` or `reviewed`.

With `job` — one job. The job name passes shape rules only; describing a denied job is allowed.
```json
{ "ecu": "NVE_65", "variant": "NVE_65", "job": "FS_LESEN", "comment": "…",
  "verdict": { "allowed": true, "rule": "allow.global", "review": "not_required" },
  "arguments": [ { "name": "…", "type": "…", "comment": "…" } ],
  "results": [ { "name": "F_ORT_NR", "type": "long", "comment": "…" } ],
  "durationMs": 95 }
```

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
items. `timeoutSeconds` is per item. The whole request stops starting new items once
`maxJobTimeoutSeconds` has elapsed; items not started get `not_run`.

200:
```json
{ "items": [
    { "ok": true,  "result": { "ecu": "…", "job": "…", "variant": "…", "jobStatus": "OKAY", "systemResults": {}, "sets": [], "durationMs": 300 }, "error": null },
    { "ok": false, "result": null, "error": { "code": "job_not_allowed", "message": "…", "details": { } } } ],
  "durationMs": 1290 }
```
Items come back in request order. Each item is policy-checked and screened on its own; one
failure does not stop the rest. Per-item error codes: `job_not_allowed`, `job_needs_review`,
`ecu_not_found`, `cable_not_found`, `ediabas_error`, `job_timeout`, `not_run`. Whole-request failures
(`bad_request`, `cable_in_use`, `ediabas_unavailable`) are HTTP errors as above.

### `GET /ediabas/vehicle`
Runs, in one cycle and through the same policy as any other job: `UTILITY STATUS_ZUENDUNG`,
`UTILITY STATUS_UBATT`, every `voltageSources` entry, `TMODE LESE_SPANNUNG_KL30` and
`TMODE LESE_SPANNUNG_KL15`. Returns each as an item of the `/ediabas/jobs` shape; the agent
does not interpret result names (they are confirmed at the car in Phase 6).

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
- Errors: `bad_request`, `window_not_found`, `window_minimized`, `desktop_unavailable`.
- The agent never restores, focuses, moves or resizes a window.

## Logs
Read-only, under the config's `logRoots` only.

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
  lines of the selected range are returned. A bad pattern is `bad_request`.
- A path outside the roots is always `path_not_allowed`, whether or not the file exists. A
  file under a root that exists but cannot be opened is 500 `internal_error`. Files with more
  than one hard link and paths with forward slashes are refused.
- The path must resolve, after following links, to a file under a log root. Refused with
  `path_not_allowed`: `..` segments, relative paths, UNC and device paths (`\\?\`, `\\.\`),
  alternate data streams, DOS device names (`CON`, `NUL`, `COM1`, …), junction or symlink
  escapes, and 8.3 short names that resolve outside the roots.

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
requests write one line per item plus one for the request.
