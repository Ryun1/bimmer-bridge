# skills: Claude skills and their evals

| Path | What |
|---|---|
| `bimmer-bridge/` | how to run a diagnostic session with the `vm_*` and `ediabas_*` tools: health check first, short ignition-on time, finding jobs with `ediabas_describe`, what each error means. References hold only names and results confirmed by real sessions, recorded on a 2005 E61 550i: an example for other cars, whose ECUs and jobs differ |
| `bmw-standard-tools/` | helping the user drive INPA, Tool32, NCS Expert and WinKFP by hand, with their risks; watching mode from screenshots |
| `evals/` | eight eval scenarios, run against the replay agent with and without the skills. They are written around the E61 the bridge was built on (night vision, its battery drain, its INPA loader screen) |
| `install.sh` | links each skill into `~/.claude/skills/` and builds `skills/dist/<skill>.skill` (a zip) for claude.ai |

```sh
skills/install.sh        # [--dest <dir>] [--no-link] [--no-zip]
```

The skills are guidance; the limits are enforced by the code ([docs/safety.md](../docs/safety.md)).
Their reference files are filled only from real sessions, in the same commit as the fixtures
they came from. Those sessions were with the VM and cable of a 2005 E61 550i; on another car the skills tell Claude to
find ECUs and jobs with `ediabas_list_ecus` and `ediabas_describe` instead, and to treat the
E61 data as an example. Keep notes about your own car in your own copy or a git-ignored file,
not in these references. Evals 02, 05, 07 and 08 passed with the skills against the replay agent
(2026-11-21). [PLAN.md](../docs/history/PLAN.md) also asks that each fails or does worse without the skills; for 07 and 08
that was not shown, so their acceptance is not met. 01, 03 and 06 wait for fixtures recorded at
the car; 04's fixture can be recorded at the desk
([dev/fixtures/README.md](../dev/fixtures/README.md)). Installing them for Claude Code and
the desktop app: [install.md step 5](../docs/install.md#5-install-the-skills).
[bmw-e61-skill-addition.md](bmw-e61-skill-addition.md) is optional: an example
section to paste into a car-specific skill of your own (it was written for a `bmw-e61` skill),
so that it hands over to this bridge.
