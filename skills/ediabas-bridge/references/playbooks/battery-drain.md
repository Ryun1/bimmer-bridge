# Playbook: battery-drain evidence

Ryan's E61 has a recurring battery drain. The aim is to gather evidence in one short session,
not to diagnose from the readings alone: interpretation belongs to the `bmw-e61` skill and to
Ryan. The session routine in `../../SKILL.md` applies; this file adds only what is specific.

**What the bridge can and cannot show** (general knowledge, not from this car): no bridge read
measures the parasitic current. Reading the car keeps its buses awake, so the car is not
asleep while it is being read. The bridge's evidence is history: wake-up reasons, fault and
info memory, and battery history where a module keeps it. The current itself is measured with
a meter or clamp with the car asleep, which is Ryan's to do.

**Status: no battery-related job has been run on this car yet.** The jobs below were found at
the desk on 2026-11-21/22 with `ediabas_describe` (cable in the VM, **not in the car**). They
show what the files in the VM declare; none of these files is known to be fitted to this car.
Descriptions and result names are in `../job-cookbook.md`. Verdicts were recorded under policy
version 1 or 2; under version 3 check each again with `ediabas_describe` before relying on it.

| File | Passed the description screen | Needed review; approved by Ryan by name on 2026-11-22 (`reviewedJobs` in the VM's config, not in the repo) |
|---|---|---|
| `CAS` | `STATUS_PA_WUP` | `STATUS_WAKEUP_HISTORY`, `STATUS_KLEMMENVERHINDERER`, `STATUS_KL15_ABSCHALTUNG`, `STATUS_WUP` |
| `KOMB60` | `STATUS_KLEMMENSPANNUNG`, `STATUS_KLEMMEN`, `STATUS_KL30_H_ZAEHLER` | — |
| `IHKA60` | `STATUS_ENERGIESPARMODE`, `STATUS_VERBRAUCHERREDUZIERUNG` | — |
| `sgm_60_2` (variant `SGM_60_2`) | `STATUS_STROMVERSORGUNG_LESEN` | — |
| `MPM_60` | `STATUS_ECU_LESEN` | `STATUS_AD_WERTE_LESEN` |

The approved jobs run while that config stands; after a fresh install of the agent they are
refused again until Ryan adds them back. What each job returns is whatever the car sends; do
not describe it from the name.

Which engine file fits this car is not known: `N62_TUE2` and `ME9N62_2` could not be described
at the desk (`../errors.md`). The project plan's engine-ECU candidates (`D_MOTOR`,
`STATUS_IBS_MESSWERTE`, `STATUS_BATTERIEINTEGRATOR`, `STATUS_SYSTEMCHECK_PM_INFO_1`,
`STATUS_SYSTEMCHECK_PM_INFO_2`) remain **unverified**; none has been seen in a session.

## Before the ignition goes on

- Ask Ryan what he has seen: flat after how many days, warnings, recent work, and whether the
  battery is on a charger. This decides which reads matter.
- Check the chosen jobs with `ediabas_describe` and `job`. Drop any refused or "needs review"
  job; show Ryan its description, but do not hold the session for it.
- Pick a short list: the whole-car read (`E60`), the fault memory of the modules involved, and
  a few of the status jobs above. Describing the engine file has to wait for the ignition;
  plan for it only if the session needs it.

## With the ignition on (not yet run at the car)

1. The whole-car read first (`E60` `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL`), so the reads
   that follow use modules that answered.
2. One `ediabas_read_faults` call for the modules chosen, with `includeInfoMemory` if Ryan
   wants the info memory.
3. The status jobs in one `ediabas_jobs` call.

## After

- Report readings and faults as returned, and what could not be read and why. Do not claim a
  cause the readings do not show.
- Tell Ryan which names and result fields were confirmed, for `../ecu-map.md` and
  `../job-cookbook.md`.
