# Safety: how read-only is enforced

The bridge is read-only by construction. The rules were first written in
[PLAN.md section 2](../PLAN.md#2-hard-safety-rules-non-negotiable), which is historical; the
table below is the current list and says which code enforces each one. They are enforced in code on both sides, not only in tool descriptions or
skills, which are guidance. Check any change against this list.

**Anything that runs against the car is run by the owner, at the car.** Claude Code may run only jobs that
cannot reach a module: the `TMODE` and `UTILITY` info jobs, and `ediabas_describe` (the `_JOBS`
family), after the owner confirms the cable is not plugged into the car (it may still be in the VM;
a real SGBD does not load without it).

## The rules and what enforces them

| # | Rule | Enforced in |
|---|---|---|
| 1 | No input into the VM: no click, keystroke, mouse or clipboard | There is no such route: the route table in `vm-agent/EdiabasAgent/Endpoints.cs` is the whole list. Capture never restores, focuses, moves or resizes a window (`vm-agent/EdiabasAgent/Capture/`). The Mac server registers only read-only tools, each marked `readOnlyHint: true` (`mac-mcp/src/server.ts`) |
| 2 | No writes to the car in v1: no clearing, coding, programming, activations or adaptations | No endpoint for them (`Endpoints.cs`); the job policy (rule 3) refuses them by name |
| 3 | EDIABAS is default-deny | `policy/policy.json`, applied by `vm-agent/EdiabasAgent.Core/JobPolicy.cs` (plus the description screen in `vm-agent/EdiabasAgent.Core/CommentScreen.cs`) and by `mac-mcp/src/policy.ts` before anything leaves the Mac. The agent embeds the file at build time; the Mac reads it at start-up. Both test suites run every case in `policy/policy-cases.json` and `policy/policy-parse-cases.json`; a policy change without a case change is a bug. Every job the agent sends, its own `_JOBS`, `_JOBCOMMENTS`, `_ARGUMENTS` and `_RESULTS` calls included, passes the policy first |
| 4 | One user of the cable at a time; no override | `vm-agent/EdiabasAgent/Proc/ConflictCheck.cs` and `vm-agent/EdiabasAgent.Core/ConflictMatcher.cs`: a process on `conflictProcesses` in the VM's `config.json`, or any other process with `api32.dll` loaded, holds the cable. `vm-agent/EdiabasAgent.Core/EdiabasService.cs` runs the check before a request opens EDIABAS, after loading the DLL (before `apiInit`), before every EDIABAS job in it and every 250 ms while a job runs (a check under 250 ms old is reused); a tool seen during a job stops it with `apiBreak`, and once a BMW tool appears, no further job starts |
| 5 | Release the interface when idle | One `apiInit` … `apiEnd` per request in `EdiabasService.cs`; the runner in `vm-agent/EdiabasAgent/Ediabas/EdiabasRunner.cs` unloads `api32.dll` after `ediabasIdleReleaseSeconds` (default 20) without an EDIABAS request, so `ebas32.exe` exits too. A job that ignores `apiBreak` is the exception: the agent then leaves EDIABAS alone (no `apiEnd`, no unload) and refuses every EDIABAS request until it is restarted; `/health` reports `ediabas.stuck` |
| 6 | Network exposure: bind to the shared-network address only, firewall to the Mac only, source-address check, Bearer token compared in constant time, token in the Keychain on the Mac | Bind: `vm-agent/EdiabasAgent.Core/BindAddressSelector.cs` (one literal IPv4 address, never a wildcard). Firewall rule: `vm-setup/install.ps1`. Source check (only the VM's gateway, which is the Mac): `vm-agent/EdiabasAgent/Middleware.cs`. Token: `vm-agent/EdiabasAgent.Core/TokenAuth.cs`. Mac: `mac-mcp/src/token.ts` (Keychain; never sent to an address set with `EDIABAS_AGENT_URL`) and `mac-mcp/src/vmAddress.ts` (only on-link addresses are contacted). `--selftest` checks bind, firewall and token permissions in the VM |
| 7 | Read-only file access under configured roots; the agent writes only its own logs | `vm-agent/EdiabasAgent.Core/PathRules.cs`, `vm-agent/EdiabasAgent.Core/LogService.cs` and `vm-agent/EdiabasAgent/Logs/LogAccess.cs`. `/logs/read` checks the path as written against the roots before it opens anything, then checks where the opened file really is ([api.md](api.md#logs)). Folder and token permissions: `vm-setup/install.ps1` |
| 8 | Never change the EDIABAS configuration: no edits to `EDIABAS.INI` or `obd.ini`, no `apiSetConfig` | `vm-agent/EdiabasAgent/Ediabas/Api32.cs` declares only what read jobs need, and not `apiSetConfig`; nothing in the agent writes those files |
| 9 | Audit trail on both sides | VM: `vm-agent/EdiabasAgent/Audit/AuditLog.cs` (format in `vm-agent/EdiabasAgent.Core/AuditLine.cs`). Mac: `mac-mcp/src/audit.ts`. See [architecture.md](architecture.md#logs-and-audit) |
| 10 | Never test against the car unattended | Not a code check: a working rule ([PLAN.md section 12](../PLAN.md#12-working-agreements-for-claude-code)), repeated in the skills. The owner runs everything at the car |

Two more limits follow from the timeouts: the agent refuses to start with
`maxJobTimeoutSeconds` above 60, and the Mac never sends a request that runs an EDIABAS job a
second time, so a job cannot run twice because a connection broke.

## The job policy in plain words

`policy/policy.json` decides, for an ECU (SGBD) name and a job name, whether the job may run.
The Mac checks it before sending anything; the agent checks it again before running anything.
In order, the first step that decides wins:

1. **Shape.** Names must be 1 to 64 letters, digits or `_`; arguments and result filters must
   be printable ASCII, at most 200 characters. Anything else is refused.
2. Names are upper-cased.
3. **Deny.** A job whose name contains a write-like substring (`SCHREIB`, `LOESCH`, `STEUER`,
   `CODIER`, `FLASH`, ...) or has a write-like word between underscores (`START`, `STOP`,
   `LERN`, `PROG`, ...) is refused, whatever else applies.
4. **Restricted ECUs.** `TMODE` and `UTILITY` contain dangerous jobs beside their reads, so
   only the jobs listed for them by name may run. `E60` is restricted the same way: only
   `INFO`, `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL` (whole-car ident and fault reads) and the
   `_` info jobs.
5. **Allowed by name.** A fixed list of reads (`IDENT`, `FS_LESEN`, `IS_LESEN`, the `_JOBS`
   family, ...) may run on any other ECU.
6. **Allowed by pattern.** Any other `STATUS_...` job may run only if its description passes
   the description screen (below).
7. Everything else is refused (`default_deny`).

The refusal names the rule that decided (for example `deny.substring:LOESCH`). The exact rules,
including how the file itself is validated, are in [api.md "Job policy"](api.md#job-policy).
How to change the policy safely: [policy/README.md](../policy/README.md).

## The description screen

A `STATUS_` name is not a guarantee of a pure read: some descriptions mention starting or
resetting something. So for a job allowed only by the pattern (step 6), the agent first reads
the job's own description from the SGBD (`_JOBCOMMENTS`) and refuses with `job_needs_review`,
quoting it, when:

- the description is empty, or only a label such as `Jobbeschreibung:`; or
- it contains a write-like term from `descriptionScreen.terms` (`start`, `schreib`, `loesch`,
  `setz`, `program`, `einstell`, `reset`, `write`, `clear`, ...), case-insensitively.

When such a job is called with arguments, the agent also reads the description of its
arguments (`_ARGUMENTS`) and screens every argument name and comment the same way. Arguments
with no description at all are refused too.

Before the terms are matched, the labels in `descriptionScreen.ignore` (`beschreibung`,
`umsetzer`) are removed, because they contain a term without meaning one: `Jobbeschreibung:`
starts every `CAS` description and contains `schreib`; `AD-Umsetzers` (an A/D converter)
contains `setz`. Nothing broader is ignored. Exact rules: [api.md](api.md#job-policy),
"Description screen". The Mac does not screen; it only knows that the agent will.

`ediabas_describe` shows the result per job: `passed`, `needs_review`, `reviewed`, or
`not_checked` when the description was not read. It gives the same answer a run would.

## Approving one job

A `job_needs_review` refusal is the owner's decision, made for one job at a time after reading its
description. The safe default is not to run it, and approving must never become routine. The
approval list lives only in the VM, in the agent's config, not in this repo.

An entry in `reviewedJobs` takes one of two forms:

- `"ECU:JOB@<12 hex>"`, pinned: approves the job only while its description is the one the
  owner read. If the SGBD's description changes, the job is refused again with `job_needs_review`.
  This is the recommended form.
- `"ECU:JOB"`, unpinned: approves the job by name, whatever its description says now or later.

Steps:

1. Run `ediabas_describe` on the job (with `job`). Under the description it prints
   `Description hash: <hash>`, and for a job that needs review the exact line to paste:
   `To approve this job as it is described now, the user adds "ECU:JOB@<hash>" to reviewedJobs in C:\ProgramData\EdiabasBridge\config.json in the VM (no restart needed).`
2. In the VM, open `C:\ProgramData\EdiabasBridge\config.json` as an administrator (the folder
   grants users read access only).
3. Add the entry to the `reviewedJobs` list, upper case, with the ECU name as it is asked for
   (not the variant a group name resolves to). The match is exact. For example
   `"reviewedJobs": ["NVE_65:STATUS_EXAMPLE@3f1c0a9b2d4e"]`. Save the file.
4. Nothing to restart. The agent re-reads `reviewedJobs` at its next EDIABAS request after
   config.json changed. `ediabas_describe` then shows the job as `reviewed`.

An entry of the wrong shape is a warning in `agent.log` and approves nothing; the agent keeps
running. If the saved file is not a valid config, the agent keeps the previous list and logs a
warning; the next good save is picked up. `vm_health` shows how many approvals of each form are
in force.

An approval skips only the description screen (and the argument screen), never the name rules:
a job refused by name stays refused, and an approval only applies to a job the policy allows by
the `STATUS_` pattern. The jobs approved so far are listed in the README's
[Troubleshooting](../README.md#troubleshooting) table.
