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
| Night vision | `NVE_65` (`prg`) | `NVE_65` | Night vision: its job list includes fault memory, info memory, ident and `STATUS_NIVI`, `STATUS_PAIRING` (see `job-cookbook.md`) | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`; `ediabas_describe` returned 59 jobs (cable in the VM, not in the car; verdicts under policy version 1). **Not yet read from the car.** |
| Night vision (group) | `D_NVE` (`grp`) | **not checked** | not checked | 2026-11-21: listed by `ediabas_list_ecus` with filter `nve`, nothing more. Which variant it resolves to on this car has NOT been checked; do not assume it is `NVE_65`. |
| Diagnostic interface (the cable), not a car module | `TMODE` | `TMODE` | The interface itself: its runnable jobs read the interface type and version and the KL30 / KL15 voltages it sees (see `job-cookbook.md`) | 2026-11-21: `ediabas_describe` returned 25 jobs (policy version 1). Loads with no cable connected at all (seen at the desk the same day). |
| Utility SGBD, not a car module | `UTILITY` | not recorded | not recorded: its job list has not been captured | 2026-11-21: loaded with no cable connected at all (seen at the desk). |

`TMODE` and `UTILITY` are the only two names seen to load without the cable.

## Other job lists read at the desk, 2026-11-21

`ediabas_describe` also returned the full job lists of `E60`, `CAS`, `MPM_60`, `KOMB60`,
`KBM_60`, `LM_60`, `sgm_60_2` (variant `SGM_60_2`), `IHKA60`, `CCC_60`, `MASK60`, `DSC_60` and
`EKPM60_2`, with the cable in the VM and **not in the car**. That shows only that the files are
in the VM, not that any of them is fitted to this car. Their job counts and verdicts (recorded
under policy version 1) are in `job-cookbook.md`. Under policy version 3, `E60` also allows the
whole-car jobs `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL`; none of this has been read from the
car.

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

