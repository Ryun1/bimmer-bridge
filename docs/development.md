# Development

How to build and test the bridge on the Mac with no VM and no car. Releasing is in the
README's [Releasing](../README.md#releasing) section; deployment to the VM in
[Updating](../README.md#updating).

## Prerequisites

- **Node 24 or later** (`brew install node`). The MCP server itself needs Node 20+, but the dev
  tools (`dev/replay-agent.ts`, `dev/scrub-fixtures.ts`) are run directly by Node through type
  stripping, which needs 24+.
- **.NET 10 SDK** (`brew install dotnet@10`). It is keg-only; `release.sh` and
  `vm-agent/build.sh` find it in the Homebrew folder by themselves. To run `dotnet` by hand, see
  the README's [prerequisites](../README.md#1-prerequisites-on-the-mac).
- **Python 3** (the macOS one is enough), for `dev/serve.py` and its tests.
- The MCP server's dependencies, once: `(cd mac-mcp && npm ci)`. `dev/` has none of its own; its
  tests run from `mac-mcp` and use its packages.

## Test suites

| Suite | Command (from the repo root) | Covers |
|---|---|---|
| agent | `(cd vm-agent && dotnet test EdiabasAgent.Tests)` | xUnit tests of `EdiabasAgent.Core` against fakes: config, policy (every shared case), description screen, path rules, conflict matching, EDIABAS service against a fake API, result shaping, audit lines |
| MCP server | `(cd mac-mcp && npm run typecheck && npm test)` | vitest: every tool against a stub agent, policy (every shared case), VM lookup, token, formatting (km to miles), audit |
| deploy server | `python3 -m unittest dev/test_serve.py` | routes, address rules, upload caps, token hand-off, the pasted lines |
| dev tools | `(cd mac-mcp && npm run typecheck:dev && npm run test:dev)` | replay agent, scrubber, and a session of the real MCP server answering tool calls from the replay agent, all on synthetic recordings in temporary folders |
| everything | `./release.sh --no-build` | the four above plus the version check |

The agent tests cover only `EdiabasAgent.Core`, which has no Windows dependencies. The Windows
host (`vm-agent/EdiabasAgent/`: Kestrel, P/Invoke into `api32.dll`, window capture, file
handles) is built by `vm-agent/build.sh` but can only be exercised in the VM, by
`EdiabasAgent.exe --selftest` and the desk checks recorded in
[phase-results.md](phase-results.md).

## Policy fixtures: both sides must agree

`policy/policy.json` is read by two parsers and two evaluators, one in C# and one in
TypeScript. Two shared files keep them identical:

- `policy/policy-cases.json`: ECU, job, and the expected `allowed`, `rule` and `screen`. Both
  `vm-agent/EdiabasAgent.Tests/JobPolicyTests.cs` and `mac-mcp/test/policy.test.ts` run every
  case.
- `policy/policy-parse-cases.json`: whole policy texts each parser must accept or refuse
  (unknown or duplicate keys, bad `descriptionScreen.ignore`, ...).

Change `policy.json` and the cases in the same commit; a policy change without a case change
is a bug. If the file's meaning changes, raise its `version`, and update
[api.md "Job policy"](api.md#job-policy) first: both sides are built against it. See
[policy/README.md](../policy/README.md).

## The replay agent and fixtures

`dev/replay-agent.ts` stands in for the VM agent and answers only from recorded exchanges in
`dev/fixtures/<scenario>/`; it never makes up an answer. Two scenarios exist, both recorded
at the desk on 2026-11-21: `idle` (the agent up, no BMW tool open) and `inpa-open` (an overlay
on `idle`: INPA open, every EDIABAS call refused with `cable_in_use`, and a screenshot of the
INPA menu).

```sh
EDIABAS_AGENT_TOKEN=replay-dev-token node dev/replay-agent.ts --scenario idle   # 127.0.0.1:8765
```

Then point an MCP server at it with `EDIABAS_AGENT_URL=http://127.0.0.1:8765` and the same
`EDIABAS_AGENT_TOKEN` (the Keychain is never read when `EDIABAS_AGENT_URL` is set). The
registration for an eval session, the scenario format and the matching rules are in
[dev/fixtures/README.md](../dev/fixtures/README.md).

## Recording and scrubbing

Fixtures come only from real sessions; nothing is written by hand.

1. Run the MCP server with `EDIABAS_RECORD_DIR` set to a folder **outside the repo** (it holds
   real vehicle data). Every agent exchange is written there as one JSON file, screenshots
   beside it; the token never is.
2. Scrub into the fixtures folder, first with `--dry-run`:
   `node dev/scrub-fixtures.ts <raw-folder> <scenario> --dry-run`, then without it.
3. Review every file and every screenshot before committing (screenshots are copied
   unchanged).

The full procedure, and which scenarios the evals still need, are in
[dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario).

## Skills and evals

The skills are instruction files with their own rules (see [skills/README.md](../skills/README.md)).
The evals in `skills/evals/` run in a fresh Claude Code session against the replay agent, once
with the skills and once without.

## Working rules

From [PLAN.md section 12](../PLAN.md#12-working-agreements-for-claude-code), still in force:

- Nothing that talks to the car runs unattended. Claude Code may run offline jobs (`TMODE`,
  the `_JOBS` family) only after Ryan confirms the cable is unplugged.
- Contracts first: change [api.md](api.md) or [tools.md](tools.md), then the code on both
  sides.
- Skill reference files hold only what a real session returned, committed with the fixtures
  they came from.
