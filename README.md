# bimmer-bridge

A read-only bridge that lets Claude read diagnostics from a BMW through EDIABAS. The car is
reached through a Windows VM in UTM on a Mac, where BMW Standard Tools (INPA, Tool32, NCS
Expert, WinKFP) and EDIABAS talk to the car over a K+DCAN cable. A small agent in the VM
(`vm-agent/`, C#) runs allowlisted read-only EDIABAS jobs; a local MCP server on the Mac
(`mac-mcp/`, TypeScript) offers eleven tools to Claude Code and the Claude desktop app; skills in
`skills/` teach Claude how to run a diagnostic session.

> [!WARNING]
> **Disclaimer: use at your own risk.** This is a personal project, provided as is, with no
> warranty of any kind. It talks to a real car's control units through software and a cable
> that are not made for it, and it has been tested on one car, in one session at the car. Its
> read-only limits reduce the risk but cannot remove it: a fault in the code, the cable,
> EDIABAS, an SGBD file or the car could still leave a module, the battery or the car in a bad
> state. You are responsible for your car, for what you run on it and for checking any result
> before you act on it. It is not a substitute for a qualified mechanic or for BMW's own tools.
> The authors accept no liability for damage, loss or cost of any kind from using it. This
> project is not affiliated with or endorsed by BMW AG; BMW, EDIABAS, INPA and the other names
> are their owners' trademarks. See also the [MIT license](LICENSE).

## What it does and does not do

It reads fault memory and runs allowlisted read-only jobs (identification, `STATUS_` reads) on
up to 20 ECUs in one call, describes an SGBD's jobs, reports ignition and voltages, lists and
screenshots the VM's windows, and reads EDIABAS and NCS Expert trace files.

It has no endpoint, tool, flag or setting that could click or type in the VM, clear faults, code,
flash, run activations or adaptations, send raw telegrams, change the EDIABAS configuration, or
use the cable while a BMW tool is open in the VM. Anything that writes to the car is done by the
owner, by hand, in INPA or NCS Expert. How each limit is enforced: [docs/safety.md](docs/safety.md).

No BMW software, SGBD files or headers are stored in this repo.

## Which cars

Any BMW that EDIABAS and the SGBD files installed in the VM support: in practice the E-series
over a K+DCAN cable, and possibly F-series cars over ENET or an ICOM. Nothing in the code is
chassis-specific; Claude finds a car's modules and jobs with `ediabas_list_ecus` and
`ediabas_describe`. Whole-car jobs (`IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL`) are allowed only
on the group SGBDs listed in [policy/policy.json](policy/README.md).

**Tested:** one 2005 E61 550i, K+DCAN USB cable, EDIABAS 7.3.0, Windows 11 on ARM in UTM on an
Apple Silicon Mac. One session at the car (2026-11-23, 0.6.2, ignition on): fault reads,
identification, vehicle status and the `E60` whole-car reads
([results](docs/history/changes.md#phase-6-at-the-car-2026-11-23)). ENET, ICOM and F-series cars
are untested.

## Quick start

On a Mac with [UTM](https://mac.getutm.app/) and a Windows VM in which BMW Standard Tools
already talk to the car. Full steps: [docs/install.md](docs/install.md#setup-from-scratch).

1. `brew install node dotnet@10`, then `(cd mac-mcp && npm ci)` and `./release.sh` (or
   [use a GitHub release's agent zip](docs/install.md#2-build-and-test)).
2. If the VM is not named `Windows` in UTM, `cp config.local.example.json config.local.json`
   and set `vmName`.
3. Start the VM and log in. On the Mac run `python3 dev/serve.py --accept-token` and paste the
   **Install** line it prints into **Windows PowerShell** run as administrator in the VM.
4. Register the MCP server from the repo root:
   `claude mcp add --scope user bimmer-bridge -- /opt/homebrew/bin/node "$PWD/mac-mcp/dist/index.js"`
   (desktop app: [install.md step 4](docs/install.md#4-register-the-mcp-server)).
5. Install the skills: `skills/install.sh`.
6. In a new Claude session, ask "is the BMW VM up?".

<!-- STATUS: maintained by the coordinator -->
## Status

Version 0.7.0, job policy version 4, eleven MCP tools. The first session at the car was on
2026-11-23 with 0.6.2: the acceptance results are in
[docs/history/changes.md](docs/history/changes.md#phase-6-at-the-car-2026-11-23).

### What changed in 0.7.0

Formatter fixes and whole-car views, from the first session at the car (Phase 6).

- **Trip counts are not distances** (A1): `STAT_ANZAHL_FAHRTEN_*` and similar counts in the
  `KGM_60` history blocks are shown as counts, not as kilometres or miles.
- **Vehicle status labels** (A2, A3): the battery and KL30 values are labelled as cable
  readings, and the ignition values are shown as seen at the car.
- **"Not supported" answers** (C1): when a module answers that it does not support a job (for
  example `CAS` `STATUS_WAKEUP_HISTORY`), the result says it is a negative response from the
  module, not a bridge, cable or policy fault.
- **Whole-car views** (B1): `ediabas_job` on `E60` `FS_LESEN_FUNKTIONAL` gives one line per module
  with faults (name, address, code, present now or stored, inferred from `F_ART`) and a summary count;
  `IDENT_FUNKTIONAL` lists the modules that answered, with their SGBD and group. Both fit the
  text budget. A replay test over the real recordings (`dev/fixtures/battery`) checks the counts.

Evals run 2026-11-23 against the Phase 6 fixtures: 03 passes; 01 and 06 fail with the skill because Claude takes names from the skill's references instead of `ediabas_list_ecus`/`ediabas_describe` (rubric vs skill: owner to decide). See [docs/history/evals-2026-11-23.md](docs/history/evals-2026-11-23.md).

Open: a run without the skills that does worse for evals 07 and 08. Earlier versions and the phase results: [docs/history/changes.md](docs/history/changes.md).
<!-- /STATUS -->

## Docs

| Doc | What it holds |
|---|---|
| [docs/install.md](docs/install.md) | setup, updating, uninstalling, troubleshooting, logs |
| [docs/safety.md](docs/safety.md) | each read-only guarantee and what enforces it; approving one job |
| [docs/architecture.md](docs/architecture.md) | components, the request path, trust boundaries |
| [docs/tools.md](docs/tools.md) | the eleven MCP tools and every error code |
| [docs/at-the-car.md](docs/at-the-car.md) | checklist for a session at the car |
| [docs/development.md](docs/development.md) | building and testing without the car, fixtures, releasing |
| [docs/api.md](docs/api.md), [docs/deploy.md](docs/deploy.md) | the agent's HTTP contract; the Mac-to-VM deploy contract |
| [skills/README.md](skills/README.md) | the skills, their evals and how to install them |
| [docs/history/](docs/history/) | the build plans, phase results and earlier release notes, kept as written |

Personal settings stay out of the repo: the VM's name or MAC go in the git-ignored
`config.local.json`.

## License

[MIT](LICENSE). It covers this repo's code and docs only, not BMW's software or SGBD files.
