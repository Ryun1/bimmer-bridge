# What changed in 0.3.0 and 0.4.0

> Historical record: the release notes of earlier versions, moved from the README. For the current state, see the [README's Status](../../README.md#status).

## What changed in 0.4.0

- Usable by anyone with an EDIABAS-supported BMW: no owner name in messages, docs or skills;
  see [Which cars](../../README.md#which-cars).
- The VM's MAC has no built-in default: it is read from UTM's config for the VM named
  `Windows` (or `vmName`), with an optional git-ignored `config.local.json` (`vmName`, `vmMac`,
  `agentPort`) and the `EDIABAS_VM_*` environment variables as overrides.
- The agent's `voltageSources` default is empty (it was the E60/E61 night-vision module);
  existing `config.json` files are unchanged.
- `discover.ps1` no longer checks for E61-specific SGBD files.

## What changed in 0.3.0

From the review round recorded in [plan-review-fixes.md](plan-review-fixes.md):

- Policy version 3: `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` (whole-car ident and
  faults), `C_FA_LESEN` (vehicle order), more description-screen terms, stricter parse rules.
- New tool `ediabas_jobs`: up to 20 read jobs in one call.
- Every error starts with its `[code]`; new codes `ediabas_request_lost` and `ediabas_busy`.
- EDIABAS calls leave the Mac one at a time; a connect timeout counts as never sent.
- Car-facing results end with a car-session line (time since the first read from the car).
- `vm_health` shows the Mac server's version beside the agent's, and warns when they differ.
- Approvals in `reviewedJobs` can be pinned to the job's description (`ECU:JOB@hash`);
  `ediabas_describe` prints the line, and the agent re-reads them without a restart.
- Job arguments go through the description screen; a job the SGBD lacks is `job_not_found`.
- Queue time counts against a request's time limit; unreadable results get their own message.
- Screenshot errors `capture_busy` and `capture_failed`; one 5 s limit per log grep; one set of
  log roots for listing and reading.
- No tool result exceeds 60 000 characters.
- Deploy: the token is never printed; uninstall keeps data unless `$Purge=$true`;
  `release.sh` refuses a dirty tree and the zip carries `commit.txt`.

