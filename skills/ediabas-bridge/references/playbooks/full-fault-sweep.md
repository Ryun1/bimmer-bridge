# Playbook: full fault sweep

Reading the fault memory of every module Ryan cares about, in as little ignition-on time as
possible.

**Status: procedure only.** No sweep has been run on this car yet, so this playbook names no
SGBD as confirmed. Once one has, the list of ECUs that answered goes into `../ecu-map.md` and
the sweep reads from there.

## Before the ignition goes on

1. `vm_health`. If a BMW tool is listed as running, name it, ask Ryan to close it when he has
   finished with it, and stop until he says it is closed. Never hurry him out of WinKFP or NCS
   Expert.
2. Agree the scope with Ryan: every module, or the systems behind a symptom. A narrower sweep
   is quicker and drains less.
3. Build the ECU list:
   - If `../ecu-map.md` has entries, use those names.
   - Otherwise search with `ediabas_list_ecus` (use `filter`; the VM holds well over a
     thousand SGBD files, most for other cars). Group names (`grp`) resolve to a specific
     SGBD variant when read, and the tool reports which `variant` they resolved to, so they
     help where the exact variant is not known. Confirm each candidate has a runnable
     fault-memory job with `ediabas_describe`.
   - If you cannot tell which name fits this car, ask Ryan rather than including guesses.
     Every wrong name is an error result that still costs time.
4. Tell Ryan the plan: how many ECUs, one `ediabas_read_faults` call (two if more than 20, or
   more than 10 with info memory), whether info memory is included, and a rough time. Ask him
   to switch the ignition on and say when it is. Note the time.

## With the ignition on

5. `ediabas_vehicle_status`. Report what it shows as returned. If nothing answers, the
   ignition may not be on: ask, rather than starting the reads.
6. One `ediabas_read_faults` call with the whole list. Add `includeInfoMemory` only if Ryan
   wants it; it doubles the reads.
7. Do not go back for extra detail per fault unless Ryan asks; each extra call is more
   ignition time. Keep the total under 10 minutes.

## After

8. Say "ignition off".
9. Report per ECU: faults as returned (miles first), "no faults", or the error and why.
   List ECUs that did not answer separately; do not treat silence as "no faults".
10. Hand the codes, with date and mileage, to the `bmw-e61` skill for interpretation and the
    fault log.
11. Tell Ryan which names answered, so they can be recorded in `../ecu-map.md`.
