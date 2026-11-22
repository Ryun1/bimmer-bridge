# Review fixes and simplifications (0.5.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix every confirmed finding of the 2026-11-22 review and take the simplifications that
also improve safety, released as 0.5.0.

**Architecture:** No change to the shape of the system: Mac MCP server → HTTP → agent in the VM →
`api32.dll`. The agent gains a conflict check inside the job loop and a `/hello` identity
proof, and it loses unpinned approvals, `/ediabas/vehicle` and `NotInJobList`. The Mac gains
the identity check, policy-hash and approval-change checks, and it loses ARP/utmctl discovery
and the production recorder.

**Tech Stack:** C# .NET 10 (xUnit) in `vm-agent/`, TypeScript Node 20+ (vitest, zod) in
`mac-mcp/`, Python 3 `dev/serve.py`, Windows PowerShell 5.1 scripts in `vm-setup/`.

**Spec:** `docs/superpowers/specs/2026-11-22-review-fixes-design.md` (findings F1–F17, decisions D1–D13).

## Global Constraints

- Nothing in this plan runs against the car. EDIABAS jobs run only on fakes, or at the desk on
  `TMODE`/`UTILITY` and describe calls after the owner confirms the cable is not in the car (PLAN.md §12).
- A policy change changes `policy/policy.json`, `policy/policy-cases.json` and the `version` together.
- Every error message starts with its `[code]` on the Mac side; new codes: `policy_mismatch`.
- Agent: one EDIABAS thread; every `apiJob` goes through `EdiabasService.Execute`.
- Tests to run after every task (all must pass):
  - `(cd vm-agent && dotnet test EdiabasAgent.Tests)`
  - `(cd mac-mcp && npm run typecheck && npm test)`
  - `(cd mac-mcp && npm run typecheck:dev && npm run test:dev)`
  - `python3 -m unittest dev/test_serve.py`
- Docs that a task's change makes wrong are fixed in that task (`docs/api.md`, `docs/tools.md`,
  `docs/safety.md`, skills). Task 21 is for the restructure only.
- Version 0.5.0 everywhere (Task 22). Each commit message names the finding or decision.

## Review Focus

1. **A BMW tool that appears during the 10 s DLL load or during a long job.** Expected: no `apiInit`,
   or the job is stopped within about 250 ms. Tests in Task 2.
2. **An approval removed while the editor still has `config.json` locked.** Expected: the next
   request after the save sees the removal. Test in Task 3.
3. **A stale cached address now held by another HTTP service that answers 404 or 200 with other
   JSON.** Expected: the token is never sent to it, and the next call rediscovers. Test in Task 15b.
4. **A job request failing with EHOSTUNREACH after the body was written.** Expected:
   `[ediabas_request_lost]`, never a second send. Test in Task 14.
5. **An owner who updates the VM before pinning their unpinned approvals.** Expected: those jobs
   refuse with `job_needs_review` and the paste line, `vm_health` shows them as ignored, and
   nothing else breaks. Tests in Tasks 4 and 5.

---

## Part 1: unblock and agent safety

### Task 1: release and build ignore untracked files (F9)

**Files:** Modify `release.sh:85`, `vm-agent/build.sh:45`; `.gitignore`.

- [ ] **Step 1:** In both scripts use `git status --porcelain --untracked-files=no` for the dirty check.
- [ ] **Step 2:** Add `reports/` to `.gitignore` (scratch output). Keep `docs/superpowers/`
  and `research_notes/` trackable.
- [ ] **Step 3:** Verify: `./release.sh --no-build` gets past "Checking the git tree" with only
  untracked files present, and still stops after `touch` + `git add` of a modified tracked file.
- [ ] **Step 4:** Commit `release.sh, build.sh: untracked files do not make the tree dirty`.

### Task 2: conflict check while a job runs and before apiInit (F1, F2, D4)

**Files:** Modify `vm-agent/EdiabasAgent.Core/EdiabasService.cs` (`Execute` poll loop ~834-853,
`Session.EnsureInit` ~1069-1085, `RunItem` ~478-540 and its callers). Test:
`vm-agent/EdiabasAgent.Tests/EdiabasMidRequestConflictTests.cs`.

**Interfaces:** Produces `private ApiFailure? RunItem(Session, JobRequest, int timeout, CancellationToken)`
(the `recheckConflicts` parameter is removed).

- [ ] **Step 1: Write the failing tests** (use the file's existing `ToolStartsAt`, `Takes`, `WinKfp`):
  - `SingleJob_ToolStartsDuringTheJob_JobIsBroken_NotRun`: `Takes(5000)`, `ToolStartsAt(1000)`.
    Assert the result is `not_run` and the message contains `WINKFPT`, `_api.Calls` contains
    `"Break"`, and `_clock.ElapsedMs < 1000 + 2 * EdiabasService.ConflictRecheckMs + EdiabasService.PollIntervalMs`.
  - `SingleJob_ToolStartsDuringDllLoad_NoApiInit`: make `FakeEdiabasApi.TryLoad` advance the
    clock 10 000 ms (add a `LoadMs` property to the fake), `ToolStartsAt(5000)`. Assert the result
    is `cable_in_use`, and `_api.Calls` has no `"Init"` and no entry starting `"Job "`.
  - `SingleJob_LongJob_NoTool_ConflictScansAreRateLimited`: `Takes(3000)`, no tool. Assert
    `_conflicts.Calls <= 3000 / EdiabasService.ConflictRecheckMs + 2`.
- [ ] **Step 2:** Run `dotnet test --filter EdiabasMidRequestConflictTests`. Expected: the 3 new tests fail.
- [ ] **Step 3: Implement.**
  - In the poll loop, after the cancel and timeout checks: if `ToolStartedSince(session, force: false)`
    is not null, call `BreakAndSettle(session, ecu, job)` and return `ToolStartedNotRun(running)`.
  - In `EnsureInit`, after `TryLoad` succeeds and before `_api.Init()`: if
    `owner.ToolStartedSince(this, force: true)` is not null, set `_initFailure` to the
    `cable_in_use` failure built the way `ToolStartedFailure` builds it, and return it without
    calling `Init`.
  - Remove `recheckConflicts` from `RunItem` and its call sites.
- [ ] **Step 4:** Run all agent tests. Expected: PASS.
- [ ] **Step 5:** Update `docs/safety.md` rule 4 ("…and every 250 ms while a job runs, and after
  loading the DLL; a tool seen during a job stops it with apiBreak") and the threads section of
  `docs/architecture.md`.
- [ ] **Step 6:** Commit `Agent: conflict check during a job and before apiInit (F1, F2)`.

### Task 3: re-read the approvals on every EDIABAS request (F4)

**Files:** Modify `vm-agent/EdiabasAgent.Core/ApprovalReloader.cs`. Test:
`vm-agent/EdiabasAgent.Tests/ApprovalReloadTests.cs` (rewrite the stamp-based tests).

**Interfaces:** `IConfigFile` keeps only `string ReadAllText()`. `LastWriteUtc` and its
implementation go.

- [ ] **Step 1: Write the failing tests:**
  - `Refresh_ReadFailsThenSucceeds_RemovalApplies`: an approval in the list. The file now has none,
    the first `ReadAllText` throws `IOException` and the second returns the text. After two
    `Refresh()` calls, `state.Current.Lookup(ecu, job).Kind == ApprovalKind.None`.
  - `Refresh_SameBadText_WarnsOnce`: invalid JSON returned 3 times gives exactly 1 warning, and
    the list is unchanged.
  - `Refresh_FileMissing_KeepsListAndWarnsOnce`.
- [ ] **Step 2:** Run them. Expected: the first fails, because the timestamp was recorded before the read.
- [ ] **Step 3: Implement** `Refresh()`: read the file on every call. Keep
  `string? _lastProblem` (the warning text) and warn only when the problem differs from the last
  one. Clear it after a good parse. A good parse replaces the list only when `!next.SameAs(current)`.
- [ ] **Step 4:** Run all agent tests. Expected: PASS.
- [ ] **Step 5:** Commit `Agent: reviewedJobs re-read on every EDIABAS request; a failed read no longer hides a removal (F4)`.

### Task 4: pinned approvals only; the argument screen always runs (F5, D2, D3)

**Files:** Modify `vm-agent/EdiabasAgent.Core/CommentScreen.cs` (`ApprovalKind`, `JobApproval`,
`ApprovalList`), `EdiabasService.cs` (`ScreenJob` ~549-580, `CheckPin` ~587, `ToJobVerdict` ~911-940),
`ApiModels.cs` (`ApprovalCounts`), `AgentConfig.cs` (warning text). Tests: `ApprovalTests.cs`,
`EdiabasServiceTests.cs`, `ApprovalReloadTests.cs`.

**Interfaces:**
- `enum ApprovalKind { None, Pinned }`
- `ApprovalList`: `int PinnedCount`, `int IgnoredCount` (unpinned or malformed entries), `IReadOnlyList<string> Entries` (valid pinned entries, as written, sorted ordinal).
- `record ApprovalCounts(int Pinned, int Ignored)`. Task 5 extends this.

- [ ] **Step 1: Write the failing tests:**
  - `ApprovalList_UnpinnedEntry_ApprovesNothing_CountsIgnored`: `["NVE_65:STATUS_X"]` gives
    `Lookup(...).Kind == None` and `IgnoredCount == 1`.
  - `ApprovalList_UnpinnedEntry_LogsWarningNamingThePinnedForm`: the warning contains `ECU:JOB@<hash>` and `ediabas_describe`.
  - `RunJob_PinnedApproval_WithArgs_ArgumentScreenStillRuns`: pinned and matching, with args whose
    `ARGCOMMENT` contains `setzen`. Expected `job_needs_review`, and no `apiJob` for the job.
  - `RunJob_PinnedApproval_NoArgs_SkipsDescriptionScreen_Runs` (the existing behaviour, kept).
  - `DescribeJob_UnpinnedEntry_ShowsNeedsReviewAndHash`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3: Implement.** Remove `ApprovalKind.Unpinned`, `JobApproval.Unpinned` and the
  unpinned branches in `ScreenJob` and `ToJobVerdict`. In `ScreenJob`, a matching pin skips only
  the description screen. After it, `string.IsNullOrEmpty(args) ? null : ScreenArguments(...)`
  runs for every screened job. The reload log line becomes `reviewedJobs re-read: {Pinned} pinned, {Ignored} ignored`.
- [ ] **Step 4:** Mac: in `mac-mcp/src/agentClient.ts` `HealthSchema.approvals`, accept
  `{pinned, ignored}` or the old `{pinned, unpinned}`. In `format.ts`, if `ignored > 0`, print
  `N approval entries are ignored (unpinned or malformed); ediabas_describe prints the pinned entry for each.`
  Add a test in `mac-mcp/test/format.test.ts`.
- [ ] **Step 5:** Run all tests. Expected: PASS.
- [ ] **Step 6:** Update `docs/safety.md` "Approving one job" (one form only; the argument
  screen always runs), `docs/api.md` (health `approvals`) and `skills/ediabas-bridge` errors.md if it mentions unpinned.
- [ ] **Step 7:** Commit `Agent: only pinned approvals; a pin skips the description screen only (F5)`.

### Task 5: approvals visible to the Mac, changes flagged (F3, D1)

**Files:** Agent `ApiModels.cs`, `CommentScreen.cs`, the health handler in `vm-agent/EdiabasAgent/Endpoints.cs`.
Mac `agentClient.ts` (HealthSchema), `format.ts`, a new file `mac-mcp/src/approvalWatch.ts`,
`server.ts` (the `ediabas_*` handlers), `audit.ts`. Tests: `ApiModelsTests.cs`,
`mac-mcp/test/approvalWatch.test.ts`, `tools.test.ts`, `stubAgent.ts`.

**Interfaces:**
- Agent: `record ApprovalCounts(int Pinned, int Ignored, IReadOnlyList<string> Entries, string Hash)`.
  `Hash` = the first 12 lowercase hex characters of SHA-256 over `string.Join("\n", Entries)` (UTF-8).
  For no entries it is the hash of the empty string.
- Mac: `class ApprovalWatch { observe(a: { hash: string; entries: string[] } | undefined): string | null }`.
  It returns `null` on the first observation and while the hash is unchanged. On a change it
  returns `"[approvals changed] reviewedJobs in the VM changed since this server started: now <entries or 'none'>. Only the owner should change it."`.
  It remembers the latest hash.

- [ ] **Step 1: Write the failing tests:**
  - C#: `Health_Approvals_HashIsStableForSameEntries_DiffersWhenOneAdded`.
  - TS: `approvalWatch: first observation null; same hash null; changed hash returns the message with the entries`.
  - TS `tools.test.ts`: the stub agent's health changes approvals between two `ediabas_job` calls.
    The second result starts with `[approvals changed]`, and the Mac audit line has `approvals_changed=1`.
  - TS `format.test.ts`: `vm_health` lists each entry on its own line under `Approved jobs:`.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The agent fills the new fields from `ApprovalState.Current`.
  - The Mac's `ediabas_*` handlers call `client.health()` once per tool call. This costs one
    extra `/health` round trip and never loads the DLL.
  - Feed the result to `ApprovalWatch` and prefix the result with its message when it is not null.
  - `vm_health` also feeds the watch.
- [ ] **Step 4:** Run all tests. Expected: PASS.
- [ ] **Step 5:** Fix the docs contradiction now:
  - `docs/deploy.md:84-86`, `docs/safety.md:100-101` and the README troubleshooting say: with UAC
    off, any program the owner runs in the VM can edit `config.json`; the Mac shows and flags
    approval changes.
  - `docs/api.md`: the health fields.
- [ ] **Step 6:** Commit `Approvals shown in vm_health and flagged on change (F3)`.

### Task 6: policy version 4: screen terms and chassis SGBDs (F11, D8)

**Files:** `policy/policy.json`, `policy/policy-cases.json`, `docs/safety.md` (plain-words
list), `docs/api.md`, `skills/ediabas-bridge/references/job-cookbook.md` if verdicts change.

- [ ] **Step 1: Verify names (desk, no car).** Call `vm_health`, then `ediabas_list_ecus` for
  each candidate in D8 (`E65`, `E70`, `E83`, `E85`, `E87`, `E89`, `E90`, `F01`, `F10`, `F25`, `R56`).
  This reads the Ecu folder only. Record which `.prg` files exist. Only those go into the policy.
- [ ] **Step 2: Write the failing cases** in `policy-cases.json`:
  - every confirmed chassis name with `FS_LESEN` gives `ecu_restricted`;
  - with `FS_LESEN_FUNKTIONAL` gives `allow.ecu`;
  - with `IDENT_FUNKTIONAL` gives `allow.ecu`.

  Screen cases, if the case file has a screen section (else in `CommentScreen` tests in both suites):
  - `"Schaltet Relais ein"` and `"Ausloesen Airbag Test"` give needs-review;
  - `"Status Schalter lesen"` passes (`schalter` contains none of the new terms).
- [ ] **Step 3:** Run both suites. Expected: the new cases fail.
- [ ] **Step 4:** Edit `policy.json`: `version: 4`, add the D8 terms, add each confirmed chassis under
  `restrictedEcus` with E60's list.
- [ ] **Step 5:** Run both suites. Expected: PASS.
- [ ] **Step 6:** Run the screen over the recorded describe fixtures:
  `dev/fixtures/idle/*describe*`, `dev/fixtures/inpa-open/*describe-NVE_65*`. Write a vitest in
  `dev/test/` that loads each fixture's comments and applies `screenDescription` from `policy.ts`
  (add that export if it does not exist). List the jobs whose verdict flips in the commit message.
- [ ] **Step 7:** Commit `Policy version 4: switching/triggering screen terms, chassis SGBDs restricted like E60 (F11)`.

### Task 7: policy hash compared, refuse on mismatch (F12, D9)

**Files:** Agent `JobPolicy.cs` (`public string Sha256 { get; }` over the embedded bytes),
`ApiModels.cs` (`HealthResponse.PolicySha256`, `string?`), health handler. Mac `policy.ts`
(`PolicyGuard.sha256`), `server.ts`, `errors.ts` (`policyMismatch()`), `format.ts`. Tests:
`JobPolicyTests.cs`, `mac-mcp/test/policy.test.ts`, `tools.test.ts`.

- [ ] **Step 1: Write the failing tests:**
  - C#: `Embedded_Sha256_MatchesFileBytes`: compare with SHA-256 of `../../policy/policy.json` read as bytes.
  - TS: `PolicyGuard.load().sha256` equals `createHash("sha256")` of the file bytes.
  - TS `tools.test.ts`: stub health with a different `policySha256` makes `ediabas_job` return text
    starting `[policy_mismatch]`, and the stub gets no `/ediabas/*` request. With `policySha256` absent the job runs.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The check runs inside the per-call health read from Task 5.
  - Message: `[policy_mismatch] The agent's job policy (sha256 <12>) differs from this Mac's (<12>). Update the agent or the Mac server so both carry the same policy/policy.json.`
  - `vm_health` shows both hashes.
- [ ] **Step 4:** Run all tests. Expected: PASS.
- [ ] **Step 5:** Add `policy_mismatch` to `docs/tools.md` errors, `skills/ediabas-bridge/references/errors.md`
  and the README troubleshooting table.
- [ ] **Step 6:** Commit `Policy hash in /health; ediabas tools refuse on a mismatch (F12)`.

### Task 8: default conflict process names (F13)

**Files:** `AgentConfig.cs:53`, `vm-setup/install.ps1:397`. Test: `AgentConfigTests.cs`.

- [ ] **Step 1:** Write the failing test `Defaults_ConflictProcesses_IncludeWinkfpAndIsta`: the
  defaults contain `WINKFP` and `ISTAGUI` beside the current five.
- [ ] **Step 2:** Add both names in `AgentConfig.cs` and `install.ps1`. Task 19 merges them into existing configs.
- [ ] **Step 3:** Run tests. Expected: PASS. Commit `Agent: WINKFP and ISTAGUI in the default conflict list (F13)`.

### Task 9: `/logs/read` never follows a link when it opens a file (F14)

**Files:** `vm-agent/EdiabasAgent/Logs/LogAccess.cs:64-86` (and the `LogNative` constants).
Self-test: `vm-agent/EdiabasAgent/SelfTestLogsCapture.cs`.

- [ ] **Step 1:** Add a self-test case `link to a device is refused without opening the device`.
  Create a symlink in a temp root to `\\.\NUL`. Expected: `LogOpenStatus.NotFound` or `Unreadable`, and
  the target is never opened (open with the flag below, so the link itself is what gets opened).
- [ ] **Step 2: Implement.** Open with `CreateFileW` flags
  `FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT` (`0x00200000`), so a link is opened as itself. Then `GetFileInformationByHandle`: if
  `dwFileAttributes` has `FILE_ATTRIBUTE_REPARSE_POINT`, close the handle and return `LogOpenStatus.NotFound`.
  The existing final-path check stays.
- [ ] **Step 3:** Build with `vm-agent/build.sh --allow-dirty`. Run `--selftest` in the VM at
  the next deploy (Task 22). Commit `Agent: /logs/read opens links as links and refuses them (F14)`.

### Task 10: a failed apiEnd shows in /health (F15)

**Files:** `ApiModels.cs` (`EdiabasInfo` gains `bool HeldOpen`), `EdiabasService.cs`
(expose `_handleMayBeOpen`), health handler; Mac `agentClient.ts`, `format.ts`. Tests:
`EdiabasReleaseAndCableTests.cs`, `format.test.ts`.

- [ ] **Step 1: Write the failing tests:**
  - `ApiEndThrows_HealthReportsHeldOpen`;
  - TS: `heldOpen: true` prints `EDIABAS: held open after a failed apiEnd; the interface stays in use until the agent restarts (restart Windows or the scheduled task).`
- [ ] **Step 2:** Implement and run all tests. Expected: PASS.
- [ ] **Step 3:** Commit `apiEnd failure visible in vm_health (F15)`.

### Task 11: a capture never waits on a window that stopped responding (F16)

**Files:** `vm-agent/EdiabasAgent/Capture/WindowCapture.cs:97-107`, `CaptureNative.cs`.

- [ ] **Step 1: Implement.**
  - Before `PrintWindow`, call `SendMessageTimeoutW(hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out _)`.
  - If it returns 0, skip `PrintWindow` and use the screen copy, as for `IsHungAppWindow`.
- [ ] **Step 2:** Check at the desk (Task 22): screenshot INPA while it loads a script. Commit
  `Agent: screenshot falls back to the screen copy for a window that does not answer in 500 ms (F16)`.

### Task 12: remove NotInJobList (D11)

**Files:** `EdiabasService.cs` (remove `NotInJobList`, `IsPrgFile`; `RunItem` returns `run.Failure`).
Mac `errors.ts`: the `ediabas_error` text for a run adds
`If the job name may be wrong, check it with ediabas_describe.` Tests: delete the `job_not_found`
run tests in `EdiabasRequestPathTests.cs`/`EdiabasServiceTests.cs`; add `RunJob_EdiabasError_NoExtraJobsCall`
(after a failing job, `_api.Calls` has no entry starting `"Job NVE_65 _JOBS"`); TS test for the hint.

- [ ] **Step 1:** Write the new tests. Run them. Expected: FAIL.
- [ ] **Step 2:** Remove the code. Run all tests. Expected: PASS. `job_not_found` stays for describe.
- [ ] **Step 3:** Update `docs/api.md` and `docs/tools.md`. Commit `Agent: a job the SGBD lacks is ediabas_error again; no extra _JOBS call (D11)`.

### Task 13: vehicle status built on the Mac (D10)

**Files:**
- Agent: remove `Vehicle` from `EdiabasService.cs:252-288`, the `/ediabas/vehicle` route in
  `EdiabasEndpoints.cs` and `Endpoints.cs`, `VoltageSource`/`VoltageSources` in `AgentConfig.cs`
  (an existing key is a warning `voltageSources is no longer used; set it on the Mac`), the
  `VehicleResponse` models and their tests.
- Mac: `config.ts` (`voltageSources: {ecu, job}[]` from `config.local.json`, validated by the
  policy name rules), `agentClient.ts` (remove `vehicle()`, `VehicleSchema`), `server.ts`
  (`ediabas_vehicle_status` handler), `formatEdiabas.ts`, and the `vehicle` recordings in
  `dev/replay-agent.ts` and the fixtures (re-record as an `/ediabas/jobs` exchange or convert in
  the replay agent). Tests: `tools.test.ts`, `config.test.ts`, `formatEdiabas.test.ts`, `dev/test/replay.test.ts`.

**Interfaces:** Mac `function vehicleBatch(sources: {ecu: string; job: string}[]): {ecu: string; job: string}[]`
in `formatEdiabas.ts` (or a new `vehicle.ts`). It returns the D10 order. The formatter reads
items by that order, so the output text is the same as today's.

- [ ] **Step 1: Write the failing tests:**
  - `vehicleBatch([]) → 4 items in D10 order`;
  - with one source, it is placed 3rd;
  - `ediabas_vehicle_status` against the stub sends exactly one `POST /ediabas/jobs` and prints
    the same text as the current vehicle test's expected output;
  - a `voltageSources` entry refused by the policy is a config error at start-up.
- [ ] **Step 2:** Implement the Mac side. Run the Mac tests. Expected: PASS.
- [ ] **Step 3:** Remove the agent side. C#: add `Config_VoltageSourcesKey_IsWarningNotError`. Run all tests. Expected: PASS.
- [ ] **Step 4:** Update `docs/api.md`, `docs/tools.md`, `docs/at-the-car.md` (voltages need
  `voltageSources` in `config.local.json`), `SKILL.md` and `server.ts` instructions ("ignition, and
  voltages when configured"). Commit `ediabas_vehicle_status runs as one job batch from the Mac; /ediabas/vehicle removed (D10)`.

## Part 2: Mac server

### Task 14: "never sent" only when the connect failed (F8, D7)

**Files:** `mac-mcp/src/agentClient.ts:232-250`. Test: `mac-mcp/test/agentClient.test.ts`.

- [ ] **Step 1: Write the failing tests:**
  - `runJob: EHOSTUNREACH with syscall 'write' → ediabas_request_lost, fetch called once`;
  - `runJob: EHOSTUNREACH with syscall 'connect' → rediscovers and sends once more` (existing behaviour, with `syscall` now set).
- [ ] **Step 2:** Run them. Expected: the first fails.
- [ ] **Step 3: Implement.** `neverSent = code === "ECONNREFUSED" || code === "UND_ERR_CONNECT_TIMEOUT" || (UNREACHABLE.has(code) && cause.syscall === "connect")`.
  Kind stays `unreachable` for the message.
- [ ] **Step 4:** Run all Mac tests. Expected: PASS. Commit `Mac: a job is resent only if the connect itself failed (F8)`.

### Task 15a: `/hello` identity proof in the agent (D5)

**Files:** `vm-agent/EdiabasAgent.Core/TokenAuth.cs` (add `string HelloProof(string nonce)`),
`vm-agent/EdiabasAgent/Middleware.cs` (`/hello` exempt from the token, not from the source check),
`Endpoints.cs` (route), `RequestParsing.cs` (nonce: exactly 32 of `[0-9a-f]`). Tests:
`TokenAuthTests.cs`, `RequestParsingTests.cs`.

**Interfaces:** `GET /hello?nonce=<32 hex>` returns 200 `{"agent":"ediabas-bridge-agent","proof":"<64 hex>"}`.
A bad nonce gives 400 `bad_request`. It is audited like every request.

- [ ] **Step 1: Write the failing tests:**
  - `HelloProof_KnownVector`: token `"test-token"`, nonce `"00112233445566778899aabbccddeeff"`.
    Expected value computed in the test as HMAC-SHA256(SHA-256(UTF-8 "test-token"), UTF-8 "ediabas-bridge-hello-v1:" + nonce), lowercase hex. Pin the literal hex in the test too.
  - `Nonce_Shape`: 31 or 33 characters, upper-case, or missing are refused.
- [ ] **Step 2:** Run them. Expected: FAIL. Implement. Run them. Expected: PASS.
- [ ] **Step 3:** Document in `docs/api.md` (the only unauthenticated route; why it is safe: the
  source check, the proof reveals nothing about the token). Commit `Agent: GET /hello proves the agent holds the token (D5)`.

### Task 15b: the Mac checks identity before it sends the token; the cache is dropped on non-agent answers (F6, F7, D5)

**Files:** `mac-mcp/src/agentClient.ts` (`probe`, `callNow`, `request`), `mac-mcp/src/token.ts`
(`helloKey(): Promise<Buffer>` = SHA-256 of the token), `test/stubAgent.ts` (answer `/hello`).
Test: `agentClient.test.ts`.

- [ ] **Step 1: Write the failing tests:**
  - `probe: an impostor answering /hello with a wrong proof never receives an Authorization header`
    (assert on every request the fake fetch saw);
  - `probe: an impostor answering 404 to /hello → wrong_service, no Authorization sent`;
  - `cached address now answers 404 without the agent's error shape → wrong_service, cache invalidated, next call rediscovers`
    (assert `locator.resolve` hits discovery again);
  - `cached address answers 200 with JSON that fails the schema → bad_response, cache invalidated`;
  - the matching Review Focus 3 case: the second call to an impostor at the cached address sends no Authorization.
- [ ] **Step 2:** Run them. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - `probe(baseUrl)` sends `GET /hello?nonce=` with `randomBytes(16).toString("hex")`, **without**
    `Authorization`. It checks `agent === AGENT_ID` and compares `proof` with `timingSafeEqual`
    against the HMAC from `helloKey()`.
  - A wrong proof is `wrong_service`. On success the authenticated request follows.
  - In `callNow`, on a `BridgeError` with code `wrong_service` or `bad_response`, call
    `locator.invalidate()` before rethrowing.
  - In `request`, a non-200 whose body is not the agent's error shape throws `wrongService` (as `probe` does).
- [ ] **Step 4:** Run all Mac tests and the dev tests (the replay agent must answer `/hello`:
  compute the proof from the replay token). Expected: PASS.
- [ ] **Step 5:** Update `docs/architecture.md` steps 3-4 and `docs/safety.md` rule 6. Commit
  `Mac: the token goes only to an address that proved it is the agent; a non-agent answer drops the cache (F6, F7)`.

### Task 16: discovery from the newest lease only (D6)

**Files:** `mac-mcp/src/vmAddress.ts` (remove `parseArp`, `arpCandidates`, `parseUtmctl`, `rank`,
`ARP_PATH`, the `utmctl` source; `leaseCandidates` returns at most the newest lease for the MAC;
`bridgeSubnets` keeps only `/^bridge1\d\d$/`), `config.ts` (`utmctlPath` only if still used for
the MAC lookup, otherwise removed). Tests: `vmAddress.parse.test.ts`, `agentClient.test.ts`.

**Interfaces:** `leaseCandidates(text: string, mac: string): string[]` returns `[]` or `[ip]`. The
newest lease is the one with the largest `lease` field (hex expiry), as `serve.py` reads it in
`dev/serve.py:180-208`; match that logic exactly.

- [ ] **Step 1: Write the failing tests:**
  - two leases for the MAC give only the newer one;
  - `bridge0` with an address in the lease's subnet is not on-link;
  - `bridge100` is on-link.
- [ ] **Step 2:** Run them. Expected: FAIL. Implement, deleting the removed functions' tests.
- [ ] **Step 3:** Run all tests. Expected: PASS. Update the five places that describe discovery
  (README, `docs/architecture.md`, `docs/deploy.md`, `mac-mcp/README.md`, the `serve.py`
  docstring) to one sentence plus a link to `docs/architecture.md`. Commit `Mac: VM found from its newest DHCP lease only (D6)`.

### Task 17: recorder moves to dev (D12)

**Files:** Move `mac-mcp/src/recorder.ts` to `dev/recorder.ts`. Create `dev/record-server.ts`.
Modify `mac-mcp/src/server.ts` (remove the recorder wiring), `agentClient.ts` (remove the
`recorder` option and its calls), `config.ts` (remove `recordDir`), `docs/development.md`
(how to record: register `npx tsx dev/record-server.ts` as the MCP command for a recording
session). Tests: move the recorder tests to `dev/test/`.

**Interfaces:** `dev/record-server.ts` calls `createAppServer(loadConfig(env), { ...defaultDeps(), fetch: recordingFetch(dir, redact) })`.
`recordingFetch` writes the same `Exchange` files as today; check against `dev/test/scrub.test.ts`
inputs. Export `defaultDeps` from `mac-mcp/src/index.ts` if it is not already exported.

- [ ] **Step 1:** Write `dev/test/record-server.test.ts`: a `recordingFetch` around the stub writes
  one file per exchange, and no file contains `Authorization` or the token. Run it. Expected: FAIL.
- [ ] **Step 2:** Implement and move. Run all tests. Expected: PASS. `grep -rn RECORD mac-mcp/src` finds nothing.
- [ ] **Step 3:** Commit `Recorder lives in dev/; the production server never writes exchanges (D12)`.

### Task 18: nits

**Files:** `formatEdiabas.ts:50-53`, `errors.ts:97`. Tests: `formatEdiabas.test.ts`, `errors` cases in `tools.test.ts`.

- [ ] **Step 1: Write the failing tests:**
  - `isKmName("STAT_GESCHWINDIGKEIT_KM_PRO_H") === false`; `isKmName("STAT_KM_STAND") === true`;
  - `wrongService("http://u:p@1.2.3.4:8765")` does not contain `u:p`.
- [ ] **Step 2:** Implement. A `KM` token directly followed by `PRO`, `H` or `STD` is not a
  distance; `wrongService` uses `hostPort`. Run all tests. Expected: PASS. Commit `Mac: km/h fields are not distances; no URL credentials in messages`.

## Part 3: deploy

### Task 19: safe update (F10, F13 merge)

**Files:** `vm-setup/update.ps1`, `vm-setup/install.ps1` (shared block :96-187 and the firewall
step :428-432). The shared-block test must still pass (`dev/test_serve.py` checks that the copies match).

- [ ] **Step 1:** Move the firewall rule creation into the shared block as `Set-FirewallRule([int]$Port)`.
  It reads the current default gateway the way install does. Install and update both call it.
- [ ] **Step 2: Update sequence:**
  1. `Disable-ScheduledTask`.
  2. `Stop-Agent`.
  3. Copy the current exe to `EdiabasAgent.exe.bak` (hash-checked).
  4. `Copy-VerifiedExe`. On any failure, restore `.bak` with `Copy-VerifiedExe` and say so; never leave no exe.
  5. Merge the default conflict names into `config.json`: add any of the 7 defaults missing from
     `conflictProcesses`, write the file back with the same encoding and indentation, and print
     the names added.
  6. `Set-FirewallRule`.
  7. `Enable-ScheduledTask`, then `Start-ScheduledTask`.
  8. Poll `/health` from inside the VM, as install does at :460-490, for 60 s. On failure,
     restore `.bak`, start it again, and print `Rolled back to <old version>`.
- [ ] **Step 3:** `Copy-VerifiedExe`'s hash-mismatch branch no longer deletes the target. It
  returns `$false`, and the caller restores `.bak`.
- [ ] **Step 4:** Verify with `pwsh -NoProfile -Command` parse (`./release.sh --no-build` does
  this) and `python3 -m unittest dev/test_serve.py`. In the VM (Task 22) run Update twice; the
  second run is a no-op update that must succeed.
- [ ] **Step 5:** Commit `update.ps1: backup, rollback on a bad copy or failed start, task disabled during the copy, firewall re-pinned, conflict defaults merged (F10, F13)`.

### Task 20: release builds the skill zips

**Files:** `release.sh`, README "Releasing".

- [ ] **Step 1:** `release.sh` runs `skills/install.sh --no-link` after the agent zip. It lists
  `skills/dist/*.zip` with SHA-256s in its summary.
- [ ] **Step 2:** README: the release step attaches the files `release.sh` printed. Add one
  sentence: "The `.sha256` checks that the download is intact, not who made it."
- [ ] **Step 3:** Run `./release.sh --dry-run`. Expected: the skill zips are listed. Commit `release.sh builds the skill zips it tells you to attach`.

## Part 4: docs and release

### Task 21: leaner docs (F17)

**Files:** see the steps. Content moves; facts do not change.

- [ ] **Step 1:** Move `PLAN.md`, `docs/plan-v1-original.md`, `docs/plan-review.md`,
  `docs/plan-review-fixes.md` and `docs/phase-results.md` to `docs/history/` with `git mv`. Each
  has a one-line banner pointing to the README Status. Fix every link (`grep -rn 'PLAN.md\|plan-review\|phase-results\|plan-v1'`).
- [ ] **Step 2:** In `docs/history/*`, replace the owner's first name with "the owner", to meet
  0.4.0's "no owner name" claim. Check with `grep -rni ryan docs README.md skills mac-mcp vm-agent vm-setup policy dev`;
  expected no hits outside `.git`.
- [ ] **Step 3:** Create `docs/install.md` from the README's Setup, Updating, Uninstalling and
  Troubleshooting sections, `docs/deploy.md`'s operator material and `vm-setup/README.md`
  (which becomes a 5-line pointer). `docs/deploy.md` keeps only the wire contract.
- [ ] **Step 4:** Make the README about 200 lines: what it is, which cars, Status, quick start, docs map.
  - Its Safety section links to `docs/safety.md` and does not repeat it.
  - `docs/safety.md` points to its own table, not PLAN §2.
  - The release steps move to `docs/development.md`.
  - `dev/README.md` becomes a pointer.
- [ ] **Step 5:** Error codes live in `docs/tools.md` only. The README troubleshooting and
  `skills/ediabas-bridge/references/errors.md` link to it, and the skill keeps only what to tell
  the user. Add a test to `mac-mcp/test/` that every code in `errors.ts`'s union appears in `docs/tools.md`.
- [ ] **Step 6:** Move `docs/bmw-e61-skill-addition.md` to `skills/`.
  - SKILL.md says the car-session clock resets after 5 min without a read.
  - SKILL.md and the `server.ts` instructions match D10's vehicle wording.
  - Settle the `maxJobTimeoutSeconds` wording: the agent keeps running and re-reads the config every 30 s (`Program.cs:28`).
- [ ] **Step 7:** Check that every relative link resolves (for example with a short script over
  `**/*.md`). Commit `Docs: history folder, install.md, one home for error codes and discovery (F17)`.

### Task 22: 0.5.0, migration and desk checks (owner in the VM, no car)

- [ ] **Step 1: Before updating the VM (owner):** with 0.4.0 still installed and the cable in the
  VM but not in the car, run `ediabas_describe` on each of the 5 unpinned approvals. Replace
  each entry in `C:\ProgramData\EdiabasBridge\config.json` with the printed `ECU:JOB@hash` line.
  Expected: `vm_health` shows 5 pinned, 0 unpinned.
- [ ] **Step 2:** Set `0.5.0` in `VERSION`, `mac-mcp/package.json` and `vm-agent/Directory.Build.props`.
  Add "What changed in 0.5.0" to the README Status. Run `./release.sh`. Expected: all suites pass and the zip is built.
- [ ] **Step 3:** Deploy with the README's Update line. Then check:
  - `vm_health`: both at 0.5.0, policy v4 on both sides, the hashes match, 5 pinned, 0 ignored;
  - `--selftest` in the VM: PASS, including the new link-to-device case;
  - a no-op second Update succeeds.
- [ ] **Step 4:** Desk checks (cable not in the car, confirmed by the owner):
  - `TMODE INFO` runs.
  - `ediabas_vehicle_status` returns the TMODE lines.
  - Open INPA during a `TMODE _JOBS` describe → `cable_in_use`/`not_run`.
  - Edit `reviewedJobs` in the VM → the next `ediabas_*` result starts `[approvals changed]`.
  - Screenshot of INPA loading.
  - `ediabas_describe E60` shows the two whole-car jobs allowed (the open 0.4.0 item).
  - Re-record the skill references' job verdicts under policy v4.
- [ ] **Step 5:** Update the README Status with what passed and commit `0.5.0`.
