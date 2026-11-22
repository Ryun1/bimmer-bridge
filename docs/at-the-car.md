# At the car: session checklist

For Phase 6 and any later session that reads the car. This is Ryan's: nothing that talks to
the car runs unattended. The car has a battery-drain fault, so ignition-on time is the scarce
resource: do everything you can before the ignition goes on, and keep it under 10 minutes.

The Phase 6 acceptance, from [PLAN.md section 9](../PLAN.md#9-build-phases-and-acceptance):
ignition and a voltage in volts are shown; `IDENT` through `D_MOTOR` names the engine SGBD;
`ediabas_read_faults` on `NVE_65` shows the A752/A751 pair from the INPA session of
11/09/2026; total ignition-on time under 10 minutes; responses saved as fixtures for the
replay agent.

Nothing added in 0.3.0 has been run at the car yet: the whole-car read, `ediabas_jobs`, the
car-session line and the longer idle time below are all untested there.

## Before going to the car

- [ ] `./release.sh` passes and the VM runs the current agent. `vm_health` shows the Mac
      server's version and the agent's: they must agree, with no policy-version warning. If
      not, [update](../README.md#updating) and restart Claude first.
- [ ] For the session, set `"ediabasIdleReleaseSeconds": 90` in the VM's
      `C:\ProgramData\EdiabasBridge\config.json` (allowed 5 to 600, default 20), so EDIABAS is
      not unloaded and reloaded after every pause between reads. Unlike `reviewedJobs`, this
      key is read only when the agent starts: restart Windows in the VM afterwards. While it is
      90, the bridge keeps EDIABAS loaded for 90 s after its last call.
- [ ] To record fixtures, register or start the MCP server with `EDIABAS_RECORD_DIR` set to a
      folder **outside the repo**, for example `~/ediabas-recordings/nve-faults`, one folder
      per scenario ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
      Restart Claude so the server picks it up.
- [ ] Know which scenarios to record at the car: `nve-faults` (evals 01 and 03) and `battery`
      (eval 06). `needs-review` (eval 04) does not need the car; record it at the desk. What
      each needs is in [dev/fixtures/README.md](../dev/fixtures/README.md#scenarios-the-evals-use-skillsevals).

## At the car, ignition still off

- [ ] Put a battery charger or support unit on the car for the whole session (general
      knowledge, not something recorded on this car): it takes away most of the time pressure
      and avoids communication errors from low voltage.
- [ ] Start the `Windows` VM in UTM and log in (the agent runs in the logged-in session).
- [ ] Plug the K+DCAN cable into the Mac and the car, then **tick the cable for the VM in UTM's
      USB menu**. UTM does not hand a plugged-in or replugged cable to the VM by itself; until
      it is ticked, EDIABAS answers `cable_not_found` (error 28, `IFH-0018`) or error 16
      `IFH-0006: COMMAND NOT ACCEPTED` on every real SGBD.
- [ ] Close every BMW tool in the VM (INPA, Tool32, NCS Expert, WinKFP, BMW Coding Tool). While
      one is open, every EDIABAS call is refused with `cable_in_use`. Never close WinKFP or
      NCS Expert in the middle of a flash or coding run.
- [ ] Ask Claude "is the BMW VM up?": `vm_health` must answer with no BMW tool running.
- [ ] Look up names now, without the ignition: `ediabas_list_ecus` with a `filter`, then
      `ediabas_describe`, including `E60` for the whole-car jobs. Describing needs the cable in
      the VM but, for most files, not the car (desk, 2026-11-21).
  - Describe large SGBDs with a `filter` (a job-name substring such as `STATUS_`): a full
    describe of `CAS` or `LM_60` hit the 60 s limit and left its last jobs unscreened.
  - `N62_TUE2`, `ME9N62_2`, `GS19D` and `SZL_60` gave `ediabas_error` 100 `SYS-0010` at the
    desk and appear to need the module to answer. Leave them for the ignition-on part, and
    only if the session needs them.
- [ ] Agree the plan: which ECUs and jobs, in how many calls, and a rough time. The first
      EDIABAS call after a pause takes 10–15 s longer while the agent loads EDIABAS again
      (desk, 2026-11-21); with the idle time at 90 s that should happen once, not after every
      pause.

## Ignition on

- [ ] Switch the ignition on and note the time.
- [ ] `ediabas_vehicle_status` first. Its field names are not confirmed yet: note what
      ignition and voltage fields it returns, for the skill references.
- [ ] The whole-car read: `ediabas_job` on `E60` with `IDENT_FUNKTIONAL` (which modules
      answer) and `FS_LESEN_FUNKTIONAL` (their faults in one job), and `C_FA_LESEN` on `CAS` or
      `LM_60` (the vehicle order).
- [ ] The rest, batched: one `ediabas_read_faults` for the ECUs wanted (up to 20) and the
      status jobs in one `ediabas_jobs` call. For Phase 6: `IDENT` through `D_MOTOR`, and the
      fault memory of `NVE_65`.
- [ ] Watch the line at the end of each result: "Car session: … since the first read from the
      car (aim to finish within 10 min)." It counts from the first read, not from the
      ignition. At about 8 minutes Claude wraps up; at 10 it stops and asks before any further
      read. Stop even if reads are left; continue another day.
- [ ] If a job is refused with `job_needs_review`, note the quoted description. Approving it
      is Ryan's decision and never routine: `ediabas_describe` on the job prints the line to
      add to `reviewedJobs`, and the agent picks it up at the next call, no restart
      ([safety.md](safety.md#approving-one-job)). Decide away from the clock if in doubt.
- [ ] **Ignition off.** Note the total ignition-on time.
- [ ] Unplug the cable from the car's OBD port (general knowledge: a connected interface can
      keep the car's buses from going to sleep). Then take the charger off.

## Afterwards

- [ ] Set `ediabasIdleReleaseSeconds` back to 20 (or remove it) and restart Windows in the VM.
- [ ] Remove `EDIABAS_RECORD_DIR` from the MCP registration and restart Claude, so later
      sessions do not record.
- [ ] Scrub each recording folder into `dev/fixtures/<scenario>/` and review every file and
      screenshot ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
- [ ] Fill the skill reference entries that waited for the car (vehicle-status field names,
      the modules that answered, the engine SGBD, the SGBDs above) from what the session
      returned, in the same commit as the fixtures.
- [ ] Run evals 01, 03 and 06 against the new fixtures, and update the README's Status.
