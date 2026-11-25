# Agent HTTP API — contract

The contract between `vm-agent` (server) and `mac-mcp` (client). Both sides are built against
this file. Change it here first.

The first part covers `/health` and `/windows` (Phases 1 and 3) and `/hello` (added in 0.5.0). The part from
"Phases 2, 4, 5 and 6" on adds the error details, the `/health` additions, the job policy and the EDIABAS,
screenshot and log endpoints.

## Transport
- Plain HTTP, port **8765**, on the VM's shared-network IPv4 address only.
- Every request, including `/health`, needs `Authorization: Bearer <token>`, except
  [`GET /hello`](#get-hello), the one unauthenticated route.
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
| 401 | `unauthorized` | header missing, malformed, or token wrong. Sent before any routing, so an unauthenticated caller cannot tell which paths exist (apart from `GET /hello`) |
| 404 | `not_found` | unknown path (authenticated callers only) |
| 405 | `method_not_allowed` | known path, wrong method |
| 500 | `internal_error` | unhandled failure; the message is generic, details go to the agent log |

A 401 also carries `WWW-Authenticate: Bearer`.

Order of checks: source address (403), token (401), path (404), method (405). `GET /hello`
skips the token step only, and only for that exact path and method: `/hello/`, `/hellox` and
`POST /hello` still need the token. Requests that
Kestrel rejects itself (malformed HTTP, oversized headers, HTTP/2) never reach these checks
and get Kestrel's own response.

## `GET /hello`
`GET /hello?nonce=<32 lowercase hex>` proves the agent holds the token without the caller
sending it (decision D5). It needs no token; an `Authorization` header sent with it is ignored.
The source-address check (403) still comes first, and the request is audited like any other.

```json
{ "agent": "ediabas-bridge-agent", "proof": "496e963110d8963be46b16878c70fbd5003960c525362880c4f7514a06a608bd" }
```

- `proof` is lowercase hex
  `HMAC-SHA256(key = SHA-256(UTF-8 token), message = UTF-8 "ediabas-bridge-hello-v1:" + nonce)`.
  The example is for token `test-token` and nonce `00112233445566778899aabbccddeeff`.
- `nonce` is required, given once, exactly 32 characters of `[0-9a-f]`; anything else
  (missing, 31 or 33 characters, upper case, another parameter) is 400 `bad_request` with
  `details.parameter`.
- A client checks the proof (in constant time, with a fresh nonce) before it sends the token to
  an address, so the token never goes to something that is not this agent.
- Why it is safe without a token: only the VM's gateway (the Mac) reaches it (firewall and
  source check), it touches neither the cable nor EDIABAS, and the proof reveals nothing usable
  about the token. It is an HMAC keyed with the token's SHA-256, bound to a fixed context string
  and to the caller's nonce; it cannot be turned back into the token or its hash, cannot be
  presented as a Bearer token, and a proof for one nonce says nothing about another. The token
  is 32 random bytes, so it cannot be guessed offline from a proof either.

## `GET /health`
Never touches the cable and never loads the EDIABAS DLL.

```json
{
  "agent": "ediabas-bridge-agent",
  "version": "0.6.2",
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
  "approvals": { "pinned": 1, "ignored": 0, "entries": ["NVE_65:STATUS_EXAMPLE@3f1c0a9b2d4e"], "hash": "0123456789ab" }
}
```

- `agent` is always the literal `ediabas-bridge-agent`. Clients use it to confirm they
  reached the right service.
- `conflictingProcesses` lists running processes whose name matches the configured conflict
  list (case-insensitive, without `.exe`). Empty array when none.
- `sessionId` is the Windows session the agent runs in. `0` means it is not in an
  interactive session and screenshots will not work.
- `ediabas.dllFound` is a file-existence check only.
- `lastCableUseAt`, `policyVersion`, `policySha256`, `capabilities`, `ediabas.loaded`, `ediabas.stuck`, `ediabas.heldOpen` and
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
| 404 | `job_not_found` | `describe` was asked for a job the SGBD does not have. A run of a job the SGBD lacks gives `ediabas_error`. Message: "{ECU} has no job {JOB}." | `{ ecu, job }` |
| 404 | `window_not_found` | no visible window matches | `null` |
| 404 | `file_not_found` | log file does not exist | `null` |
| 409 | `cable_in_use` | a BMW tool or another `api32.dll` user is running. No override | `{ processes: [ { name, pid } ] }` |
| 409 | `window_minimized` | the window is minimised; the agent never restores it | `{ handle, title }` |
| 502 | `cable_not_found` | EDIABAS could not initialise the interface: error 28, `IFH-0018: INITIALIZATION ERROR` | `{ errorCode: 28, errorText }` |
| 502 | `ediabas_error` | EDIABAS reported an error for the job; or the job finished but its results could not be read (message "The job finished but its results could not be read.", with whatever EDIABAS reported in `details` and `resultsUnread: true` there) | `{ errorCode: 19, errorText: "IFH-0009: …" }`; plus `resultsUnread: true` only in the unread-results case |
| 503 | `ediabas_unavailable` | `api32.dll` missing, an expected export missing, `apiInit` failed, or an earlier job did not stop after `apiBreak` (the agent must be restarted), or the agent's EDIABAS thread did not answer within `maxJobTimeoutSeconds` + 30 s (its watchdog restarts it if it stays stuck) | `{ reason }` |
| 500 | `capture_failed` | the desktop was available but the copy failed or the screen size could not be read | `null` |
| 503 | `desktop_unavailable` | only a locked screen, the secure desktop, or session 0 | `null` |
| 503 | `capture_busy` | another capture is still running; nothing was captured. Try again | `null` |
| 503 | `not_run` | `/ediabas/job` or describe started no job, or stopped one: the request's `maxJobTimeoutSeconds` budget ran out before the job (or the screen before it) could start, or the client gave up (the running job was stopped with `apiBreak`). The message says which, e.g. "The request was abandoned before {JOB} started." A request that spent the whole budget queued behind another EDIABAS request gets "The request waited behind another EDIABAS request for the whole {max} s limit; nothing was started." on `/ediabas/job`, `/ediabas/jobs` and both describe modes, and EDIABAS is not loaded for it. Also a per-item code in `/ediabas/jobs` (see there) | `null` |
| 504 | `job_timeout` | the job was still busy at its timeout; `apiBreak` was called | `{ timeoutSeconds }` |
| 403 | `telegram_not_allowed` | the telegram check (see "Telegram check (agent)") found, in what the request added to the IFH trace, a telegram the telegram rule refuses, an interface function it does not know, or a line it cannot read; or the check latched on an earlier request (every `/ediabas/*` request, `/ediabas/ecus` included, until the agent restarts) | `{ function, concept, frame, reason }`: the interface function (`SENDECUTEL`), the concept as four hex digits, the telegram as hex bytes (`"83 12 F1 14 FF FF"`), and the rule's reason (`service`, `malformed`, …) or `unknown_function` / `unreadable_trace` / `trace_too_large` / `telegram_preface`; any of the first three is `null` when it does not apply |
| 503 | `telegram_check_unavailable` | under `enforce`: before anything is sent, `EDIABAS.INI` is missing or unreadable, its `IfhTrace` is below 3, its `TracePath` is not a full path, or the telegram rule did not load; or, after a request that ran a job, EDIABAS wrote no trace for it or the trace could not be read (this latches like `telegram_not_allowed`, and later requests get this code) | `{ reason }` |


**Settled at the desk (2026-11-21, cable unplugged):** loading a real SGBD (`NVE_65`, even for
`_JOBS`) with no cable in the VM fails with error 28, `IFH-0018: INITIALIZATION ERROR`; `TMODE`
and `UTILITY` load without the interface. The agent reports error 28 as `cable_not_found`,
both as a request error and as a batch item error. Every other EDIABAS error stays
`ediabas_error`. Which code a busy port gives is not known; a BMW tool holding the cable is
caught by the conflict check before EDIABAS is called.

## `GET /health` additions
- `lastCableUseAt`: UTC time the runner last called `apiInit`, else `null`.
- `policyVersion`: the `version` of the policy file built into the agent (integer).
- `policySha256`: lower-case hex SHA-256 (64 characters) of the exact bytes of the policy file
  built into the agent; `null` only if the policy did not load. A 0.4.x agent omits it. Before
  every `ediabas_*` call the Mac compares it with the SHA-256 of the file it loaded; if both are
  present and differ, it refuses with `policy_mismatch` and sends no `/ediabas/*` request.
- `telegramsVersion`, `telegramsSha256`: the same for the telegram rule built into the agent
  (`policy/telegrams.json`); both `null` if it did not load. The Mac compares the SHA-256 with its
  own copy of the file and `vm_health` warns when they differ; it refuses nothing for it (the Mac
  never sees telegrams). An agent before the telegram check omits them.
- `telegramCheck`: `{ "mode": "enforce" | "report" | "off", "latch": null | { "at", "code",
  "reason", "function", "concept", "frame", "message" } }`. `latch` is set by the first finding
  under `enforce` and stays until the agent restarts: `at` is the UTC time, `code`
  `telegram_not_allowed` or `telegram_check_unavailable`, the rest as in that error's details,
  and `message` says what was found.
- `ediabas.interface`: the `ediabasInterface` the agent opens with `apiInitExt` (such as
  `"GATE:OBD"`), or `null` when it opens EDIABAS.INI's interface with `apiInit`.
  `ediabas.requireGate`: whether EDIABAS requests are refused unless that is a `GATE:` interface.
- `capabilities`: array of strings, the endpoint groups this build has:
  `"ediabas"`, `"capture"`, `"logs"`.
- `ediabas.loaded`: boolean, whether `api32.dll` is loaded right now (see "Idle release").
- `ediabas.stuck`: boolean, true once a job did not stop after `apiBreak` (see "A job that
  does not stop"). Stays true until the agent restarts.
- `ediabas.heldOpen`: boolean, true once `apiEnd` threw: EDIABAS stays loaded and the interface
  stays in use until the agent restarts. (`vm_health` says so.)
- `approvals`: `{ "pinned": n, "ignored": m, "entries": [...], "hash": "..." }`: the distinct
  `reviewedJobs` entries in force now: `/health` re-reads `config.json` before it answers, as
  every EDIABAS request does (see "Approvals"). `pinned` counts the pinned ones;
  `ignored` counts those that approve nothing (unpinned, of the wrong shape, or with an ECU or
  JOB that is not upper case). `entries` lists the pinned ones as written, sorted by ordinal
  string comparison; `hash` is the first 12 lower-case hex digits of the SHA-256 of the entries
  joined by `\n` (UTF-8; of the empty string when there are none). The Mac server remembers
  the hash and flags a change (docs/tools.md). An agent before 0.5.0 sends neither field. A 0.4.0 agent sent `{ "pinned": n, "unpinned": m }`; the Mac server
  still reads that.

## Job policy
`policy/policy.json` is the single source. The agent embeds it at build time; the Mac server
reads the same file. Both evaluate it the same way and both run `policy/policy-cases.json`.
Both also load it the same way: a file with an unknown or duplicate key, a `version` below 1, an
empty name, no `descriptionScreen.terms`, or a missing or bad `descriptionScreen.ignore` (it must
be a list of non-empty lower-case strings, none listed twice; it may be empty) is invalid. So is
a file with no `deny.ecuJobs` object, or where an allow name, a deny substring or token, a
restricted ECU key or one of its job names, or a `deny.ecuJobs` key or one of its job names, is
not upper-case; an `allow.patterns` entry has a backslash or a non-ASCII character;
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
   `deny.tokens`, in file order: if it equals a token → `deny.token:<entry>`. Then, if `ecu` is
   a key of `deny.ecuJobs` and `job` is in its list → `deny.ecuJob`.
4. **Restricted ECU.** If `ecu` is a key of `allow.restrictedEcus`: allowed with `allow.ecu`
   if `job` is in that ECU's list, else `ecu_restricted`. Nothing else applies to that ECU.
5. `job` in `allow.globalExact` → `allow.global`.
6. `job` fully matches one of `allow.patterns` → `allow.pattern`, and the verdict is marked
   `screen: true`.
7. Otherwise `default_deny`.

The file is at `version` 5. Version 5 adds `deny.ecuJobs` (required, may be `{}`): jobs refused
under one ECU name only, because their names pass (all are `STATUS_` jobs, most with
descriptions that say they read) but the SGBD sends a write service: `$2E`/`$3B` write data,
`$14` clear fault memory, or a non-default session followed by a `$3B` of code and a routine
(`DSC_E65`). The key is the name as the caller gives it, so each SGBD is listed together with
every group (`.grp`) that can resolve to it: EDIABAS runs a group name as the variant it
identifies (`D_DSC` runs `DSC_E65`). `dev/sgbd-sweep.ts` finds both by reading the SGBD and
group files ([policy/README.md](../policy/README.md)); its 2026-11-24 run over the VM's 1,251
SGBDs and 353 groups gave 18 jobs on 13 SGBDs, reachable through 13 groups. It also lists 11
jobs on the E61's own modules that actuate rather than read (`$30` with no report-current-state
byte in either layout, the `PGS_65_2` self-test and antenna test, and a non-default session in
`GS19B` `STATUS_RAUS`), with their groups; a reviewed exact telegram can bring one back once the
telegram gate exists ([spikes/2026-11-24-write-gate.md](spikes/2026-11-24-write-gate.md)). Version 2 added `descriptionScreen.ignore`, which a version 1 file
does not have (so this build refuses it). Version 4 makes `E65`, `E70`, `E89X`, `F01` and `R56`
(the chassis SGBDs present in the VM's Ecu folder) restricted ECUs with the same list as `E60`,
and adds the screen terms `schaltet`, `einschalt`, `ausschalt`, `umschalt`, `ausloes`, `auslös`,
`trigger`, `oeffnen` and `öffnen`; a job whose description now matches becomes `job_needs_review`
(none of the recorded describe fixtures changes verdict). Version 3:
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
`Beschreibungschreiben` on `schreib`. A job pinned in the agent config's `reviewedJobs` skips
this screen while its description is unchanged, never the name rules or the argument screen
(see "Approvals").

**Argument screen (agent only).** A job allowed only by the pattern and called with non-empty
`args` gets one more call after its description passed (or its pin matched): `_ARGUMENTS` for
that job. An approval does not skip it. Every `ARG` name and `ARGCOMMENT` goes through the same
screen. It fails with `job_needs_review` and one of these messages:
- "The argument description of {JOB} mentions: {terms}. The user can approve it in the agent config."
- "{JOB} takes arguments but they are not described, so the call cannot be shown to be a read.
  The user can approve it in the agent config." (an EDIABAS error on `_ARGUMENTS`, or no rows)

`details.description` then holds the argument text. Call order: `_JOBCOMMENTS`, `_ARGUMENTS`,
the job.

## Approvals (`reviewedJobs`)
`reviewedJobs` in the agent config holds the owner's approvals of single jobs that the description
screen refuses. One entry form: `"ECU:JOB@hhhhhhhhhhhh"` (12 lower-case hex digits) approves
the job only while its description is unchanged.

ECU and JOB are matched exactly, upper case, with the ECU name as it is asked for. With several
pins for one job any matching one approves. An unpinned `"ECU:JOB"` entry (0.4.0 and before)
approves nothing since 0.5.0: it is a warning in `agent.log` naming the pinned form
(`reviewedJobs[i]: "ECU:JOB" is unpinned and approves nothing since 0.5.0; run ediabas_describe
on the job and use the "ECU:JOB@<hash>" line it prints`). An entry of any other shape is also a
warning and approves nothing. So does an entry whose ECU or JOB is not upper case (it can never
match; the warning says it must be upper case). None of these stops the agent; all count as `ignored` in `/health`.
An approval only ever applies to a job the policy allows by the `STATUS_` pattern.

The hash is the first 12 hex digits (lower case) of the SHA-256 of the UTF-8 bytes of the
description, trimmed: every value of the `_JOBCOMMENTS` sets, one per line, joined by `\n`.
`describe` with `job` returns it as `descriptionHash`.

For a pinned job the agent reads `_JOBCOMMENTS`; a matching pin takes the place of the
description screen only. A call with arguments then goes through the argument screen as for any
screened job (`_ARGUMENTS`, then the job); a call without arguments runs the job. A pin that no
longer holds is `job_needs_review`:
- "The description of {JOB} has changed since it was approved (now {hash}). The user can approve
  it again in the agent config."
- "{JOB} has no description now, so its approval cannot be checked. The user can approve it again
  in the agent config."

A description that cannot be read keeps its own failure, as for an unpinned job: an EDIABAS error
there (often a module that does not answer, e.g. IFH-0009) is `ediabas_error`, and the job is not
run.

**No restart.** At the start of every EDIABAS request, and before it answers `/health`, the
agent re-reads `reviewedJobs`, and nothing else, from config.json. A file that is missing,
unreadable or not a valid config keeps the previous list and logs one warning per distinct
problem; a later good read is picked up.

## Telegram rule

The job policy decides by name; the telegram rule decides by the bytes. `policy/telegrams.json`
is the single source, version 1. It is enforced in three places, all running
`policy/telegram-cases.json`: the agent's check of the IFH trace after each request ("Telegram
check"), the `XGate32.dll` interface handler in the VM (`gate/`), and the native engine's frame
gate. A telegram is judged with the protocol (concept) last set for the interface:

1. **Concept.** None set → refused (`no_concept`). BMW-FAST `0x010F`, KWP2000 BMW `0x010C` and
   D-CAN `0x0110` use BMW-FAST framing; KWP2000\* `0x010D` its own; DS2 `0x0006` → refused
   (`ds2`; DS2 commands are manufacturer-specific, so version 1 allows none); any other →
   refused (`unknown_concept`).
2. **Frame.** Byte 0's top two bits must be `10` (physical) or `11` (functional), byte 2 (the
   source) must be `F1` (the tester). BMW-FAST framing: the data length is byte 0's low six
   bits; if those are 0, it is byte 3; if that is 0 too, bytes 4–5 (big-endian). KWP2000\*:
   the length is always byte 3. The data starts after the length; the frame must end with the
   data or with one more (checksum) byte. Zero length or anything else → `malformed`.
3. **Adapter.** Target and source both `F1` (the cable itself): allowed only when the first
   data byte is in `adapterReads` (`FA`, `FD`, `FE`), else `adapter`.
4. **Read services.** The first data byte (the service) in `readServices` (`$12 $13 $17 $18
   $19 $1A $21 $22 $23 $3E`) → allowed. `$10` is allowed only as exactly `10 81` (the default
   session).
5. **Exact.** Otherwise allowed only when an `exact` entry has the frame's target address
   (byte 1) and exactly its data bytes (checksum excluded). Each entry is a reviewed telegram,
   for example `57` / `30 91 01 01` for `NVE_65` `STATUS_BORDNETZSPANNUNG`. The addressing
   mode (physical or functional, byte 0) is not part of the match.
6. Anything else → `service`.
7. **Both framings.** A frame allowed by steps 3–5 is read once more with the other framing
   (BMW-FAST ↔ KWP2000\*). If that reading is well-formed too and steps 3–6 would refuse it,
   the frame is refused (`ambiguous`). Only a crafted frame parses both ways (for example
   `A3 12 F1 22 2E …`: `$22` to BMW-FAST, `$2E` to KWP2000\*), so this costs real reads
   nothing, and it means a wrong idea of the concept, in the gate or the trace check, can
   never turn an allowed read into a write.

`$30` and `$31` are never allowed by service: BMW SGBDs use them for both reads and
actuation, and do not agree on where the "report current state" byte goes. Adding an `exact`
entry is the owner's decision, made with a case, like a policy change.

## Telegram check (agent)

With `telegramCheck` (agent config) `enforce` (the default) or `report`, the agent checks
every telegram EDIABAS sent for a request, from EDIABAS's own interface trace
(`IFH.trc` in `TracePath`, written when `IfhTrace` in `EDIABAS.INI` is 3 or more):
before the request it notes the trace's length; afterwards it reads what was added, takes
the concept from each `SETECUCOMM` (its first two input bytes, little-endian) and judges each
`SENDECUTEL` input with the telegram rule. A function name it does not know is a finding too.
`enforce` turns any finding into `telegram_not_allowed` for the request and latches: every
later EDIABAS request is refused with the same code until the agent restarts, and `/health`
shows the latch. `report` only records findings. With `IfhTrace` below 3, or no trace written
for a request that ran a job, `enforce` refuses with `telegram_check_unavailable`. This
check cannot stop a telegram; it proves what was sent and stops everything after a bad one.
The interface handler below is what stops one.

How the agent does it (`EdiabasAgent.Core/TelegramCheck.cs`, `IfhTrace.cs`, `EdiabasIni.cs`):

- **Where.** `EDIABAS.INI` is the one beside `ediabasDll` (`C:\EDIABAS\BIN\EDIABAS.INI`). From
  its `[Configuration]` section (keys in any case, the first of a repeated key, `;` starts a
  comment) it reads `IfhTrace` (a whole number) and `TracePath`, which must be a full path
  (`C:\…`); the trace is `IFH.trc` there. Both are read again for every request.
- **When.** Per EDIABAS request (a batch is one request), just before `apiInit` (after the DLL
  is loaded and the conflict check has passed again) the agent reads `EDIABAS.INI` and the
  trace's length; after `apiEnd` it reads everything the request added. Under `enforce`, an
  `EDIABAS.INI` problem refuses the request there, before `apiInit`; under `report` the request
  runs unchecked and the agent log says why. A request refused before `apiInit` (by the policy,
  say) reads nothing. A trace that is shorter after the request than before was replaced: the
  agent reads the whole new file and says so in the finding and in `agent.log`. Before the
  request the agent also keeps the trace's last 256 bytes; if they differ afterwards, the file
  was replaced or rewritten in place and the request's part cannot be found
  (`trace_rewritten`). More than 16 MB added is a finding (`trace_too_large`).
- **What is a finding.** A `SENDECUTEL` the rule refuses; any function other than `OPEN`,
  `GETBATTERYVOLTAGE`, `GETIGNITIONVOLTAGE`, `GETHANDLERTYPE`, `STOPECUTELFREQUENT`,
  `SETECUCOMM`, `SETTELPARAMETER` and `SENDECUTEL` (`unknown_function`); a `SETTELPARAMETER` (the telegram preface) whose input is not all
  zero bytes (`telegram_preface`: a preface would change the bytes sent after they were
  judged); a trace line the parser cannot read (`unreadable_trace`: nothing is skipped
  silently); and, for `telegram_check_unavailable`, a request that called `apiJob` but added no
  trace lines (`no_trace`), a trace that could not be read afterwards (`unreadable_trace`) or
  was rewritten (`trace_rewritten`), a job still running after `apiBreak`
  (`job_not_stopped`: whatever it sends later is never in a checked part), or any other
  failure of the check itself (`check_failed`). A
  `telegram_check_unavailable` after a request latches as `telegram_not_allowed` does, because
  that request's telegrams went unchecked.
- **The concept.** Each `SETECUCOMM` that ends `ERROR: NO ERROR` sets it from its first two
  INPUT bytes; one that fails, or has fewer than two bytes, leaves it unknown, as does `OPEN`.
  A request's part of the trace starts with the concept the previous request ended with only
  when nothing was written to the trace in between and the previous part had no finding; else
  unknown, so a telegram before any `SETECUCOMM` is `no_concept`. Trace written between two
  requests (a BMW tool's, which the conflict check keeps out during a request) is not judged;
  `agent.log` notes it.
- **The gate's log.** With a `GATE:` interface the agent also reads what the request added to
  `XGate32.dll`'s log (`LogPath` in `XGate.ini` beside `api32.dll`, read as the gate reads it;
  default `C:\EDIABAS\TRACE\XGate.log`). The gate refuses a telegram before `XStd32.dll`
  sees it, so a refused telegram is in no `IFH.trc`, and EDIABAS reports only error 2
  (`ERRORCODE OUT OF RANGE`, desk run 2026-11-25): each `REFUSED` line, and each
  `WOULD_REFUSE` line in log mode (that telegram was forwarded), is a `telegram_not_allowed`
  finding with the gate's reason, its function as `XGate fkt <n>` and the frame. A request that
  ran a job while the gate logged no call is `telegram_check_unavailable` (`gate_not_seen`):
  EDIABAS did not go through the gate. A gate log that cannot be read is `unreadable_trace`.
- **Records.** Each finding is a `WARN` line in `agent.log` and an audit line of its own (path
  `<path>#telegram[i]`, the finding's code as the error code, its reason as the rule, and the
  function, concept and frame as the target), under `report` as under `enforce`. The request's
  own audit line ends with the number of telegrams checked (see "Audit").
- **Interface.** `ediabasInterface` (agent config, default `null`) opens that interface with
  `apiInitExt(ifh, "", "", "")` instead of `apiInit`; it must be an interface and a unit such as
  `GATE:OBD` (`^[A-Za-z0-9_]{1,16}:[A-Za-z0-9_]{1,16}$`, else a config error). With
  `requireGate: true` every EDIABAS request except the Ecu listing is refused with
  `ediabas_unavailable` unless the interface starts with `GATE:`.

## EDIABAS endpoints
Every one of these runs the conflict check first and returns `cable_in_use` if it fails,
including the ones that only read SGBD files. One `apiInit` … `apiEnd` cycle per request; no
handle outlives the request. Requests are served one at a time.

**Time in the queue counts.** A request's `maxJobTimeoutSeconds` budget starts when it arrives,
so time spent waiting behind another EDIABAS request counts against it. `durationMs` still
covers only the run. A request that waited the whole budget gets `not_run` and EDIABAS is not
loaded for it.

**Job not in the SGBD.** A job the SGBD does not have fails with `ediabas_error` like any other
failed job; the agent does not run `_JOBS` to check. Only `describe` answers `job_not_found`.

**apiEnd failure.** If `apiEnd` fails after a job ran, the client still gets the result. The
agent logs it and does not release `api32.dll` again until it is restarted.

**Conflict check during a request.** The check runs again after loading `api32.dll` (a tool
seen then means no `apiInit`), before every EDIABAS job the agent sends within a request (each
batch item, the description screen's `_JOBCOMMENTS`, the argument screen's `_ARGUMENTS`, the job
itself and a follow-up `_JOBS`, and each of describe's `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS` and
`_RESULTS`), and every 250 ms while a job runs. A check less than 250 ms old is reused (the
process scan is not free, and describe on a large SGBD sends hundreds of short jobs). A job
running when a BMW tool is seen is stopped with `apiBreak` and counts as not run. A check that
fails while a job runs (the process scan threw) counts the same way: the job is stopped with
`apiBreak` and is `not_run`. Once a BMW tool is seen, no further job starts in that request:
- `/ediabas/jobs`: the item and every later item get `not_run`; items
  already done are kept.
- describe without `job`: the job list is kept, comments already read are kept, and the rest are
  `not_checked`. If the tool appears before or while `_JOBS` runs, the request is `cable_in_use`.
- `/ediabas/job` and describe with `job`: the whole request is `cable_in_use`, with the tools in
  `details.processes`.

**Idle release.** `apiEnd` alone leaves EDIABAS's host process `ebas32.exe` running for as long
as the agent has `api32.dll` loaded (seen at the desk). So the agent unloads the DLL once no
EDIABAS request has arrived for `ediabasIdleReleaseSeconds` (agent config, default 20, range
5..600), and loads it again on the next request. The first request after a release is slow
(about 10 s in this VM). `/health` reports `ediabas.loaded` (boolean): whether the DLL is
loaded right now.

**A job that does not stop.** A job still busy at its timeout (or when the client gives up) is
stopped with `apiBreak`. If `apiState` still says busy 10 s later, the agent starts no further
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
  "durationMs": 412, "commentsCutOff": null }
```
`commentsCutOff` is `null` when every description asked for was read (or `comments=false`),
else why the rest were not: `time_limit` (the request's budget ran out; asking again goes on from
there, because the agent caches every description it read by the SGBD file's SHA-256),
`tool_started`, `ediabas_stuck` or `abandoned`. A job whose own description read was stopped by
the time limit is `needs_review` in that answer, and is read again on the next request.
`review` is `not_required` (no screen applies), `passed`, `needs_review`, `reviewed` (a pinned
`reviewedJobs` entry that matches the description), or `not_checked`
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
`jobTimeoutSeconds` and is clamped to `maxJobTimeoutSeconds`. With a `maxJobTimeoutSeconds`
above 60 the agent does not start serving (it keeps running, logs why and re-reads the config
every 30 s): a request can last that long plus the 10 s `apiBreak` grace, and
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
- a BMW tool started during the request, or the check for one failed while the item's job ran
  (see "Conflict check during a request");
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

The Mac's `ediabas_vehicle_status` is one `/ediabas/jobs` request: `UTILITY STATUS_ZUENDUNG`,
`UTILITY STATUS_UBATT`, each `voltageSources` entry from the Mac's `config.local.json`, then
`TMODE LESE_SPANNUNG_KL30` and `TMODE LESE_SPANNUNG_KL15` ([tools.md](tools.md)). The agent has
no vehicle-status endpoint of its own (`/ediabas/vehicle` was removed in 0.5.0), and an old
`voltageSources` key in its `config.json` is a start-up warning, not an error.

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
  `COM1`, …), junction escapes, and 8.3 short names that resolve outside the roots. A log file
  that is itself a symbolic link is opened as the link and refused (`file_not_found`), whatever
  it points at; before, it was followed and checked by final path. A link is never followed, so
  one planted in a root that points at a device is never opened.

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
format characters (U+202E, U+200B, U+2066 …) in a field are replaced with `_`. A last field,
`telegrams`, holds the number of telegrams the telegram check judged for an EDIABAS request
(`-` when it did not check), and each telegram check finding adds a line of its own.
