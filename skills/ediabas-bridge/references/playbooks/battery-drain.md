# Playbook: battery-drain evidence

Ryan's E61 has a recurring battery drain. The aim is to gather evidence from the car in one
short session, not to diagnose from the readings alone: interpretation belongs to the
`bmw-e61` skill and to Ryan.

**Status: no battery-related job has been run on this car yet.** The jobs below were found
at the desk on 2026-11-21/22 with `ediabas_describe` (the cable in the VM, **not plugged into
the car**). They show that the files are in the VM and what the SGBDs declare; none of these
files is known to be fitted to this car, and no result has been read. Their descriptions,
result names and verdicts are in `../job-cookbook.md` ("Battery-drain related jobs").

| File | Runnable (review passed) | Needs review: the bridge refuses them; Ryan decides |
|---|---|---|
| `CAS` | `STATUS_PA_WUP` | `STATUS_WAKEUP_HISTORY`, `STATUS_KLEMMENVERHINDERER`, `STATUS_KL15_ABSCHALTUNG`, `STATUS_WUP` |
| `KOMB60` | `STATUS_KLEMMENSPANNUNG`, `STATUS_KLEMMEN`, `STATUS_KL30_H_ZAEHLER` | — |
| `IHKA60` | `STATUS_ENERGIESPARMODE`, `STATUS_VERBRAUCHERREDUZIERUNG` | — |
| `sgm_60_2` (variant `SGM_60_2`) | `STATUS_STROMVERSORGUNG_LESEN` | — |
| `MPM_60` | `STATUS_ECU_LESEN` | `STATUS_AD_WERTE_LESEN` |

The engine files could not be described at the desk: `ediabas_describe` on `N62_TUE2` and
`ME9N62_2` failed with `ediabas_error` 100 `SYS-0010: INITIALIZATION ERROR` (see
`../errors.md`), and which engine file fits this car is not known. The project plan's
engine-ECU candidates (`D_MOTOR`, `STATUS_IBS_MESSWERTE`, `STATUS_BATTERIEINTEGRATOR`,
`STATUS_SYSTEMCHECK_PM_INFO_1`, `STATUS_SYSTEMCHECK_PM_INFO_2`) remain **unverified**: none
has been seen in a session. Describing the engine file has to be done at the car; plan for it.

What each job actually returns is whatever the car sends; do not describe it from the name.

The irony to keep in mind: reading the car to investigate a drain drains it. Plan everything
first.

## Before the ignition goes on

1. `vm_health`. If a BMW tool is running, name it, ask Ryan to close it when he has finished,
   and stop until he says so.
2. Ask Ryan what he has seen (flat battery after how many days, warnings, recent work) and
   whether the battery is on a charger. This decides which reads matter. Ask him to have the
   cable plugged in and passed through to the VM in UTM: the next step needs it.
3. Discover the names: `ediabas_list_ecus` (filter for the candidates above), then
   `ediabas_describe` on the engine group or SGBD with a `filter` (for example `STATUS_`) to
   see which battery or power-management jobs exist and which are runnable. Use
   `ediabas_describe` with `job` to see each chosen job's arguments and result fields. Drop
   any job that is refused or marked "needs review"; show Ryan the description of any
   "needs review" job and let him decide, but do not hold the session for it.
   `ediabas_describe` needs the cable passed through to the VM, and for most files neither
   the car nor the ignition: on 2026-11-21 it failed with `cable_not_found` without the
   cable, and worked with the cable in the VM and not plugged into the car. The exceptions
   seen are `N62_TUE2`, `ME9N62_2`, `GS19D` and `SZL_60` (above). So do all the other discovery
   now, before asking for the ignition, with a `filter` (a full describe of `CAS` or `LM_60`
   ran into the 60 s limit and left the last jobs unscreened). `../job-cookbook.md` lists the
   jobs above and those on `NVE_65` and `TMODE` (for example `TMODE`'s `LESE_SPANNUNG_KL30`);
   none has been run on the car yet, so check each one's arguments and results with
   `ediabas_describe` and `job`.
4. Pick a short list: fault memory of the modules involved plus a few status jobs. Tell Ryan
   the plan, the number of calls and a rough time (the first EDIABAS call after a pause can
   take 10–15 s while the agent loads EDIABAS again; that is normal); ask him to switch the
   ignition on and say when. Note the time.

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
