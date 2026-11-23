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
> that are not made for it, and it has been tested on one car, at the desk only. Its
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
Apple Silicon Mac, with the cable in the VM but not in the car. No job has been run against a car
yet. ENET, ICOM and F-series cars are untested.

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

Version 0.6.2, job policy version 4, eleven MCP tools. Built and tested at the desk; **nothing
from 0.3.0 on has been run at the car.**

### What changed in 0.6.2

From a review of the EDIABAS path ahead of the first session at the car (Phase 6).

- **A job gets 10 s to stop after `apiBreak`** (was 2 s) before the agent treats EDIABAS as
  stuck until a restart. A job on the car's line may act on a break only between steps; 60 s
  plus 10 s still ends inside the Mac's 75 s.
- **A pinned approval whose description cannot be read is the EDIABAS error** (for example
  `IFH-0009`, a module that does not answer), as for an unpinned job, not `job_needs_review`.
  The job is still not run.
- `docs/at-the-car.md`: record the evals' own calls by typing their prompts at the car (the
  replay agent needs an exact match); describe the engine group with the ignition on; run each
  functional job on its own with `timeoutSeconds: 60` once EDIABAS is loaded; name only modules
  that answered in `ediabas_read_faults`. The skill says the same for functional jobs.
- Eval 01 no longer needs a fault with a mileage: `NVE_65` `FS_LESEN` declares none.

Open: the at-the-car checks ([docs/at-the-car.md](docs/at-the-car.md)), evals 01, 03 and 06
(they need fixtures recorded at the car), and a run without the skills that does worse for evals
07 and 08. Earlier versions and the phase results: [docs/history/changes.md](docs/history/changes.md).
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
