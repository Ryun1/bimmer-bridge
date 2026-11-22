# Review fixes and next features: implementation plan (2026-11-22)

> Historical record: the plan for the 0.3.0 review fixes, as written at the time. For the current state, see the [README's Status](../../README.md#status).

> **For agentic workers:** each task below is one implementer's whole brief. Read "Rules for
> every task" first, then only your task. Steps use `- [ ]` for tracking.

**Goal:** fix what the second review (2026-11-22) found, and add the batch tool, session
ergonomics, whole-car reads and pinned approvals, without weakening any read-only rule.

**Architecture:** unchanged. A C# agent in the VM (`vm-agent/`), a TypeScript MCP server on the
Mac (`mac-mcp/`), one policy file both sides parse (`policy/`). Work is split by directory so
five tasks run in parallel in their own git worktrees; two later tasks build on the merge.

**Tech stack:** .NET 10 (xUnit), TypeScript 7 + vitest, Python 3 unittest, Windows PowerShell 5.1.

**Spec:** the review findings as listed in each task. Contracts: `docs/api.md`, `docs/tools.md`,
`docs/safety.md`, `policy/policy.json` and its case files.

## Rules for every task

- READ-ONLY product. Never add an endpoint, tool, flag or code path that can write to the car,
  clear faults, send raw telegrams, change EDIABAS configuration or put input into the VM.
- Do not contact the VM or its agent (192.168.64.x), do not call any `ediabas-bridge` MCP tool,
  do not run UTM, `release.sh` or `dev/serve.py`. All testing is local with fakes and stubs.
- Work only in your own worktree (path in your task) and only in the files your task lists.
  Never touch `~/projects/ediabas-bridge` itself: its `mac-mcp/dist` and `skills/`
  are live.
- Code tasks (A to E, G) do NOT edit `README.md`, `docs/`, `skills/` or any directory README.
  List every doc change your work needs under "Doc changes" in your report; task F applies them.
- Do not change version numbers (`VERSION`, `package.json`, `Directory.Build.props` `<Version>`).
- Test first: write the failing test, see it fail, make it pass. Commit after each numbered
  item with a plain one-line message in the repo's style (see `git log`). Commit only inside
  your worktree; do not push, merge or rebase.
- Facts about EDIABAS or the car that are not in the repo are not to be hard-coded as truths.
  Where a behaviour can only be confirmed in Windows or at the car, say so in the report.
- No token, VIN, or address beyond those already in the repo in any file.
- Follow the existing code's patterns and tone. Comments say why, briefly. Plain English.
- Final message: status (DONE / DONE_WITH_CONCERNS / BLOCKED), the test commands with their
  result lines, the commits, "Doc changes", anything you could not verify, deviations from this
  plan with the reason. Under 60 lines.

Suites: `dotnet test vm-agent` (1118 pass on main); `cd mac-mcp && npm ci && npm test &&
npm run typecheck` (436 pass); `npm --prefix mac-mcp run test:dev` and `typecheck:dev` (40 pass);
`python3 -m unittest dev/test_serve.py` (62 pass). The host project is checked with
`dotnet build vm-agent/EdiabasAgent/EdiabasAgent.csproj -c Release -r win-x86`.

## Review focus

Conditions no single task's tests would otherwise pin; each has its test in the owning task.

1. Claude sends two `ediabas_*` calls at once: the second waits, then runs or says it was not
   sent. It never reports "may have run" for a request that was never sent (A2, C2).
2. The VM comes back on a new address: the next call finds it instead of failing (C3).
3. A log root that is a junction to somewhere else: listed as missing, never walked (D1).
4. A job description changes after the owner approved the job: it needs review again (G).
5. A recording made at the car carries the VIN in an encoded or binary field: scrubbed (E1).

---

## Wave 1 (parallel)

### Task A: agent, EDIABAS path

Worktree `~/projects/ediabas-bridge-wt/a-ediabas-core`, branch `fix/a-ediabas-core`.

**Files:** `vm-agent/EdiabasAgent.Core/EdiabasService.cs`, `EdiabasAgent.Core/EdiabasErrorMap.cs`
(read only unless needed), `vm-agent/EdiabasAgent/Ediabas/EdiabasRunner.cs`,
`vm-agent/EdiabasAgent/EdiabasEndpoints.cs`, tests under `vm-agent/EdiabasAgent.Tests/`
(`EdiabasServiceTests.cs`, `Fakes.cs`, new files as needed). Not `JobPolicy.cs`,
`CommentScreen.cs`, `AgentConfig.cs` (other tasks).

- [ ] **A1. `Execute` is the single gate.** `Execute` returns `NotRun("The request was
  abandoned before {job} started.")` when `ct.IsCancellationRequested`, before the budget check,
  so no `apiJob` starts for an abandoned request. Tests: cancel while the screen's
  `_JOBCOMMENTS` runs, assert the client's job never reaches `FakeEdiabasApi.Job`; cancel after
  `_JOBS` in `DescribeJob`, assert no further info job is started. Remove `ct` checks that
  `Execute` now makes redundant only where a test still pins the behaviour.
- [ ] **A2. Queue time counts against the request budget.** `EdiabasRunner.Run` measures how
  long the item waited in the queue and passes it on; the service's request methods take
  `long queuedMs` and the session's budget starts that much earlier (`StartMs = now - queuedMs`).
  A request that waited the whole budget is answered `not_run` ("waited behind another EDIABAS
  request for the whole {max} s limit; nothing was started") without loading EDIABAS. Tests in
  Core with the fake clock: queued 50 s of a 60 s budget leaves 10 s for the job; queued 60 s
  starts nothing.
- [ ] **A3. A failed `apiEnd` does not discard a result.** `Session.Dispose` logs, sets
  `_handleMayBeOpen` and does not rethrow. Test: `End` throws after a successful job; the
  caller gets the job result; `ReleaseIfIdle` afterwards does not unload.
- [ ] **A4. Unreadable results get their own message.** When `ResultShaping.ReadAll` returns
  null after `Ready`: `ediabas_error` with the message "The job finished but its results could
  not be read." and whatever code and text EDIABAS reports in the details. Test pins the text.
- [ ] **A5. Arguments are screened.** In `RunItem`, when the job is screened (`verdict.Screen`
  and not reviewed) and `request.Args` is not empty: read `_ARGUMENTS` for the job (the same
  helper `DescribeJob` uses), join every `ARG` name and `ARGCOMMENT`, and pass that text
  through `_screen.Screen`. A matched term, a failed read, or no argument rows at all gives
  `job_needs_review` with the message "The argument description of {job} mentions: …" (or
  "{job} takes arguments but they are not described, so the call cannot be shown to be a
  read."). Rows with empty comments and no matched term pass. Jobs called without args are
  unchanged. Tests: each of the four outcomes; call order (`_JOBCOMMENTS`, `_ARGUMENTS`, job).
- [ ] **A6. A job the SGBD does not have is `job_not_found`.** When a client job (not an
  agent info job) fails with `ediabas_error`, run `_JOBS` for that ECU once through `Execute`
  (so budget, cancel and cable gates apply); if the list loads and does not contain the job,
  answer `job_not_found` ("{ECU} has no job {JOB}.") instead. If `_JOBS` fails, keep the
  original error. Only for an ECU that is a `.prg` file: a group file (`.grp`) would have to
  reach the car again to list its jobs, so its error is left as it is. No EDIABAS error number
  is assumed. Tests: a `.grp` name starts no `_JOBS`; missing job in a single run and
  as a batch item; `_JOBS` failing keeps the first error; a job that exists keeps its error.
- [ ] **A7. Tidy.** `OverBudget(requestStart)` and the separate budget sum in `Execute` become
  one `Session` member (`BudgetLeftMs`); drop the `requestStart` parameters it replaces. No
  larger split of the file in this round.

### Task B: policy version 3, both parsers

Worktree `~/projects/ediabas-bridge-wt/b-policy`, branch `fix/b-policy`.

**Files:** `policy/policy.json`, `policy/policy-cases.json`, `policy/policy-parse-cases.json`,
`vm-agent/EdiabasAgent.Core/JobPolicy.cs`, `CommentScreen.cs`, their tests,
`mac-mcp/src/policy.ts`, `mac-mcp/test/policy.test.ts`.

- [ ] **B1. Version 3, whole-car reads.** `"version": 3`. Add to `allow.restrictedEcus`:
  `"E60": ["INFO", "IDENT_FUNKTIONAL", "FS_LESEN_FUNKTIONAL", "_JOBS", "_JOBCOMMENTS",
  "_ARGUMENTS", "_RESULTS", "_VERSIONINFO", "_TABLES", "_TABLE"]`. Add `"C_FA_LESEN"` to
  `allow.globalExact`. (Recorded at the desk 2026-11-21: these are KWP `$1A`, `$18` and `$22`
  reads; every other `E60` job stays refused.) Cases: `E60` `FS_LESEN_FUNKTIONAL` and
  `IDENT_FUNKTIONAL` allowed; `E60` `FS_LOESCHEN_FUNKTIONAL`, `IS_LOESCHEN_FUNKTIONAL`,
  `SLEEP_MODE_FUNKTIONAL`, `CBS_RESET_FUNKTIONAL`, `ENERGIESPARMODE_FUNKTIONAL`,
  `MOST_CAN_GATEWAY_DISABLE`, `I_STUFE_SCHREIBEN`, `IDENT`, `STATUS_X` refused;
  `KOMB60` `FS_LESEN_FUNKTIONAL` refused (`default_deny`); `CAS` and `LM_60` `C_FA_LESEN`
  allowed; `C_FA_SCHREIBEN` and `C_FA_AUFTRAG` refused.
- [ ] **B2. Screen terms.** In `descriptionScreen.terms` replace `"programm"` with `"program"`
  and add `"einstell"`, `"verstell"`, `"aender"`, `"änder"`, `"adjust"`. Do NOT add `"steuer"`
  (it matches "Steuergerät", the word for a control unit) or `"control"`. Cases for a
  `STATUS_` job described as "Stellmotor auf Wert einstellen", "Program default value",
  "Wert aendern" (needs review) and "Status des Steuergeraets lesen" (passes).
- [ ] **B3. Both parsers refuse the same files.** Each of these is refused by `JobPolicy.Parse`
  and by `policy.ts`, with a shared entry in `policy-parse-cases.json`:
  (a) a deny substring, deny token, allow name, restricted ECU key or restricted job name that
  is not already upper-case; (b) an `allow.patterns` entry that contains a backslash or a
  non-ASCII character (the two regex dialects only agree without escapes); (c) `version` or
  `maxArgLength` written as a non-integer literal (`2.0`, `2e0`): the Mac checks the raw text,
  since `JSON.parse` hides it. Then both evaluators compare entries as written.
  `JobPolicy.Parse` also returns a refusal instead of throwing on a string with a lone
  surrogate (C# test only). Keep `policy.json` valid under the new rules.
- [ ] **B4.** Remove the dead `.filter((s) => s.length > 0)` at `policy.ts:117`.

### Task C: Mac server

Worktree `~/projects/ediabas-bridge-wt/c-mac`, branch `fix/c-mac`.

**Files:** `mac-mcp/src/` except `policy.ts`; `mac-mcp/test/`. New files where they keep
`server.ts` and `agentClient.ts` from growing (`src/faults.ts`, `src/carSession.ts`,
`src/textBudget.ts` are suggested names).

**Interfaces consumed from other tasks (build against these; they land in the same release):**
agent error codes `job_not_found` on `/ediabas/job` and as a `/ediabas/jobs` item (Task A),
`capture_busy` (HTTP 503) and `capture_failed` (HTTP 500) on `/screen/capture` (Task D).

Robustness:
- [ ] **C1. Error text carries its code.** Every error result starts `[code] ` (for example
  `[job_not_allowed] The job policy refuses…`). `ediabasRequestLost` gets its own code
  `ediabas_request_lost`. The server instructions name the codes exactly as printed.
- [ ] **C2. One EDIABAS request at a time.** `AgentClient` queues calls with `spec.ediabas`
  (FIFO); the timeout clock starts when the request is sent, not when it is queued. A call
  that waits more than 120 s is answered `[ediabas_busy]` "another EDIABAS request is still
  running; this one was not sent". Test with a stub that serves one request at a time: two
  concurrent calls both succeed; neither is sent twice.
- [ ] **C3. A connect timeout was never sent.** `classify` maps `UND_ERR_CONNECT_TIMEOUT` to
  `unreachable` with `neverSent = true`, so the address is resolved again and a job request is
  not reported as possibly run. `ETIMEDOUT` stays as it is. Tests for both codes on a
  `runsJob` call and on a plain call.
- [ ] **C4. One text budget.** No tool result exceeds 60 000 characters of text: applied once
  in `run()`, cutting at a line end with a last line that says how to narrow the call for that
  tool (filter, `results`, a smaller range). `vm_logs_read`: `tailBytes` and `length` max 65536,
  and 16384 bytes from the end when no range is given. `ediabas_list_ecus` prints at most 500
  names, then "N more; use filter". Remove `LOG_TEXT_MAX` and `RAW_MAX`. Tests: a 5000-name
  ECU list, a 400-job describe and a 65536-byte log stay under the budget and end with the hint.
- [ ] **C5. Messages.** `job_not_found`: "{ECU} has no job {JOB}." and, inside
  `ediabas_read_faults` for `IS_LESEN`, the line "no info memory on this module" that is not
  counted as a failure. `capture_busy`: another screenshot is still being taken, try again.
  `capture_failed`: the window could not be captured (not a locked screen). Pass the redacting
  logger to `buildServer` (`server.ts:468`). Remove the dead `safeCode` branches in `errors.ts`.
  `ediabas_describe`: the description says it needs the cable attached to the VM, can take tens
  of seconds on a large SGBD, and that `filter` is ignored when `job` is given.

Features:
- [ ] **C6. `ediabas_jobs`.** Input `jobs`: 1 to 20 of `{ecu, job, args?, results?}`, and
  `raw?`. The Mac checks every item against the policy first and refuses the whole call if one
  is refused (same rule as `ediabas_read_faults`); then one `POST /ediabas/jobs`. Output: one
  block per item in request order using the single-job formatter, a failed item as its
  `[code]` line, a header "N of M ran". Read-only annotations like the other tools. Move
  `readFaults` out of `server.ts` while there. Tests: pre-check refusal sends nothing; mixed
  results; 21 items rejected by the schema.
- [ ] **C7. Car-session clock.** A small `CarSession` with an injected clock: a car session
  starts at the first successful car-facing result (`ediabas_job`, `ediabas_jobs`,
  `ediabas_read_faults`, `ediabas_vehicle_status`; not describe, list or `vm_*`; not `TMODE`
  or `UTILITY`) and ends after 5 minutes without one. Every car-facing result ends with
  "Car session: 4 min 10 s since the first read from the car (aim to finish within 10 min)."
  It measures time since the first read, not the ignition; the text must not claim otherwise.
- [ ] **C8. Versions in `vm_health`.** First line: "Mac server {version}, started {local time}"
  (version read from `package.json` at runtime), then the agent's version, and a warning line
  when they differ: "The Mac server and the agent are different versions; restart Claude or
  update the agent."
- [ ] **C9. Two parameters.** `ediabas_job` and each `ediabas_jobs` item pass an optional
  `timeoutSeconds` (1 to 60). `ediabas_describe` takes `namesOnly?: boolean`, sent as
  `comments=false`, printed with the note "names only: not screened, verdicts by name".

### Task D: agent, host side

Worktree `~/projects/ediabas-bridge-wt/d-host`, branch `fix/d-host`.

**Files:** `vm-agent/EdiabasAgent.Core/LogService.cs`, `LogText.cs`, `AuditLine.cs`,
`CaptureRules.cs`, `AgentConfig.cs`, `ApiModels.cs` (error codes only);
`vm-agent/EdiabasAgent/ScreenEndpoints.cs`, `Capture/WindowCapture.cs`, `AgentHost.cs`,
`Net/AdapterProbe.cs`, `Middleware.cs`; their tests. Not `EdiabasService.cs`, `JobPolicy.cs`.

- [ ] **D1. `/logs` uses the roots `/logs/read` uses.** One `UsableRoots()` for both: a root
  whose resolved path is not a plain path on a local fixed disk is `exists: false` and is never
  walked. Tests: a root that resolves to a non-local drive, to a bare drive (`C:\`), and to a
  path `NormalizeFinal` refuses: no `ListDirectory` call for any of them.
- [ ] **D2. Honest capture errors.** Queue timeout: `capture_busy`, HTTP 503, "another capture
  is still running". GDI or copy failure: `capture_failed`, HTTP 500. `desktop_unavailable`
  only for a locked screen, the secure desktop or session 0. Core test for the three mappings.
- [ ] **D3. The bind loop survives a probe failure.** Guard `AdapterProbe.Resolve` at
  `AgentHost.cs:46` like the one at 141: log, wait `RetryDelay`, continue.
- [ ] **D4. Audit lines neutralise format characters.** `AuditLine` and the `where` text in
  `Middleware` replace `UnicodeCategory.Format` characters (U+202E, U+200B, U+2066…) as they do
  control characters. Test with U+202E and U+200B.
- [ ] **D5. One grep deadline per request.** 5 s across all lines; past it, `bad_request`
  "grep took too long; use a simpler pattern or a smaller range". Test with an injected clock.
- [ ] **D6. Config typos are visible.** Unknown top-level keys in `config.json` are returned
  as warnings and logged at start (the load still succeeds); `logRoots` entries that fail
  `PathRules.Check` are logged as warnings. Tests for `reviewJobs` and `C:/EDIABAS/TRACE`.

### Task E: deploy and dev tooling

Worktree `~/projects/ediabas-bridge-wt/e-deploy`, branch `fix/e-deploy`.

**Files:** `dev/scrub-fixtures.ts`, `dev/test/`, `dev/serve.py`, `dev/test_serve.py`,
`vm-setup/*.ps1`, `release.sh`, `vm-agent/build.sh`, `vm-agent/Directory.Build.props` (not the
`<Version>` line), `.gitignore`.

PowerShell cannot be run on this Mac (`pwsh` is not installed, and must not be installed by
you). Keep every `.ps1` change small, ASCII, valid in Windows PowerShell 5.1, and read it back
line by line. List each `.ps1` change in your report so it can be checked in the VM.

- [ ] **E1. Scrubber.** A string with percent-escapes is decoded, scrubbed and re-encoded
  (the agent percent-encodes `X-Window-Title`). In hex results, any 17-character run of the
  VIN alphabet with at least one letter and one digit is scrubbed, whatever touches it. In
  text, also scrub a BMW VIN (`WB` + 15 VIN characters) in any case and with any neighbours.
  Tests: the encoded title with a VIN and a `C:\Users\name` path; hex `"1"+VIN+"\0"` and
  `"FG"+VIN`; text `FG<VIN>` and a lower-case VIN.
- [ ] **E2. Committed fixtures are clean.** A test runs every recording under `dev/fixtures`
  through `scrubRecording` and expects zero replacements.
- [ ] **E3. The token is never shown.** `install.ps1` no longer prints the token or a
  `security` command; a failed hand-off says to start `serve.py --accept-token` and paste the
  Install line again. The token is sent only on a first install or when `$SendToken` is true;
  `serve.py` adds `$SendToken=$true;` to the Install line when started with `--accept-token`.
- [ ] **E4. Options do not stick.** After reading `$KeepData`, `$Elevated`, `$SendToken`
  (and `$Purge`), the scripts remove the variable from the global scope.
- [ ] **E5. Uninstall keeps the owner's data unless told otherwise.** Default: remove the task,
  firewall rule and program files, keep `config.json`, `token.txt` and `logs\`. `$Purge=$true`
  removes everything. `$KeepData` is accepted and ignored with a note. `serve.py` prints the
  matching hint.
- [ ] **E6. Left-over staging folders.** Install, update and uninstall remove
  `C:\ProgramData\EdiabasBridge-staging-*` at the start.
- [ ] **E7. The pasted line fails clearly.** It deletes its temp file before downloading and
  reports "download failed: the Mac did not serve {name} (is serve.py running?)" instead of
  running on. The hash check is unchanged. `test_serve.py` pins the line.
- [ ] **E8. `release.sh` and `build.sh`.** Refuse a dirty tree unless `--allow-dirty`. Add
  `commit.txt` (short hash, `-dirty` when so) beside `version.txt` in the zip and print it in
  the summary; `version.txt` is unchanged. `ZIP_VERSION` read no longer exits silently when the
  zip has no `version.txt`. `build.sh` publishes with `-p:ContinuousIntegrationBuild=true
  -p:EnableSourceLink=false` and a `PathMap` so no local path is embedded. When `pwsh` is on
  the PATH, `release.sh` parses the four `.ps1` files and fails on a parse error; otherwise it
  prints that the check was skipped.
- [ ] **E9. Install and update stay in step.** Mark the block the two scripts share with
  `# shared:begin` / `# shared:end` comments; a Python test asserts the two blocks are equal.
- [ ] **E10. `.gitignore`.** Add `*.ini`, `*.INI`, `*.dll`, `*.DLL`, `*.exe`, `*.EXE`.

---

## Wave 2 (after wave 1 is merged into `fix/review-2`)

### Task G: approvals pinned to the description, read without a restart

Branch `fix/g-approvals` from `fix/review-2`.

**Files:** `EdiabasService.cs`, `CommentScreen.cs`, `AgentConfig.cs`, `ApiModels.cs`,
`EdiabasEndpoints.cs`, tests; `mac-mcp/src/formatEdiabas.ts` and its test.

- [ ] **G1. Pinned approvals.** A `reviewedJobs` entry is `ECU:JOB` (as today) or
  `ECU:JOB@<12 hex>`, the first 12 hex characters of the SHA-256 of the job's description text
  as the screen sees it (UTF-8, trimmed). For a pinned entry the agent reads `_JOBCOMMENTS`
  before the job and runs it only if the hash matches; otherwise `job_needs_review` with "the
  description of {JOB} has changed since it was approved". Unpinned entries still work.
- [ ] **G2. Describe shows what to paste.** `DescribeJobResponse` carries `descriptionHash`;
  the Mac prints, for a job that needs review, the exact config line
  `"ECU:JOB@hash"` under the description. `/health` reports how many approvals are unpinned.
- [ ] **G3. Approvals are re-read.** At the start of each EDIABAS request the agent checks
  `config.json`'s last-write time and re-reads `reviewedJobs` only. A file that does not parse
  keeps the previous list and logs a warning. Nothing else in the config is re-read.
- Tests: hash match and mismatch; unpinned still honoured; reload picks up a new entry; a
  broken file keeps the old list.

### Task F: docs, skills, evals, version 0.3.0

Branch `fix/f-docs` from `fix/review-2` after G.

- [ ] Apply every "Doc changes" list from tasks A to G to `docs/api.md`, `docs/tools.md`,
  `docs/safety.md`, `docs/deploy.md`, `README.md` and the directory READMEs.
- [ ] Skills: correct the stale statements (`inpa-screens.md`, `trace-files.md`,
  `ecu-map.md:50`, `battery-drain.md:13`, `SKILL.md:41`); drop "whenever any vm_* or ediabas_*
  tool is available" from the trigger; scope "stop if a BMW tool is running" to `ediabas_*`
  calls; remove the note that the server's hint is wrong (C fixes the hint); use
  `ediabas_jobs` and the `E60` whole-car read in the playbooks, marked "not yet run at the
  car"; add "battery charger on" and "unplug the cable from the car afterwards"; say once, in
  one place each, that describe needs the cable and that the first call after a pause is slow.
- [ ] Trim: `errors.md` to the judgement cases; empty rows out of `ecu-map.md`; `risk.md`
  folded into `bmw-standard-tools/SKILL.md`. Job-cookbook verdicts are marked as recorded
  under policy version 2.
- [ ] Evals and fixtures: remove the "cannot be run" boilerplate from evals 05 and 08; fix
  `dev/fixtures/README.md`; note that eval 04's fixture can be recorded at the desk.
- [ ] `docs/at-the-car.md`: charger, `ediabasIdleReleaseSeconds` 90 for the session, check
  both versions in `vm_health`, unplug at the end.
- [ ] Version 0.3.0 in `VERSION`, `mac-mcp/package.json` (+ lock), `Directory.Build.props`.

---

## Not in this round, and why

- Battery-evidence snapshot, vehicle-status field mapping, grouping functional fault results by
  ECU: need recordings from the first car session (Phase 6).
- Recording the `needs-review` fixture and committing the eval runner: need the owner, the VM and
  the cable at the desk.
- `PrintWindow` watchdog, a Windows test project, merging `update.ps1` into `install.ps1`:
  cannot be tested without Windows; E9 keeps the two scripts in step meanwhile.
- Splitting `EdiabasService.cs`, a capture allowlist, bind choice with two candidate adapters,
  audit-log flooding, the self-test firewall check, links to device paths under a log root:
  low risk for a single-user tool; left for a later pass.

## The owner's steps after the merge

1. `./release.sh`, `python3 dev/serve.py`, paste the Update line in the VM (agent 0.3.0,
   policy version 3).
2. Restart Claude Code and the Claude desktop app so both run the new Mac server.
3. At the desk with the cable in the VM: `ediabas_describe` on `E60` (the two functional jobs
   show as allowed) and on a job that needs review (the paste line with its hash appears).
