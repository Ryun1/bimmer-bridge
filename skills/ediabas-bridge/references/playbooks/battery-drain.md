# Playbook: battery-drain evidence

Ryan's E61 has a recurring battery drain. The aim is to gather evidence from the car in one
short session, not to diagnose from the readings alone: interpretation belongs to the
`bmw-e61` skill and to Ryan.

**Status: procedure only.** No battery-related job has been run on this car yet. Every SGBD
and job name below is an **unverified candidate** taken from the project plan, to confirm
with `ediabas_list_ecus` and `ediabas_describe` before use. None is known to exist on this
car, to be allowed by the policy, or to return any particular field.

| Candidate (unverified) | Where the plan expects it |
|---|---|
| `D_MOTOR` (group) | engine ECU group; the plan guesses it resolves to `N62_TUE` or `ME9N62_2` |
| `STATUS_IBS_MESSWERTE` | a battery-related read on the engine ECU |
| `STATUS_BATTERIEINTEGRATOR` | a battery-related read on the engine ECU |
| `STATUS_SYSTEMCHECK_PM_INFO_1`, `STATUS_SYSTEMCHECK_PM_INFO_2` | battery-related reads on the engine ECU |

What each job actually returns is whatever `ediabas_describe` shows; do not describe it from
the name.

The irony to keep in mind: reading the car to investigate a drain drains it. Plan everything
first.

## Before the ignition goes on

1. `vm_health`. If a BMW tool is running, name it, ask Ryan to close it when he has finished,
   and stop until he says so.
2. Ask Ryan what he has seen (flat battery after how many days, warnings, recent work) and
   whether the battery is on a charger. This decides which reads matter.
3. Discover the names: `ediabas_list_ecus` (filter for the candidates above), then
   `ediabas_describe` on the engine group or SGBD with a `filter` (for example `STATUS_`) to
   see which battery or power-management jobs exist and which are runnable. Use
   `ediabas_describe` with `job` to see each chosen job's arguments and result fields. Drop
   any job that is refused or marked "needs review"; show Ryan the description of any
   "needs review" job and let him decide, but do not hold the session for it.
4. Pick a short list: fault memory of the modules involved plus a few status jobs. Tell Ryan
   the plan, the number of calls and a rough time; ask him to switch the ignition on and say
   when. Note the time.

## With the ignition on

5. `ediabas_vehicle_status`; report the readings as returned.
6. One `ediabas_read_faults` call for the modules chosen.
7. The status jobs with `ediabas_job`, back to back, no pauses.
8. Stop at 10 minutes of ignition-on time even if the list is not done; say what was left.

## After

9. Say "ignition off".
10. Report the readings and faults as returned (distances miles first), and what could not be
    read and why. Do not claim a cause the readings do not show.
11. Hand the results to the `bmw-e61` skill for interpretation and the fault log.
12. Tell Ryan which names and result fields were confirmed, so they can be recorded in
    `../ecu-map.md` and `../job-cookbook.md`.
