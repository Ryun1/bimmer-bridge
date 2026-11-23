# Review fixes and simplifications (0.5.0): design

Source: the whole-project review of 2026-11-22 (VM agent, Mac server, deploy, docs). This is
the spec for `docs/superpowers/plans/2026-11-22-review-fixes.md`. Nothing here runs against the
car; the at-the-car phase (6) and the car-recorded evals stay the owner's and are out of scope.

## Findings this release fixes

| # | Finding | Where |
|---|---|---|
| F1 | A BMW tool that starts while a job runs is not noticed until the next job (up to 60 s) | `EdiabasService.Execute` poll loop |
| F2 | No conflict check between loading `api32.dll` (~10 s) and `apiInit` | `Session.EnsureInit` |
| F3 | `reviewedJobs` can be written by any VM process (UAC off) and applies silently; the Mac cannot see it | `ApprovalReloader`, `/health` |
| F4 | A failed read of `config.json` records the new timestamp, so a removed approval can stay in force | `ApprovalReloader.Refresh` |
| F5 | Unpinned approvals skip both screens whatever the description says; pinned ones skip the argument screen | `ScreenJob`, `CheckPin` |
| F6 | Discovery sends the Bearer token to candidates before they prove they are the agent; spoofable sources (ARP, utmctl, old leases, any `bridge*`) | `agentClient.probe`, `vmAddress` |
| F7 | A cached address is dropped only on transport failures; a different service on it keeps getting the token | `AgentClient.callNow` |
| F8 | "Unreachable" errors count as never sent at any phase, so a job could be sent twice | `agentClient.classify` |
| F9 | `release.sh` / `build.sh` refuse untracked files, so a release cannot be built now | `release.sh`, `vm-agent/build.sh` |
| F10 | `update.ps1` keeps no backup, deletes a bad copy leaving no exe, does not health-check, races the task's repeat trigger, and leaves the firewall pinned to the install-time gateway | `vm-setup/update.ps1` |
| F11 | Description screen lacks switching / triggering terms; only E60 among chassis SGBDs is restricted | `policy/policy.json` |
| F12 | Only `version` is compared between the Mac's and the agent's policy | `/health`, `format.ts` |
| F13 | Default `conflictProcesses` lacks `WINKFP` and `ISTAGUI` | `AgentConfig`, `install.ps1` |
| F14 | `/logs/read` follows a link before its final check (a link to `\??\COM3` toggles the cable's lines) | `LogAccess.OpenRead` |
| F15 | A failed `apiEnd` keeps EDIABAS loaded until restart, invisibly | `Session.Dispose`, `/health` |
| F16 | `PrintWindow` on a window that stops pumping holds the one capture slot | `WindowCapture` |
| F17 | Docs: contradictions on folder permissions, duplication, stale review checklist, owner name left in history docs, publishing step needs zips `release.sh` does not build | docs, README |

Nits: `isKmName` treats `KM` speed fields as distances; `wrongService` prints the whole
`EDIABAS_AGENT_URL` including any `user:pass@`.

## Decisions

- **D1 Approvals stay in the VM**, where Claude has no write path, but become visible. `/health`
  returns the approval entries and a hash of them. `vm_health` lists them. The Mac server
  remembers the hash it first saw in its process, and when the hash changes it starts every
  `ediabas_*` result with `[approvals changed]` and lists the new entries. It also writes an
  audit line.
- **D2 Unpinned entries approve nothing.** They are a warning in `agent.log`, counted as
  `ignored` in `/health`. Before deploying 0.5.0 the owner replaces the 5 unpinned entries in
  the VM with pinned ones, using 0.4.0's `ediabas_describe` (cable in the VM, not in the car).
- **D3 A pinned approval skips the description screen only.** A call with arguments always goes
  through the argument screen.
- **D4 Conflict check while a job runs.** The poll loop runs the check at most every
  `ConflictRecheckMs` (250 ms). On a hit it calls `apiBreak` (through `BreakAndSettle`) and
  returns `not_run` naming the tool. It is checked once more after `TryLoad` and before
  `apiInit`. `RunItem`'s `recheckConflicts` argument goes, because `Execute` already checks
  before every job.
- **D5 Identity before token.** A new route `GET /hello?nonce=<32 lowercase hex>` needs no token
  but keeps the source-address check and is audited. It returns
  `{"agent":"ediabas-bridge-agent","proof":"<64 lowercase hex>"}`, where
  `proof = HMAC-SHA256(key = SHA-256(UTF-8 token), message = "ediabas-bridge-hello-v1:" + nonce)`.
  The agent already holds `SHA-256(token)`. The Mac probes with `/hello` and a fresh nonce, and
  checks the proof in constant time before it sends any Bearer request. The cached address is
  dropped on `wrong_service`, `bad_response`, or a non-200 that is not in the agent's error shape.
- **D6 Discovery uses the newest DHCP lease for the VM's MAC only**, the way `serve.py`
  already does, plus the `EDIABAS_AGENT_URL` override. ARP, `utmctl`, `rank()` and "every lease
  ever" go. Only `bridge1NN` interfaces (UTM's shared network) count as on-link.
- **D7 "Never sent" means the connect step failed**: `cause.syscall === "connect"`,
  `UND_ERR_CONNECT_TIMEOUT`, or `ECONNREFUSED`.
- **D8 Policy version 4.**
  - Screen terms added: `schaltet`, `einschalt`, `ausschalt`, `umschalt`, `ausloes`, `auslös`,
    `trigger`, `oeffnen`, `öffnen`.
  - Chassis functional SGBDs that exist in the VM's Ecu folder are restricted with E60's list.
    Candidates: `E65`, `E70`, `E83`, `E85`, `E87`, `E89`, `E90`, `F01`, `F10`, `F25`, `R56`.
    Only names confirmed with `ediabas_list_ecus` go in, per PLAN.md §12.
  - False positives are safe (they become `job_needs_review`). Jobs that flip are recorded from
    the describe fixtures.
- **D9 Policy hash.** `/health` adds `policySha256`, the lowercase hex SHA-256 of the embedded
  `policy.json` bytes. The Mac hashes the bytes it read. If the field is present and differs,
  every `ediabas_*` tool refuses with `[policy_mismatch]`. If it is absent (an older agent),
  there is only the existing version warning.
- **D10 Vehicle status moves to the Mac.** `ediabas_vehicle_status` sends one `/ediabas/jobs`
  batch: `UTILITY STATUS_ZUENDUNG`, `UTILITY STATUS_UBATT`, the Mac's `voltageSources` (from
  `config.local.json`, default none), then `TMODE LESE_SPANNUNG_KL30` and
  `TMODE LESE_SPANNUNG_KL15`. The agent's `/ediabas/vehicle` route, its `Vehicle` method,
  `VoltageSource` and `voltageSources` are removed. An existing `voltageSources` key in the
  VM's `config.json` becomes a warning, not an error.
- **D11 `NotInJobList` is removed.** A job the SGBD lacks returns `ediabas_error`. The Mac's
  message for `ediabas_error` on a run says to check the job name with `ediabas_describe`.
- **D12 The recorder moves to `dev/`.** A dev entry point, `dev/record-server.ts`, builds the
  server with a recording `fetch` passed in through `AppDeps.fetch`. `EDIABAS_RECORD_DIR` leaves
  the production config.
- **D13 Version 0.5.0** for the agent, the Mac server and `VERSION`. A Mac server at 0.5.0
  needs an agent at 0.5.0 (`/hello`, `policySha256`, no `/ediabas/vehicle`); `vm_health`
  already warns on a version difference.

## Not done, and why

- **Removing the Mac parser's strictness rules** (about 90 lines). The shared parse cases keep
  the two parsers equal at little cost, and D9 catches drift either way.
- **Turning UAC on in the VM.** That is the owner's choice about their VM. D1 makes approval
  changes visible instead, and the docs say plainly that folder permissions do not constrain
  anything while UAC is off.
- **Signing release zips.** There is one owner. The docs will say that the `.sha256` checks
  integrity, not origin.
