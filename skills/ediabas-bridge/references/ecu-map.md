# ECU map — Ryan's E61 550i

**Status: no SGBD has yet answered from the car.** The entries below were seen at the desk on
2026-11-21 with the cable passed through to the VM but not plugged into the car. They show that
a file exists in the VM and what `ediabas_describe` read from it; none of them shows that the
module answered on this car. That needs a real read in Phase 6.

This file holds only what a real session returned: an SGBD or group name that answered on this
car, and what it covers. Nothing from memory, forums or the web goes in, however likely it
looks, because a wrong SGBD name costs ignition-on time and can mislead a diagnosis. Until an
entry is confirmed by a read, find names with `ediabas_list_ecus` and check them with
`ediabas_describe` before relying on them.

Entries are added in the same commit as the recorded session (fixtures) they came from.

## Entry format

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| e.g. the module as Ryan names it | the name passed as `ecu` | the `variant` the tool reported | what its fault memory / jobs cover | date, tool and result that confirmed it |

## Entries

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| Night vision | `NVE_65` (`prg`) | `NVE_65` | Night vision: its job list includes fault memory, info memory, ident and `STATUS_NIVI`, `STATUS_PAIRING` (see `job-cookbook.md`) | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`; `ediabas_describe` returned 59 jobs (cable in the VM, not in the car). **Not yet read from the car.** |
| Night vision (group) | `D_NVE` (`grp`) | **not checked** | not checked | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`, nothing more. Which variant it resolves to on this car has NOT been checked; do not assume it is `NVE_65`. |
| Diagnostic interface (the cable), not a car module | `TMODE` | `TMODE` | The interface itself: its runnable jobs read the interface type and version and the KL30 / KL15 voltages it sees (see `job-cookbook.md`) | 2026-11-21: `ediabas_describe` returned 25 jobs. Loads with no cable connected at all (seen at the desk the same day). |
| Utility SGBD, not a car module | `UTILITY` | not recorded | not recorded: its job list has not been captured | 2026-11-21: loaded with no cable connected at all (seen at the desk). |

`TMODE` and `UTILITY` are the only two names seen to load without the cable. `ediabas_describe`
on a real SGBD failed with `cable_not_found` until the cable was passed through to the VM.

## Job lists read at the desk, 2026-11-21

`ediabas_describe` (full job list, with comments) returned these on 2026-11-21 with the cable
passed through to the VM and **not plugged into the car**. Each row shows only that the file
is in the VM and what EDIABAS read from it: **none of them has been read from the car**, and
none of them is known to be fitted to this car or to be the right file for it. The Module
column is left blank because the recordings give a file name, not which module it is, and a
name from memory does not go in here.

"Allowed" counts jobs the policy allows by name; "runnable" counts those that are also not
"needs review" or "not checked" (the only ones the bridge will run). Battery-related jobs from
some of these files are in `job-cookbook.md`.

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| — | `E60` | `E60` | not recorded beyond its job list; its only allowed job is `INFO` | 2026-11-21: `ediabas_describe` returned 26 jobs, 1 allowed and runnable (`durationMs` 2872). Seen at the desk; **not yet read from the car.** |
| — | `CAS` | `CAS` | not recorded beyond its job list; battery-related jobs in `job-cookbook.md` | 2026-11-21: 150 jobs, 62 allowed: 16 runnable, 28 needs review, 18 not checked. Took the full 60 s limit (`durationMs` 60043); the last 52 jobs came back with no description. Seen at the desk; **not yet read from the car.** |
| — | `MPM_60` | `MPM_60` | not recorded beyond its job list; battery-related jobs in `job-cookbook.md` | 2026-11-21: 57 jobs, 21 allowed: 18 runnable, 3 needs review (`durationMs` 13284). Seen at the desk; **not yet read from the car.** |
| — | `KOMB60` | `KOMB60` | not recorded beyond its job list; battery-related jobs in `job-cookbook.md` | 2026-11-21: 128 jobs, 37 allowed: 36 runnable, 1 needs review (`durationMs` 52730). Seen at the desk; **not yet read from the car.** |
| — | `KBM_60` | `KBM_60` | not recorded beyond its job list | 2026-11-21: 79 jobs, 24 allowed: 22 runnable, 2 needs review (`durationMs` 24111). Seen at the desk; **not yet read from the car.** |
| — | `LM_60` | `LM_60` | not recorded beyond its job list | 2026-11-21: 134 jobs, 37 allowed and runnable. Took the full 60 s limit (`durationMs` 60051); the last 9 jobs came back with no description (all refused by name, so no verdict was affected). Seen at the desk; **not yet read from the car.** |
| — | `sgm_60_2` (passed in lower case) | `SGM_60_2` | not recorded beyond its job list; battery-related job in `job-cookbook.md` | 2026-11-21: 80 jobs, 24 allowed and runnable (`durationMs` 29423). Seen at the desk; **not yet read from the car.** |
| — | `IHKA60` | `IHKA60` | not recorded beyond its job list; battery-related jobs in `job-cookbook.md` | 2026-11-21: 119 jobs, 33 allowed: 30 runnable, 3 needs review (`durationMs` 50419). Seen at the desk; **not yet read from the car.** |
| — | `CCC_60` | `CCC_60` | not recorded beyond its job list | 2026-11-21: 87 jobs, 30 allowed: 29 runnable, 1 needs review (`durationMs` 28285). Seen at the desk; **not yet read from the car.** |
| — | `MASK60` | `MASK60` | not recorded beyond its job list | 2026-11-21: 91 jobs, 25 allowed: 24 runnable, 1 needs review (`durationMs` 30179). Seen at the desk; **not yet read from the car.** |
| — | `DSC_60` | `DSC_60` | not recorded beyond its job list | 2026-11-21: 95 jobs, 21 allowed and runnable (`durationMs` 37102). Seen at the desk; **not yet read from the car.** |
| — | `EKPM60_2` | `EKPM60_2` | not recorded beyond its job list | 2026-11-21: 62 jobs, 22 allowed: 21 runnable, 1 needs review (`durationMs` 14730). Seen at the desk; **not yet read from the car.** |

## Could not be described at the desk

`ediabas_describe` on these failed on 2026-11-21, with the cable in the VM and not in the car,
with `ediabas_error` 100 `SYS-0010: INITIALIZATION ERROR`, while the files above loaded. These
files appear to need the control unit to answer even to list their jobs, so they can only be
described at the car:

- `N62_TUE2`
- `ME9N62_2`
- `GS19D`
- `SZL_60`

Which engine file (if either of `N62_TUE2` and `ME9N62_2`) fits this car is **not known**.

