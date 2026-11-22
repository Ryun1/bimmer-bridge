# At the car: session checklist

For Phase 6 and any later session that reads the car. The owner runs it, at the car: nothing
that talks to the car runs unattended. Ignition-on time drains the battery (and the car this was
built on has a battery-drain fault), so it is the scarce resource: do everything you can before
the ignition goes on, and keep it under 10 minutes.

The checklist was written for the 2005 E61 550i the bridge was built on. The general steps apply
to any car; the items marked E60/E61, and the Phase 6 acceptance below, are that car's. On
another car, find its modules and jobs with `ediabas_list_ecus` and `ediabas_describe` instead.

The Phase 6 acceptance (the E61's), from [PLAN.md section 9](history/PLAN.md#9-build-phases-and-acceptance):
ignition and a voltage in volts are shown; `IDENT` through `D_MOTOR` names the engine SGBD;
`ediabas_read_faults` on `NVE_65` shows the A752/A751 pair from the INPA session of
11/09/2026; total ignition-on time under 10 minutes; responses saved as fixtures for the
replay agent.

Nothing added in 0.3.0 has been run at the car yet: the whole-car read, `ediabas_jobs`, the
car-session line and the longer idle time below are all untested there.

## Before going to the car

- [ ] `./release.sh` passes and the VM runs the current agent. `vm_health` shows the Mac
      server's version and the agent's: they must agree, with no policy-version warning. If
      not, [update](install.md#updating) and restart Claude first.
- [ ] For the session, set `"ediabasIdleReleaseSeconds": 90` in the VM's
      `C:\ProgramData\EdiabasBridge\config.json` (allowed 5 to 600, default 20), so EDIABAS is
      not unloaded and reloaded after every pause between reads. Unlike `reviewedJobs`, this
      key is read only when the agent starts: restart Windows in the VM afterwards. While it is
      90, the bridge keeps EDIABAS loaded for 90 s after its last call.
- [ ] To record fixtures, register the recording server (`dev/record-server.ts`, see
      [development.md](development.md#recording-and-scrubbing)) in place of the production one, with `EDIABAS_RECORD_DIR` set to a
      folder **outside the repo**, for example `~/ediabas-recordings/car-<date>`. Use **one
      folder for the whole car session**: changing the folder means restarting Claude, which
      the ignition-on time cannot afford. Afterwards the folder is scrubbed once per scenario
      ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
      The commands (the server name goes **before** `-e`: `-e` takes several values and would
      swallow it, and the add then fails while the remove has already happened):

      ```sh
      claude mcp remove bimmer-bridge -s user
      claude mcp add --scope user bimmer-bridge -e EDIABAS_RECORD_DIR=$HOME/ediabas-recordings/car-<date> -- /opt/homebrew/bin/node --import "$PWD/dev/ts-resolve.mjs" "$PWD/dev/record-server.ts"
      claude mcp get bimmer-bridge   # must show record-server.ts and EDIABAS_RECORD_DIR
      ```

      Run them from the repo root, with the same `node` as the production registration (macOS's
      Local Network permission is tied to its path). Restart Claude, call `vm_health`, then
      **check the folder has a `GET-health` file**: a desk rehearsal on 2026-11-22 ran against
      the production server without anyone noticing, and recorded nothing. Afterwards:
      `claude mcp remove bimmer-bridge -s user && claude mcp add --scope user bimmer-bridge -- /opt/homebrew/bin/node "$PWD/mac-mcp/dist/index.js"`.
- [ ] Know which scenarios to record at the car: `nve-faults` (evals 01 and 03) and `battery`
      (eval 06). `needs-review` (eval 04) was recorded at the desk on 2026-11-22. What each
      needs is in [dev/fixtures/README.md](../dev/fixtures/README.md#scenarios-the-evals-use-skillsevals);
      between them, the session must include `ediabas_list_ecus` and `ediabas_describe` for
      the night-vision SGBD and the engine group, `ediabas_vehicle_status`,
      `ediabas_read_faults` (with `NVE_65` and at least one fault carrying a mileage, for eval
      01) and one `ediabas_jobs` batch.

## At the car, ignition still off

- [ ] Put a battery charger or support unit on the car for the whole session (general
      knowledge, not something recorded on this car): it takes away most of the time pressure
      and avoids communication errors from low voltage.
- [ ] Start the VM in UTM and log in (the agent runs in the logged-in session).
- [ ] Plug the K+DCAN cable into the Mac and the car, then **tick the cable for the VM in UTM's
      USB menu**. UTM does not hand a plugged-in or replugged cable to the VM by itself; until
      it is ticked, EDIABAS answers `cable_not_found` (error 28, `IFH-0018`) or error 16
      `IFH-0006: COMMAND NOT ACCEPTED` on every real SGBD.
- [ ] Keep the Mac awake for the whole session: on power, lid open. When it sleeps, UTM hands
      the cable back to the Mac; the bridge stays up but EDIABAS calls fail until the cable is
      ticked for the VM again (at the desk a `TMODE` job gave error 48, `IFH-0038`; other files
      may give other codes).
- [ ] Close every BMW tool in the VM (INPA, Tool32, NCS Expert, WinKFP, BMW Coding Tool). While
      one is open, every EDIABAS call is refused with `cable_in_use`. Never close WinKFP or
      NCS Expert in the middle of a flash or coding run.
- [ ] Ask Claude "is the BMW VM up?": `vm_health` must answer with no BMW tool running.
- [ ] Look up names now, without the ignition: `ediabas_list_ecus` with a `filter`, then
      `ediabas_describe`, including `E60` for the whole-car jobs on an E60/E61. Describing needs the cable in
      the VM but, for most files, not the car (desk, 2026-11-21).
  - Describe large SGBDs with a `filter` (a job-name substring such as `STATUS_`): on the E61
    a full describe of `CAS` or `LM_60` hit the 60 s limit and left its last jobs unscreened.
  - On the E61's VM, `N62_TUE2`, `ME9N62_2`, `GS19D` and `SZL_60` gave `ediabas_error` 100 `SYS-0010` at the
    desk and appear to need the module to answer. Leave them for the ignition-on part, and
    only if the session needs them. The engine group `D_MOTOR` gave error 13 `IFH-0003`
    (2026-11-22) for the same reason; its `IDENT` is allowed by name (`allow.global`), so
    it can be run at the car without describing it first.
  - Already described at the desk on 2026-11-22, with their result names in the skill's
    `job-cookbook.md`: `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL`, `C_FA_LESEN` on
    `CAS` and `LM_60`, `UTILITY` `STATUS_UBATT` and `STATUS_ZUENDUNG`, `NVE_65` `FS_LESEN`.
    Check `vm_health`'s policy version is still 4; if so, these need no describing again.
- [ ] Agree the plan: which ECUs and jobs, in how many calls, and a rough time. The first
      EDIABAS call after a pause takes 10–15 s longer while the agent loads EDIABAS again
      (desk, 2026-11-21); with the idle time at 90 s that should happen once, not after every
      pause.

## Ignition on

- [ ] Switch the ignition on and note the time.
- [ ] `ediabas_vehicle_status` first. Its field names are not confirmed yet: note what
      ignition and voltage fields it returns, for the skill references. Voltages from a module
      (e.g. `NVE_65` `STATUS_BORDNETZSPANNUNG` on an E60/E61 with night vision) need
      `voltageSources` in `config.local.json` on the Mac, set before Claude starts.
      `UTILITY` `STAT_UBATT` and the cable's KL30 do **not** show the car is awake: with the
      cable in the VM and no car (rehearsal, 2026-11-22) they read `STAT_UBATT=1` and KL30
      `E02E0000` (12000 read little-endian), values from the cable's own lines. The "voltage in
      volts" for the acceptance comes from the voltage source (`NVE_65`), not from these.
- [ ] On an E60/E61, the whole-car read: `ediabas_job` on `E60` with `IDENT_FUNKTIONAL` (which modules
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
      is the owner's decision and never routine: `ediabas_describe` on the job prints the line to
      add to `reviewedJobs`, and the agent picks it up at the next call, no restart
      ([safety.md](safety.md#approving-one-job)). Decide away from the clock if in doubt.
- [ ] **Ignition off.** Note the total ignition-on time.
- [ ] Unplug the cable from the car's OBD port (general knowledge: a connected interface can
      keep the car's buses from going to sleep). Then take the charger off.

## Afterwards

- [ ] Set `ediabasIdleReleaseSeconds` back to 20 (or remove it) and restart Windows in the VM.
- [ ] Register the production MCP server again (not `dev/record-server.ts`) and restart
      Claude, so later sessions do not record.
- [ ] Scrub the session's folder into each scenario, `dev/fixtures/nve-faults/` and
      `dev/fixtures/battery/` (one scrub per scenario name), then in each delete the exchanges
      that scenario does not need, and review every file and screenshot
      ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)). Check
      `FAHRZEUGAUFTRAG` (from `C_FA_LESEN`) by hand: the scrubber matches it only if it
      contains a VIN.
- [ ] Fill the skill reference entries that waited for the car (vehicle-status field names,
      the modules that answered, the engine SGBD, the SGBDs above) from what the session
      returned, in the same commit as the fixtures.
- [ ] Run evals 01, 03 and 06 against the new fixtures, and update the README's Status.
