# dev: tools for deploying and for testing without the car

| File | What |
|---|---|
| `serve.py` | the Mac-side deploy server: serves `vm-setup/*.ps1` and `release/EdiabasAgent.zip` to the VM's address only, takes the agent token into the Keychain with `--accept-token` (and then puts `$SendToken=$true` on the Install line), prints the hash-checked lines to paste. Contract: [docs/deploy.md](../docs/deploy.md) |
| `test_serve.py` | its tests: `python3 -m unittest dev/test_serve.py` |
| `replay-agent.ts` | a stand-in VM agent that answers only from recorded fixtures, on 127.0.0.1 |
| `scrub-fixtures.ts` | copies a raw recording (`EDIABAS_RECORD_DIR`) into `fixtures/<scenario>/` with identifiers removed |
| `fixtures/` | recorded scenarios (`idle`, `inpa-open`): [fixtures/README.md](fixtures/README.md) |
| `test/` | vitest tests for the replay agent and scrubber, run from `mac-mcp`; `fixtures-clean.test.ts` checks that every committed fixture needs no scrubbing |

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
`config.local.json` is a warning, not an error. The TypeScript tools have no dependencies and need Node 24+ (they run through type
stripping). More: [docs/development.md](../docs/development.md).

`./release.sh` (repo root) and `vm-agent/build.sh` check the git tree: they stop on
uncommitted changes unless given `--allow-dirty`; the zip's `commit.txt` then ends in
`-dirty`, and `release.sh` ends with "Checks passed, but this is NOT a release: … uncommitted changes."
Its summary prints the zip's version and commit. `./release.sh --dry-run` checks the versions and the git tree and
runs nothing else. When `pwsh` is installed, `release.sh` also parses the `vm-setup/*.ps1`
scripts (syntax errors only, not every Windows PowerShell 5.1 difference); without it the
check is skipped.

## Describe comparison (`describe-diff.ts`)

M1 acceptance: `describe` from `ediabasd` matches the VM agent's for every `.prg`. For each
`.prg` (as `ediabasd ecus` lists them), it runs a list-mode describe with comments on both, and
compares them with `describe-diff-lib.ts`: durations are ignored; `ecu`, `variant`, `jobCount`,
job names in order, comments and verdicts are compared; when either side did not read a job's
description (`not_checked`), only the name and the verdict's `allowed` and `rule` are. One
JSON line per ECU goes to `--out` with its `kind` (`match`, `differ`, `vm_error`,
`native_error`, `both_error`); a second run skips ECUs already there.

**It runs EDIABAS in the VM** (and real EDIABAS runs each SGBD's INIT first), so it is a desk
session: the cable unplugged from the car, Ryan's go-ahead. Build `engine/ediabasd-net` and
`mac-mcp` (`npm ci && npm run build`) first: the tool loads the agent client from `mac-mcp/dist`.

```sh
node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out describe-diff.jsonl [--only NVE_65] [--limit 20]
```
