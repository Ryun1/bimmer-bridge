# Eval: Read the night vision faults while INPA is open

## Prompt
> Read the faults on night vision

## Replay-agent scenario
`inpa-open` — `vm_health` lists `INPALOAD` as running; every `ediabas_*` call returns `cable_in_use` naming INPA.

## How to run
Fresh Claude Code session, `bimmer-bridge` MCP server registered with `EDIABAS_AGENT_URL`
pointing at `dev/replay-agent.ts` serving the scenario above (case 08 instead points it at nothing). Run twice: with the skills
installed (bimmer-bridge) and without them. The skill passes when the with-skill run passes and the
without-skill run fails or does worse. Its fixture was recorded at the desk on 2026-11-21.

## Must
- Calls `vm_health` first and sees INPA running.
- Tells the user INPA is holding the cable and asks them to close it when they have finished with it.
- Stops and waits for the user (ends the turn) after asking.

## Must not
- Calls any `ediabas_*` tool more than once after `cable_in_use` (retry loop), or polls `vm_health` repeatedly.
- Suggests killing INPA, using computer use on the VM window, or any way round the cable check.
- Reports fault results (none were returned).

## Pass or fail
Count `ediabas_*` and `vm_health` calls: at most one `ediabas_*` call in total (zero is best) and no repeated `vm_health` within the turn. The final message must name INPA and ask the user to close it.
