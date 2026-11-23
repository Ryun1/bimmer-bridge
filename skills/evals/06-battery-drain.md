# Eval: Why is my battery draining?

## Prompt
> Why is my battery draining?

## Replay-agent scenario
`battery` — healthy agent; `ediabas_list_ecus` / `ediabas_describe` answer for the engine group and a few status jobs; `ediabas_vehicle_status`, `ediabas_read_faults`, `ediabas_job` and `ediabas_jobs` answer from a real session.

## How to run
Fresh Claude Code session, `bimmer-bridge` MCP server registered with `EDIABAS_AGENT_URL`
pointing at `dev/replay-agent.ts` serving the scenario above (case 08 instead points it at nothing). Run twice: with the skills
installed (bimmer-bridge) and without them. The skill passes when the with-skill run passes and the
without-skill run fails or does worse. Its fixture has to be recorded at the car (Phase 6); until then
this case cannot be run.

## Must
- Asks about symptoms and/or states a plan of specific reads before the ignition goes on, with a rough time.
- Uses `ediabas_describe` to choose jobs; uses only names a tool returned.
- Batches fault memory into one `ediabas_read_faults` call and the status jobs into one `ediabas_jobs` call (or runs them back to back).
- Mentions the 10-minute ignition-on budget and ends with 'ignition off'.
- Hands readings to a car-specific skill (such as `bmw-e61`) when one is installed, and does not claim a cause the data does not show.

## Must not
- Reads every ECU, or runs every job an SGBD lists.
- Uses job or SGBD names from memory that no tool returned this session.
- States a definite cause of the drain from the readings alone.

## Pass or fail
Count car-facing calls (`ediabas_vehicle_status`, `ediabas_read_faults`, `ediabas_job`, and each item of `ediabas_jobs`): a pass has a plan before the first of them and roughly a handful in total; more than about 15, or more than one `ediabas_read_faults` without a reason, fails. Check every `ecu`/`job` argument appeared in an earlier tool result.
