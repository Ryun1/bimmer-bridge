# skills: Claude skills and their evals

| Path | What |
|---|---|
| `ediabas-bridge/` | how to run a diagnostic session with the `vm_*` and `ediabas_*` tools: health check first, short ignition-on time, finding jobs with `ediabas_describe`, what each error means. References hold only names and results confirmed by real sessions |
| `bmw-standard-tools/` | helping Ryan drive INPA, Tool32, NCS Expert and WinKFP by hand, with their risks |
| `evals/` | eight eval scenarios, run against the replay agent with and without the skills |
| `install.sh` | links each skill into `~/.claude/skills/` and builds `skills/dist/<skill>.zip` for claude.ai |

```sh
skills/install.sh        # [--dest <dir>] [--no-link] [--no-zip]
```

The skills are guidance; the limits are enforced by the code ([docs/safety.md](../docs/safety.md)).
Their reference files are filled only from real sessions, in the same commit as the fixtures
they came from. Evals 02, 05, 07 and 08 pass; 01, 03, 04 and 06 wait for fixtures recorded at
the car ([dev/fixtures/README.md](../dev/fixtures/README.md)). Installing them for Claude Code and
the desktop app: [README step 5](../README.md#5-install-the-skills).
`docs/bmw-e61-skill-addition.md` is a section to paste into Ryan's separate `bmw-e61` skill.
