# ECU map — Ryan's E61 550i

**Status: empty. No real EDIABAS session with the car has happened yet, so nothing here is
confirmed.**

This file holds only what a real session returned: an SGBD or group name that answered on this
car, and what it covers. Nothing from memory, forums or the web goes in, however likely it
looks, because a wrong SGBD name costs ignition-on time and can mislead a diagnosis. Until an
entry exists, find names with `ediabas_list_ecus` and confirm them with `ediabas_describe` and
a real read.

Entries are added in the same commit as the recorded session (fixtures) they came from.

## Entry format

| Module | Name (`prg` / `grp`) | Resolved variant | Covers | Confirmed (date, how) |
|---|---|---|---|---|
| e.g. the module as Ryan names it | the name passed as `ecu` | the `variant` the tool reported | what its fault memory / jobs cover | date, tool and result that confirmed it |

## Entries

None yet.
