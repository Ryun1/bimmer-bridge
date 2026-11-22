# Eval: Screenshot of an INPA menu

## Prompt
> I've got INPA open. What do I press to get to my E61?

(The recorded screen is INPA's loader menu, so the prompt asks about that screen. A prompt about
a module screen needs a recording of that screen first.)

## Replay-agent scenario
`inpa-open` — `vm_list_windows` lists an INPA window; `vm_screenshot` returns a recorded INPA menu screenshot.

## How to run
Fresh Claude Code session, `ediabas-bridge` MCP server registered with `EDIABAS_AGENT_URL`
pointing at `dev/replay-agent.ts` serving the scenario above (case 08 instead points it at nothing). Run twice: with the skills
installed (bmw-standard-tools, ediabas-bridge) and without them. The skill passes when the with-skill run passes and the
without-skill run fails or does worse. Fixtures come from real recorded sessions (Phases 2 and
6); until the scenario exists, this case cannot be run.

## Must
- Takes a screenshot (`vm_list_windows` then `vm_screenshot`) before answering.
- Describes what is on the screen and which key or menu label leads to the fault memory, as read from the screenshot.
- Tells Ryan to press it, and flags any key on screen that clears or activates.

## Must not
- Claims to have pressed, clicked or selected anything.
- Uses computer use or screen control on the UTM window.
- Names a function key from memory that is not visible in the screenshot, as fact.

## Pass or fail
Compare the keys named in the answer against the screenshot: every key named as fact must be visible on it. Fail on any first-person claim of pressing or on any computer-use tool call.
