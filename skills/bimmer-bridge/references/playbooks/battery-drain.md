# Playbook: battery-drain evidence

*Recorded on a 2005 E61 550i: other cars' ECUs and jobs differ, so check yours with
`ediabas_list_ecus` and `ediabas_describe`.*

The E61 this was written for has a recurring battery drain. The aim is to gather evidence in
one short session, not to diagnose from the readings alone: interpretation belongs to the user
(and to their car-specific skill, if they have one, such as a `bmw-e61` skill). The session routine in `../../SKILL.md` applies; this file adds only what is specific.

**What the bridge can and cannot show** (general knowledge, not from this car): no bridge read
measures the parasitic current. Reading the car keeps its buses awake, so the car is not
asleep while it is being read. The bridge's evidence is history: wake-up reasons, fault and
info memory, and battery history where a module keeps it. The current itself is measured with
a meter or clamp with the car asleep, which is the user's to do.

**Status: run on the E61 on 2026-11-23.** The jobs below were found at the desk on 2026-11-21/22
and tried at the car on 2026-11-23. Descriptions, results and what each returned are in
`../job-cookbook.md`; modules are in `../ecu-map.md`. On the E61 (2026-11-23):

| File | Job | Result at the car |
|---|---|---|
| `E60` | `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL` | answered, about 4 s each |
| `KGM_60` | `STATUS_HISTORIENSPEICHER_LESEN_BLOCK_1`, `_2`, `_3` | answered (see the cookbook) |
| `D_MOTOR` (resolves to `N62_TUE`) | `FS_LESEN` | answered: `2EF4`, and `2DED` (quiescent-current violation, stored) |
| `CAS` | `STATUS_WUP` | answered; stays pinned in the VM |
| `CAS` | `STATUS_WAKEUP_HISTORY`, `STATUS_KLEMMENVERHINDERER`, `STATUS_KL15_ABSCHALTUNG` | not supported by this car's CAS (2026-11-23); their approvals were removed from the VM's `reviewedJobs` |
| `MPM_60` | any | not on this car's bus (2026-11-23): `IFH-0009`; its `STATUS_AD_WERTE_LESEN` approval was removed |
| `CAS` | `STATUS_PA_WUP` | answered |
| `KOMB60` | `STATUS_KL30_H_ZAEHLER` | answered (hours counter) |
| `IHKA60` | `STATUS_VERBRAUCHERREDUZIERUNG` | answered (minute counters) |

An approval by name approves nothing: each must be the pinned `ECU:JOB@<hash>` line
`ediabas_describe` prints. What each job returns is whatever the car sends; do not describe it
from the name.

The engine file on this car is `N62_TUE` (reached through `D_MOTOR`; `../ecu-map.md`).

## Before the ignition goes on

- Ask the user what they have seen: flat after how many days, warnings, recent work, and whether the
  battery is on a charger. This decides which reads matter.
- Check the chosen jobs with `ediabas_describe` and `job`. Drop any refused or "needs review"
  job; show the user its description, but do not hold the session for it.
- Pick a short list: the whole-car read (`E60`, on an E60/E61), the `KGM_60` history blocks and
  the fault memory of the modules involved.

## With the ignition on

On the E61, in this order (2026-11-23):

1. `E60` `IDENT_FUNKTIONAL`: the modules that answer.
2. `E60` `FS_LESEN_FUNKTIONAL`: the faults of all of them.
3. `KGM_60` history blocks 1 to 3 in one `ediabas_jobs` call. Block 1 gives a `STAT_VERURSACHER`
   address per record, block 3 `STAT_WECKER_SG_ID` addresses (2026-11-23, recording 0016; 0x27
   is PGS by `IDENT_FUNKTIONAL`). What they mean for the drain is the user's call. Pass
   `results` to trim them.
4. `ediabas_read_faults` on the modules the history and the functional read name.
5. Fault detail (`FS_LESEN_DETAIL`) for a quiescent-current code such as `2DED`, once
   `ediabas_describe` has shown it (`N62_TUE` only describes with the ignition on).
6. If the history names PGS (0x27): `PGS_65_2` `FS_LESEN_DETAIL` for its fault, `IS_LESEN`,
   `STATUS_PGS_WUP` and `STATUS_TAGE_WUP` for each door handle. Described at the desk
   (2026-11-23), not yet run: see "Desk, 2026-11-23" in `../job-cookbook.md`.

## After

- Report readings and faults as returned, and what could not be read and why. Do not claim a
  cause the readings do not show.
- Tell the user which names and result fields were confirmed, for `../ecu-map.md` and
  `../job-cookbook.md`.
