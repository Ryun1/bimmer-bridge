# Eval: Read the faults on night vision

## Prompt
> Read the faults on night vision

## Replay-agent scenario
`nve-faults` — healthy agent, no BMW tool running; `ediabas_list_ecus`, `ediabas_describe`, `ediabas_vehicle_status` and `ediabas_read_faults` answered from a real night-vision session (fault memory with at least one fault carrying a mileage).

## How to run
Fresh Claude Code session, `ediabas-bridge` MCP server registered with `EDIABAS_AGENT_URL`
pointing at `dev/replay-agent.ts` serving the scenario above (case 08 instead points it at nothing). Run twice: with the skills
installed (ediabas-bridge) and without them. The skill passes when the with-skill run passes and the
without-skill run fails or does worse. Its fixture has to be recorded at the car (Phase 6); until then
this case cannot be run.

## Must
- Calls `vm_health` before any `ediabas_*` tool.
- Finds the SGBD with `ediabas_list_ecus` (and/or `ediabas_describe`) before reading; uses a name the tool returned.
- Asks Ryan to switch the ignition on (or confirms it is on) before car reads, and calls `ediabas_vehicle_status`.
- Reads fault memory with one `ediabas_read_faults` call.
- Reports the fault table as returned, with distances in miles first and km in brackets.
- Ends by telling Ryan to switch the ignition off.
- Offers the codes to the `bmw-e61` skill for interpretation / the fault log.

## Must not
- Passes an SGBD name to `ediabas_read_faults` or `ediabas_job` that no tool returned in this session (a guessed name, even a correct guess).
- Reports km only, or km first.
- Adds fault meanings or causes that the tool output does not contain, presented as read from the car.

## Pass or fail
Read the transcript's tool calls in order. Fail if `ediabas_read_faults` comes before `vm_health`, or if its `ecus` argument contains a name absent from every earlier tool result. Fail if any distance appears km-first. The ignition-off line must be in the final message.
