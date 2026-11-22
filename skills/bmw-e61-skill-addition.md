# Optional: hand-over section for a car-specific skill

Optional. If you keep your own Claude skill about your car (one that interprets its fault codes
and keeps its fault log), this is a short section to paste into that skill's `SKILL.md` so that
it hands over to this bridge, and a phrase for its description so that it triggers from bridge
readings. It was written for the owner's private `bmw-e61` skill, which lives outside this
repo; change the skill name and the car-specific lines to suit yours. Nothing here changes that
skill's existing content, and the bridge works without it.

## Section to add

```markdown
## Reading the car through the ediabas-bridge

When the `ediabas_*` tools are available (the user's read-only bridge to the BMW Standard Tools
VM), use the `ediabas-bridge` skill to read fault memory and live values from the car instead
of asking the user to read codes out of INPA. That skill covers the health check, keeping
ignition-on time short (more so if the car has a battery-drain fault) and what each refusal
means.

When fault codes or readings come back from the bridge:
- interpret them here, as for codes the user reads themselves;
- record what was read in the fault log: date, mileage (miles first, km in brackets),
  module, code, text, present or stored, and "read via ediabas-bridge";
- say plainly when a module could not be read, rather than treating it as fault-free.

The bridge cannot clear faults, code or flash. If a fix needs that, it is the user's job by
hand in INPA, NCS Expert or WinKFP; the `bmw-standard-tools` skill helps with those.
```

## Optional description phrase

Append to the skill's `description`: "Also use it to interpret and log fault codes and
readings that the ediabas-bridge tools read from the car."
