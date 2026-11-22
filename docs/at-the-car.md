# At the car: session checklist

For Phase 6 and any later session that reads the car. This is Ryan's: nothing that talks to
the car runs unattended. The car has a battery-drain fault, so ignition-on time is the scarce
resource: do everything you can before the ignition goes on, and keep it under 10 minutes.

The Phase 6 acceptance, from [PLAN.md section 9](../PLAN.md#9-build-phases-and-acceptance):
ignition and a voltage in volts are shown; `IDENT` through `D_MOTOR` names the engine SGBD;
`ediabas_read_faults` on `NVE_65` shows the A752/A751 pair from the INPA session of
11/09/2026; total ignition-on time under 10 minutes; responses saved as fixtures for the
replay agent.

## Before going to the car

- [ ] `./release.sh` passes and the VM runs the current agent (`vm_health` shows the version,
      with no policy-version warning). If not, [update](../README.md#updating) first.
- [ ] To record fixtures, register or start the MCP server with `EDIABAS_RECORD_DIR` set to a
      folder **outside the repo**, for example `~/ediabas-recordings/nve-faults`, one folder
      per scenario ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
      Restart Claude so the server picks it up.
- [ ] Know which scenarios to record. Still missing: `nve-faults` (evals 01 and 03),
      `needs-review` (eval 04) and `battery` (eval 06). What each needs is in the table in
      [dev/fixtures/README.md](../dev/fixtures/README.md#scenarios-the-evals-use-skillsevals).

## At the car, ignition still off

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
      `ediabas_describe`. Most SGBDs describe with the cable in the VM and the car off.
  - Describe large SGBDs with a `filter` (a job-name substring such as `STATUS_`): a full
    describe of `CAS` or `LM_60` hits the 60 s limit and leaves its last jobs unscreened.
  - `N62_TUE2`, `ME9N62_2`, `GS19D` and `SZL_60` can only be described with the control unit
    answering (with the cable in the VM but not in the car they gave `ediabas_error` 100 `SYS-0010`). Leave them for the
    ignition-on part, and only if the session needs them.
- [ ] Agree the plan: which ECUs and jobs, in how many calls, and a rough time. Allow about
      10 s more for the first EDIABAS call after a pause (the agent reloads EDIABAS).

## Ignition on

- [ ] Switch the ignition on and note the time.
- [ ] `ediabas_vehicle_status` first. Its field names are not confirmed yet: note what
      ignition and voltage fields it returns, for the skill references.
- [ ] The reads, batched: one `ediabas_read_faults` for all ECUs wanted (up to 20), any
      `ediabas_job` calls back to back. For Phase 6: `IDENT` through `D_MOTOR`, and the fault
      memory of `NVE_65`.
- [ ] If a job is refused with `job_needs_review`, note the quoted description and decide
      later, away from the car: approving it needs a Windows restart
      ([safety.md](safety.md#approving-one-job)).
- [ ] Stop at 10 minutes even if reads are left; continue another day.
- [ ] **Ignition off.** Note the total ignition-on time.

## Afterwards

- [ ] Remove `EDIABAS_RECORD_DIR` from the MCP registration and restart Claude, so later
      sessions do not record.
- [ ] Scrub each recording folder into `dev/fixtures/<scenario>/` and review every file and
      screenshot ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
- [ ] Fill the skill reference entries that waited for the car (vehicle-status field names,
      the engine SGBD, the SGBDs above) from what the session returned, in the same commit as
      the fixtures.
- [ ] Run evals 01, 03, 04 and 06 against the new fixtures, and update the README's Status.
