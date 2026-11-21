# Proposed addition to the `bmw-e61` skill

The `bmw-e61` skill is Ryan's and lives outside this repo. This is a short section for him to
paste into its `SKILL.md` (and, if he wants it to trigger from bridge readings, a phrase for
its description). Nothing here changes that skill's existing content.

## Section to add

```markdown
## Reading the car through the ediabas-bridge

When the `ediabas_*` tools are available (Ryan's read-only bridge to the BMW Standard Tools VM),
use the `ediabas-bridge` skill to read fault memory and live values from the car instead of
asking Ryan to read codes out of INPA. That skill covers the health check, keeping
ignition-on time short (the car has a battery-drain fault) and what each refusal means.

When fault codes or readings come back from the bridge:
- interpret them here, as for codes Ryan reads himself;
- record what was read in the fault log: date, mileage (miles first, km in brackets),
  module, code, text, present or stored, and "read via ediabas-bridge";
- say plainly when a module could not be read, rather than treating it as fault-free.

The bridge cannot clear faults, code or flash. If a fix needs that, it is Ryan's job by hand in
INPA, NCS Expert or WinKFP; the `bmw-standard-tools` skill helps with those.
```

## Optional description phrase

Append to the skill's `description`: "Also use it to interpret and log fault codes and
readings that the ediabas-bridge tools read from the car."
