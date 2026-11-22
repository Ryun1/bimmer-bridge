# Development

How to build, test and release the bridge on the Mac with no VM and no car. Deploying a build
to the VM: [install.md](install.md#updating).

## Prerequisites

- **Node 24 or later** (`brew install node`). The MCP server itself needs Node 20+, but the dev
  tools (`dev/replay-agent.ts`, `dev/scrub-fixtures.ts`, `dev/record-server.ts`) are run directly by Node through type
  stripping, which needs 24+.
- **.NET 10 SDK** (`brew install dotnet@10`). It is keg-only; `release.sh` and
  `vm-agent/build.sh` find it in the Homebrew folder by themselves. To run `dotnet` by hand, see
  [install.md](install.md#1-prerequisites-on-the-mac).
- **Python 3** (the macOS one is enough), for `dev/serve.py` and its tests.
- The MCP server's dependencies, once: `(cd mac-mcp && npm ci)`. `dev/` has none of its own; its
  tests run from `mac-mcp` and use its packages.

## Test suites

| Suite | Command (from the repo root) | Covers |
|---|---|---|
| agent | `(cd vm-agent && dotnet test EdiabasAgent.Tests)` | xUnit tests of `EdiabasAgent.Core` against fakes: config, policy (every shared case), description screen, path rules, conflict matching, EDIABAS service against a fake API, result shaping, audit lines |
| MCP server | `(cd mac-mcp && npm run typecheck && npm test)` | vitest: every tool against a stub agent, policy (every shared case), VM lookup, token, formatting (km to miles), audit |
| deploy server | `python3 -m unittest dev/test_serve.py` | routes, address rules, upload caps, token hand-off, the pasted lines |
| dev tools | `(cd mac-mcp && npm run typecheck:dev && npm run test:dev)` | replay agent, scrubber, and a session of the real MCP server answering tool calls from the replay agent, all on synthetic recordings in temporary folders; and a check that every committed fixture needs no scrubbing |
| everything | `./release.sh --no-build` | the four above plus the version check, the git tree check (it stops on uncommitted changes, or on untracked files under a release input; add `--allow-dirty`) and, when `pwsh` is installed, a parse check of the `vm-setup/*.ps1` scripts |

The agent tests cover only `EdiabasAgent.Core`, which has no Windows dependencies. The Windows
host (`vm-agent/EdiabasAgent/`: Kestrel, P/Invoke into `api32.dll`, window capture, file
handles) is built by `vm-agent/build.sh` but can only be exercised in the VM, by
`EdiabasAgent.exe --selftest` and the desk checks recorded in
[history/phase-results.md](history/phase-results.md).

## The `dev/` folder

| File | What |
|---|---|
| `serve.py` | the Mac-side deploy server: serves `vm-setup/*.ps1` and `release/EdiabasAgent.zip` to the VM's address only, takes the agent token into the Keychain with `--accept-token` (and then puts `$SendToken=$true` on the Install line), prints the hash-checked lines to paste. Contract: [deploy.md](deploy.md) |
| `test_serve.py` | its tests: `python3 -m unittest dev/test_serve.py` |
| `replay-agent.ts` | a stand-in VM agent that answers only from recorded fixtures, on 127.0.0.1 |
| `record-server.ts`, `recorder.ts`, `ts-resolve.mjs` | the MCP server with a recording `fetch`: writes every agent exchange to `EDIABAS_RECORD_DIR` ([Recording and scrubbing](#recording-and-scrubbing)) |
| `scrub-fixtures.ts` | copies a raw recording (`EDIABAS_RECORD_DIR`) into `fixtures/<scenario>/` with identifiers removed |
| `fixtures/` | recorded scenarios (`idle`, `inpa-open`): [dev/fixtures/README.md](../dev/fixtures/README.md) |
| `test/` | vitest tests for the replay agent and scrubber, run from `mac-mcp`; `fixtures-clean.test.ts` checks that every committed fixture needs no scrubbing; `doc-links.test.ts` checks that every relative link in the repo's markdown resolves |

```sh
python3 dev/serve.py [--accept-token] [--client <VM IPv4>] [--bind <Mac IPv4>] [--port 8000]
EDIABAS_AGENT_TOKEN=<any> node dev/replay-agent.ts --scenario idle [--port 8765] [--fixtures <dir>]
node dev/scrub-fixtures.ts <raw-folder> <scenario> [--fixtures <dir>] [--dry-run]
(cd mac-mcp && npm run typecheck:dev && npm run test:dev)
```

`serve.py` needs only Python 3's standard library. It refuses to start without the VM running
(no UTM bridge address) or without a DHCP lease for the VM's MAC (or pass `--client`). It finds
the MAC as the MCP server does: `EDIABAS_VM_MAC`, else `vmMac` in `config.local.json` at the repo
root (git-ignored; see `config.local.example.json`), else the first network card in UTM's
`~/Library/Containers/com.utmapp.UTM/Data/Documents/<name>.utm/config.plist`, where the name is
`EDIABAS_VM_NAME`, else `vmName` in `config.local.json`, else `Windows`. With no MAC from any of
these it stops and says to set `EDIABAS_VM_MAC` or `EDIABAS_VM_NAME`. A broken
`config.local.json` is a warning, not an error. The TypeScript tools have no dependencies and
need Node 24+ (they run through type stripping).

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

1. Register the recording server in place of the production one for the session, with
   `EDIABAS_RECORD_DIR` set to a folder **outside the repo** (it holds real vehicle data):
   command `node`, arguments `--experimental-transform-types --import
   /path/to/repo/dev/ts-resolve.mjs /path/to/repo/dev/record-server.ts`, and the same `env`
   as the production registration plus `EDIABAS_RECORD_DIR`. From a shell:
   `EDIABAS_RECORD_DIR=~/ediabas-recordings/x node --experimental-transform-types --import ./dev/ts-resolve.mjs dev/record-server.ts`. It is the production server
   (`mac-mcp/src`) with a recording `fetch` (`dev/recorder.ts`); the production server itself
   never writes exchanges. Every agent exchange except `GET /hello` is written as one JSON file
   named `<UTC time>-<sequence>-<METHOD>-<path>[-<ECU>-<JOB>].json`, screenshots beside it; the
   token and request headers never are. No build and no extra dependency: Node's type
   stripping needs `--experimental-transform-types` for `mac-mcp/src`'s parameter properties,
   and `dev/ts-resolve.mjs` maps its `./x.js` imports to `./x.ts` and the MCP SDK to
   `mac-mcp/node_modules`.
2. Scrub into the fixtures folder, first with `--dry-run`:
   `node dev/scrub-fixtures.ts <raw-folder> <scenario> --dry-run`, then without it.
3. Review every file and every screenshot before committing (screenshots are copied
   unchanged).

Besides fields named for a chassis number, VIN or serial number, the scrubber replaces any
17-character VIN-shaped text, and a BMW VIN (`WB…`) in any case whatever touches it. In hex
results (binary values) any 17-character run of the VIN alphabet with a letter and a digit is
scrubbed in place. Percent-encoded strings (such as the `X-Window-Title` header) are checked
decoded too. `dev/test/fixtures-clean.test.ts` runs the scrubber over every committed fixture
and fails if it would change anything: an identifier was committed, or the rules grew and the
fixture needs scrubbing again.

The full procedure, and which scenarios the evals still need, are in
[dev/fixtures/README.md](../dev/fixtures/README.md#recording-a-scenario).

## Skills and evals

The skills are instruction files with their own rules (see [skills/README.md](../skills/README.md)).
The evals in `skills/evals/` run in a fresh Claude Code session against the replay agent, once
with the skills and once without.

## Releasing

```sh
./release.sh                 # everything
./release.sh --no-build      # versions and tests only
./release.sh --skip-dotnet   # without the .NET tests and agent build (not a release)
./release.sh --dry-run       # check the versions and the git tree, and print the steps
./release.sh --allow-dirty   # build from a tree that is not clean (not a release)
```

`release.sh`:
1. checks that these versions are the same and are `x.y.z`: `VERSION`, `<Version>` in
   `vm-agent/Directory.Build.props`, and `version` in `mac-mcp/package.json` and
   `mac-mcp/package-lock.json`. To bump a version, change all of them (for the lock file,
   `npm --prefix mac-mcp version <x.y.z> --no-git-tag-version` updates both `mac-mcp` files);
2. refuses a git tree that is not clean unless `--allow-dirty` is given (the zip's
   `commit.txt` then ends in `-dirty`, and it ends with "Checks passed, but this is NOT a
   release: … the git tree is not clean."). Not clean means a tracked file with uncommitted
   changes anywhere, or an untracked file that `.gitignore` does not cover under a release
   input: `VERSION`, `vm-agent/`, `policy/`, `mac-mcp/`, `vm-setup/`, `dev/` or `skills/` (the
   .NET build compiles every `.cs` it finds, so an untracked one would ship in an exe that the
   commit cannot rebuild). The message names the untracked files. Untracked scratch elsewhere,
   such as notes under `docs/`, does not count. `vm-agent/build.sh` checks the tree the same
   way, with the same list;
3. if `pwsh` is installed, checks that the four `.ps1` files parse (syntax only; skipped
   otherwise, with a note);
4. runs the agent tests (`dotnet test vm-agent/EdiabasAgent.Tests`), the MCP server's
   typecheck and tests, the deploy server's tests (`python3 -m unittest dev/test_serve.py`) and
   the dev tools' typecheck and tests;
5. builds `release/EdiabasAgent.zip` and `release/EdiabasAgent.zip.sha256`
   (`vm-agent/build.sh`), one zip per skill in `skills/dist/` (`skills/install.sh --no-link`,
   which does not touch `~/.claude/skills`) and `mac-mcp/dist` (`npm --prefix mac-mcp run build`);
6. checks that the zip's `version.txt` is the release version, that it holds `commit.txt` and
   that the `.sha256` file matches the zip, then prints the commit, the SHA-256 and the Update
   line, and lists each `skills/dist/*.zip` with its SHA-256. The summary prints the zip's
   version and commit.

It stops at the first failure and never pushes, tags or deploys anything.

To publish: tag `vX.Y.Z` on the commit `release.sh` built and push the tag, then create a
GitHub release from it with the files `release.sh` printed attached (`release/EdiabasAgent.zip`,
`release/EdiabasAgent.zip.sha256` and each `skills/dist/*.zip`) and notes from the README's
[Status](../README.md#status) section. The `.sha256` checks that the download is intact, not who made it.

To run one part by hand:

```sh
(cd vm-agent && dotnet test EdiabasAgent.Tests)
vm-agent/build.sh
(cd mac-mcp && npm test && npm run build)
python3 -m unittest dev/test_serve.py
(cd mac-mcp && npm run test:dev)
```

`serve.py` also prints a **Discovery** line: it runs `vm-setup/discover.ps1`, which only reads,
and uploads its report into `discovery/` (git-ignored). It was used once, for Phase 0
([history/phase-results.md](history/phase-results.md)).

## Working rules

From [PLAN.md section 12](history/PLAN.md#12-working-agreements-for-claude-code), still in force:

- Nothing that talks to the car runs unattended. Claude Code may run only jobs that cannot
  reach a module (`TMODE`/`UTILITY` info jobs, `ediabas_describe` and the `_JOBS` family) and only
  after the owner confirms the cable is not plugged into the car.
- Contracts first: change [api.md](api.md) or [tools.md](tools.md), then the code on both
  sides.
- Skill reference files hold only what a real session returned, committed with the fixtures
  they came from.
