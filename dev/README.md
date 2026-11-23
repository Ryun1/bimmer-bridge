# dev: tools for deploying and for testing without the car

`serve.py` (the Mac-side deploy server), the replay agent, the recording server, the fixture
scrubber and their tests. What each file is and how to run it:
[docs/development.md](../docs/development.md#the-dev-folder). The deploy server's contract:
[docs/deploy.md](../docs/deploy.md). Recorded fixtures: [fixtures/README.md](fixtures/README.md).

## Describe comparison (`describe-diff.ts`)

M1 acceptance: `describe` from `ediabasd` matches the VM agent's for every `.prg`. For each
`.prg` (as `ediabasd ecus` lists them), it runs a list-mode describe with comments on both, and
compares them with `describe-diff-lib.ts`: durations are ignored; `ecu`, `variant`, `jobCount`,
job names in order, comments and verdicts are compared; when either side did not read a job's
description (`not_checked`), only the name and the verdict's `allowed` and `rule` are. One
JSON line per ECU goes to `--out` with its `kind` (`match`, `differ`, `verdict_only`, `vm_error`,
`native_error`, `both_error`); a second run skips ECUs already there.

**It runs EDIABAS in the VM** (and real EDIABAS runs each SGBD's INIT first), so it is a desk
session: the cable unplugged from the car, Ryan's go-ahead. Build `engine/ediabasd-net` and
`mac-mcp` (`npm ci && npm run build`) first: the tool loads the agent client from `mac-mcp/dist`.

```sh
node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out describe-diff.jsonl [--only NVE_65] [--limit 20]
```

**Sample mode** for jobs a list run could not check (the VM agent's 60 s budget ran out before
their comments; issue #16): `--sample N --from FILE…` reads earlier `--out` files (a later
file's row for an ECU replaces an earlier one's) and, for each `differ` row, sends single-job
describes for N of the jobs whose comment only the VM lacked, spread over the SGBD. Each has its
own budget. One line per job, with `kind` as above plus `not_checked` (the VM still did not read
the description).

```sh
node dev/describe-diff.ts --ecu-path ~/ediabas-ecu/Ecu --out sample.jsonl --sample 3 --from full.jsonl --from recheck.jsonl
```
