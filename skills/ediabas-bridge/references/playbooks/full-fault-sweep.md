# Playbook: full fault sweep

Reading the fault memory of every module Ryan cares about, in as little ignition-on time as
possible. The session routine in `../../SKILL.md` applies; this file adds only what is specific.

**Status: procedure only.** No sweep has been run on this car yet, and no SGBD has answered
from it. Once one has, the modules that answered go into `../ecu-map.md`.

## Before the ignition goes on

- Agree the scope with Ryan: every module, or the systems behind a symptom. A narrower sweep is
  quicker and drains less.
- If `../ecu-map.md` has modules confirmed from the car, use those names. Otherwise plan to start
  with the whole-car read (below) and build the list from what it returns. Where a name has to
  be found by hand: the VM holds well over a thousand SGBD files, most for other cars, so search
  with a `filter`. A group name (`grp`) resolves to a variant when read and the tool reports
  which, so it helps where the exact variant is not known. If you cannot tell which name fits
  this car, ask Ryan rather than including guesses: every wrong name costs time.
- Tell Ryan the number of calls: one `ediabas_read_faults` per 20 ECUs, and whether info memory
  is included (it doubles the reads).

## With the ignition on (not yet run at the car)

1. `E60` `FS_LESEN_FUNKTIONAL` reads the faults of every module that answers in one job, and
   `IDENT_FUNKTIONAL` lists those modules. If that answers the question, stop there.
2. Otherwise one `ediabas_read_faults` call with the modules wanted (from the list above), for
   the detail per module. Add `includeInfoMemory` only if Ryan wants it.
3. Do not go back for extra detail per fault unless Ryan asks.

## After

- Report per ECU: faults as returned, "no faults", or the error and why. List ECUs that did not
  answer separately; do not treat silence as "no faults".
- Tell Ryan which names answered, so they can be recorded in `../ecu-map.md`.
