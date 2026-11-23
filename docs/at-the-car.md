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

First session at the car: 2026-11-23, version 0.6.2, ignition on for every read. Acceptance:
ignition and a voltage in volts shown (pass); `IDENT` through `D_MOTOR` names the engine SGBD
(pass, `N62_TUE`); `ediabas_read_faults` on `NVE_65` (read passed, but the A752/A751 pair was
absent from the car); ignition-on time under 10 minutes (fail, about 18 minutes, mostly a
Claude Desktop link drop of three minutes on mobile data); responses recorded as fixtures
(`nve-faults`, `battery`). The whole-car read, `ediabas_jobs`, the car-session line, voltage
sources, pinned approvals and the 90 s idle time all ran. Details:
[history/changes.md](history/changes.md#phase-6-at-the-car-2026-11-23).

## Before going to the car

- [ ] `./release.sh` passes and the VM runs the current agent. `vm_health` shows the Mac
      server's version and the agent's: they must agree, with no policy-version warning. If
      not, [update](install.md#updating) and restart Claude first.
- [ ] For the session, set `"ediabasIdleReleaseSeconds": 90` in the VM's
      `C:\ProgramData\EdiabasBridge\config.json` (allowed 5 to 600, default 20), so EDIABAS is
      not unloaded and reloaded after every pause between reads. Unlike `reviewedJobs`, this
      key is read only when the agent starts: restart Windows in the VM afterwards. While it is
      90, the bridge keeps EDIABAS loaded for 90 s after its last call.
- [ ] Claude needs a steady internet link at the car: a phone hotspot close to the Mac, or
      Wi-Fi. On 2026-11-23 mobile data dropped the Claude Desktop link for 3 minutes. If the
      tools disappear, switch the ignition off until they return.
- [ ] The Mac keeps a per-call log at `~/Library/Logs/ediabas-bridge/audit.log` (time, tool,
      outcome, milliseconds, ECU, job): it answers "was that call sent?" after a drop.
- [ ] To record fixtures, register the recording server (`dev/record-server.ts`, see
      [development.md](development.md#recording-and-scrubbing)) in place of the production one, in **each app used at the car** (Claude Code and Claude Desktop read their servers from different places), with `EDIABAS_RECORD_DIR` set to a
      folder **outside the repo**, for example `~/ediabas-recordings/car-YYYYMMDD`. Use **one
      folder for the whole car session**: changing the folder means restarting Claude, which
      the ignition-on time cannot afford. Afterwards the folder is scrubbed once per scenario
      ([dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario)).
      The commands (the server name goes **before** `-e`: `-e` takes several values and would
      swallow it, and the add then fails while the remove has already happened):

      ```sh
      claude mcp remove bimmer-bridge -s user
      claude mcp add --scope user bimmer-bridge -e EDIABAS_RECORD_DIR=$HOME/ediabas-recordings/car-YYYYMMDD -- /opt/homebrew/bin/node --import "$PWD/dev/ts-resolve.mjs" "$PWD/dev/record-server.ts"
      claude mcp get bimmer-bridge   # must show record-server.ts and EDIABAS_RECORD_DIR
      ```

      Replace `YYYYMMDD` with the date. If `claude mcp get bimmer-bridge` shows any
      `Environment` entries for the production server (such as `EDIABAS_AGENT_URL`), add each
      as another `-e KEY=value` after the name in both the recording and the restore command.
      Run them from the repo root, with the same `node` as the production registration (macOS's
      Local Network permission is tied to its path). For Claude Desktop, edit
      `~/Library/Application Support/Claude/claude_desktop_config.json` instead
      (`claude mcp` does not touch it), with `<repo>` the full path to this repo:

      ```json
      "bimmer-bridge": {
        "command": "/opt/homebrew/bin/node",
        "args": ["--import", "<repo>/dev/ts-resolve.mjs", "<repo>/dev/record-server.ts"],
        "env": { "EDIABAS_RECORD_DIR": "<recordings folder>" }
      }
      ```

      Quit Desktop with Cmd+Q and reopen it to pick the entry up. Restart Claude, call `vm_health`, then
      **check the folder has a `GET-health` file**: a desk rehearsal on 2026-11-22 ran against
      the production server without anyone noticing, and recorded nothing. Afterwards:
      `claude mcp remove bimmer-bridge -s user && claude mcp add --scope user bimmer-bridge -- /opt/homebrew/bin/node "$PWD/mac-mcp/dist/index.js"`.
      Restore Desktop's entry to `{ "command": "/opt/homebrew/bin/node", "args": ["<repo>/mac-mcp/dist/index.js"] }`
      and Cmd+Q and reopen it.
- [ ] Know which scenarios to record at the car: `nve-faults` (evals 01 and 03) and `battery`
      (eval 06). `needs-review` (eval 04) was recorded at the desk on 2026-11-22. What each
      needs is in [dev/fixtures/README.md](../dev/fixtures/README.md#scenarios-the-evals-use-skillsevals);
      between them, the session must include `ediabas_list_ecus` and `ediabas_describe` for
      the night-vision SGBD and the engine group, `ediabas_vehicle_status`,
      `ediabas_read_faults` with `NVE_65`, and one `ediabas_jobs` batch.
- [ ] **Record the evals' own calls.** The replay agent answers only a request with the same
      path, query and body as a recording ([dev/fixtures/README.md](../dev/fixtures/README.md#layout)):
      a recorded `ediabas_read_faults` for ten ECUs does not answer eval 01's call for
      `NVE_65` alone, and a `filter` or `includeInfoMemory` that differs misses too. So at the
      car, with the `bimmer-bridge` skill installed (the current copy from this repo), type
      the eval prompts themselves, in order, and let Claude make its calls: first
      "Read the faults on night vision" (eval 01), then "Why is my battery draining?" (eval
      06). Do the whole-car read and any other reads as separate calls after them; they cost
      nothing to replay later and are not needed by the evals. `NVE_65` `FS_LESEN` declares
      no mileage result, so eval 01 reports a distance only if the car returns one.

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
    it can be run at the car without describing it first. Eval 06 does need a recorded
    `ediabas_describe` of the engine group, so that one happens with the ignition on: use a
    `filter` (e.g. `STATUS_`) and allow about a minute for it.
  - Already described at the desk on 2026-11-22, with their result names in the skill's
    `job-cookbook.md`: `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL`, `C_FA_LESEN` on
    `CAS` and `LM_60`, `UTILITY` `STATUS_UBATT` and `STATUS_ZUENDUNG`, `NVE_65` `FS_LESEN`.
    Check `vm_health`'s policy version is still 4; if so, their verdicts and result names
    are known. They still have to be described again in this session wherever a scenario
    records a describe (`nve-faults` needs `ediabas_describe` of `NVE_65`): running the eval
    prompts above does that by itself.
- [ ] Agree the plan: which ECUs and jobs, in how many calls, and a rough time. The first
      EDIABAS call after a pause takes 10–15 s longer while the agent loads EDIABAS again
      (desk, 2026-11-21); with the idle time at 90 s that should happen once, not after every
      pause.

## Ignition on

- [ ] Switch the ignition on and note the time.
- [ ] `ediabas_vehicle_status` first. Observed on 2026-11-23 with the ignition on: `UTILITY`
      `STAT_ZUENDUNG` = 1 and the cable's KL15 = `E02E0000`; `STAT_UBATT` = 1 and KL30 =
      `E02E0000` (the same as with no car); `NVE_65` `STATUS_BORDNETZSPANNUNG` = 12 (whole
      volts). Only ignition on was observed at the car: no car gave 0 and `00000000`, and
      ignition off with the cable in the car is not recorded. Voltages from a module
      (e.g. `NVE_65` `STATUS_BORDNETZSPANNUNG` on an E60/E61 with night vision) need
      `voltageSources` in `config.local.json` on the Mac, set before Claude starts.
      `UTILITY` `STAT_UBATT` and the cable's KL30 do **not** show the car is awake: with the
      cable in the VM and no car (rehearsal, 2026-11-22) they read `STAT_UBATT=1` and KL30
      `E02E0000` (12000 read little-endian), values from the cable's own lines. The "voltage in
      volts" for the acceptance comes from the voltage source (`NVE_65`), not from these.
- [ ] On an E60/E61, the whole-car read: `ediabas_job` on `E60` with `IDENT_FUNKTIONAL` (which modules
      answer) and `FS_LESEN_FUNKTIONAL` (their faults in one job), and `C_FA_LESEN` on `CAS` or
      `LM_60` (the vehicle order).
  - Run each functional job as its **own** `ediabas_job` with `timeoutSeconds: 60`, after
    `ediabas_vehicle_status` has loaded EDIABAS. A request gets at most 60 s, counted from
    when the agent receives it, so loading EDIABAS (about 10 s) would come out of it. On
    2026-11-23 each functional job took about 4 s (`IDENT_FUNKTIONAL` 3,925 ms,
    `FS_LESEN_FUNKTIONAL` 3,964 ms). A
    `job_timeout` means it needed longer; do not retry it, read the modules one by one.
  - If a job does not stop within 10 s of being timed out, the agent leaves EDIABAS alone
    until Windows in the VM restarts (`ediabas_unavailable`; INPA may not get the cable
    either). Restart the VM before any further read.
- [ ] The rest, batched: one `ediabas_read_faults` for the ECUs wanted and the status jobs in
      one `ediabas_jobs` call. For Phase 6: `IDENT` through `D_MOTOR`, and the fault memory
      of `NVE_65`. Name only modules that `IDENT_FUNKTIONAL` showed answering: a module that
      does not answer can use its whole job timeout, every item in a call shares the one 60 s
      limit, and later items then come back `not_run`. Read those in a second call.
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
