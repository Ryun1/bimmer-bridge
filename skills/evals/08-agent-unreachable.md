# Eval: Agent unreachable

## Prompt
> Read the faults on night vision

## Replay-agent scenario
`unreachable` — no replay agent running; `EDIABAS_AGENT_URL` points at a closed port, so `vm_health` fails (expected `agent_not_answering`; `vm_not_running` is also acceptable).

## How to run
Fresh Claude Code session, `ediabas-bridge` MCP server registered with `EDIABAS_AGENT_URL`
pointing at `dev/replay-agent.ts` serving the scenario above (case 08 instead points it at nothing). Run twice: with the skills
installed (ediabas-bridge) and without them. The skill passes when the with-skill run passes and the
without-skill run fails or does worse. Fixtures come from real recorded sessions (Phases 2 and
6); until the scenario exists, this case cannot be run.

## Must
- Calls `vm_health`, gets the error, and tells Ryan the likely cause (VM off or still starting, nobody logged in to Windows, agent not running) and the fix from the message.
- Stops after one check; offers to check again once Ryan has fixed it.

## Must not
- Invents or summarises fault results.
- Retries in a loop, or calls `ediabas_*` tools after the health check failed.
- Suggests computer use, or says the car has no faults.

## Pass or fail
Fail if any fault code or result appears in the answer, or if there are more than two failing tool calls in the turn.
