# Job cookbook — jobs confirmed on Ryan's E61

**Status: empty. No real EDIABAS session with the car has happened yet, so no job here is
confirmed.**

This file holds only jobs that a real session ran successfully on this car, with the arguments
used and the result fields that came back. Nothing from memory, forums or the web goes in:
SGBDs differ between variants, and a job name or result field remembered from another car can
be wrong here. Until an entry exists, find jobs with `ediabas_describe` (job list, then one
job's arguments and results) and use only those it shows as runnable.

Entries are added in the same commit as the recorded session (fixtures) they came from.

## Entry format

### `<ECU>` `<JOB>`
- **Purpose:** what it reads, in plain English.
- **Arguments:** exactly as passed (or none).
- **Result fields seen:** name, unit, example value, and what it meant.
- **Policy verdict:** the rule `ediabas_describe` showed (and review status).
- **Typical duration:** `durationMs` seen.
- **Confirmed:** date, session, fixture name.

## Entries

None yet.
