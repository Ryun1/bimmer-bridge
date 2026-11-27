# dev: tools for deploying and for testing without the car

`serve.py` (the Mac-side deploy server), the replay agent, the recording server, the fixture
scrubber and their tests. What each file is and how to run it:
[docs/development.md](../docs/development.md#the-dev-folder). The deploy server's contract:
[docs/deploy.md](../docs/deploy.md). Recorded fixtures: [fixtures/README.md](fixtures/README.md).

## Car comparison (`car-compare.ts`, M2)

`run` sends a job list (`dev/m2/session-b.json`, `session-c.json`) through one side, each job
alone and once, one JSON line per job. **It talks to the car**: only at the car, with Ryan's go
for that session (M2 plan, tasks 8 and 9). Before the first job it checks the side: the VM agent
answers and no BMW tool holds the cable, or `ediabasd`'s cable is `free`. An existing `--out` is
never overwritten. `compare` pairs two runs job by job: result sets per set by field name (type
and value), system results and `durationMs` ignored, EdiabasLib's extra `GRUPPE`/`FAMILIE`
ignored, `variant` and `jobStatus` compared, and a native `BIP-0007`/`BIP-0011` noted as the
EDIABAS 7.7.3 emulation difference. It also counts IFH-0003 per side.

```sh
node dev/car-compare.ts run --side vm --jobs dev/m2/session-c.json --out ~/m2/c-vm.jsonl
node dev/car-compare.ts run --side native --ediabasd-config ~/m2/ediabasd-c.json --jobs dev/m2/session-c.json --out ~/m2/c-native.jsonl
node dev/car-compare.ts compare ~/m2/c-vm.jsonl ~/m2/c-native.jsonl
```

Needs `mac-mcp` built (`npm run build`) and, for native, `ediabasd`'s Release build. Outputs stay
under `~/m2/`, never in the repo: a `C_FA_LESEN` reply holds the VIN.

## Gate screen (`gate-screen/`, M2)

Opens no device. For each SGBD named, runs every job the policy allows by name that takes no
argument through EdiabasLib and `KlineInterface` into an empty libkline replay, and reports what
libkline's frame gate did with the first telegram: refused (with its service byte), let through,
or nothing sent. An SGBD whose `INITIALISIERUNG` sends first is reported as not screened past
it; a group is screened by its identification telegram. `--with-args SGBD:JOB` also runs a job
whose arguments are optional. `--refused` writes the refused pairs for `dev/m2/gate-refused.json`
(names and service bytes only), which the job-list test checks every M2 list against. Results:
[m2-results.md](../docs/native/m2-results.md), section 2.

```sh
DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec /opt/homebrew/opt/dotnet@10/bin/dotnet build dev/gate-screen
DOTNET_ROOT=/opt/homebrew/opt/dotnet@10/libexec dev/gate-screen/bin/Debug/net10.0/gate-screen \
  --ecu ~/ediabas-ecu/Ecu --out ~/m2/gate-screen.json --refused dev/m2/gate-refused.json \
  --with-args E60:IDENT_FUNKTIONAL --with-args E60:FS_LESEN_FUNKTIONAL NVE_65 CAS …
```

## Telegrams (`telegrams.ts`, M2)

Reads files only. `compare` sets EDIABAS's IFH trace from the VM (`IfhTrace = 3`) against a
`libkline` recording of the same read: one exchange per `SENDECUTEL` block on the VM side, one
per `send` natively, checksums and the K-line echo aside; it prints the first exchange where they
diverge, with both sides' bytes. `timing` prints, per recording, its latency label, echo and
reply times (min / median / max ms), short reads and refusals.

```sh
node dev/telegrams.ts compare ~/m2/b-ifh.trc ~/m2/b/kline-….jsonl
node dev/telegrams.ts timing ~/m2/b/*.jsonl
```

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
