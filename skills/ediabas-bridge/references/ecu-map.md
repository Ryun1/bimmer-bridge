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
