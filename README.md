# ediabas-bridge

A read-only bridge that lets Claude read diagnostics from a BMW through EDIABAS. The car is
reached through a Windows VM in UTM on a Mac, where BMW Standard Tools (INPA, Tool32, NCS
Expert, WinKFP) and EDIABAS talk to the car over a K+DCAN cable. It was built on a 2005 E61
550i and is meant for any car EDIABAS can talk to ([Which cars](#which-cars)). A small agent
in the VM (`vm-agent/`, C#) runs allowlisted read-only EDIABAS jobs, lists windows, takes screenshots and
reads trace files. A local MCP server on the Mac (`mac-mcp/`, TypeScript) finds the VM (from its
newest DHCP lease, see [docs/architecture.md](docs/architecture.md#the-request-path)), calls
the agent and offers eleven tools to Claude Code and the Claude desktop app. Skills in `skills/`
teach Claude how to run a diagnostic session.

## Which cars

Any BMW that EDIABAS and the SGBD files installed in the VM support. In practice that means the
E-series (roughly the late 1990s to about 2013: E46, E39, E60/E61, E90, E70 and the rest) over a
K+DCAN cable, and possibly F-series cars over ENET or an ICOM where their SGBDs are installed.
The bridge only runs read jobs by name through EDIABAS; nothing in its code is
chassis-specific, and it finds a car's modules with `ediabas_list_ecus` and their jobs with
`ediabas_describe` rather than from a built-in list.

**What has actually been tested:** one car, a 2005 E61 550i, with a K+DCAN USB cable, EDIABAS
7.3.0 and Windows 11 on ARM in UTM on an Apple Silicon Mac. Every check so far was at the desk,
with the cable in the VM but not in the car ([docs/history/phase-results.md](docs/history/phase-results.md)):
no job has yet been run against a car ([Status](#status)). ENET, ICOM and F-series cars are
untested.

What is specific to a car or chassis:
- **Whole-car jobs.** The job policy allows `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` (which
  modules answer, and all their faults, in one job) only on the `E60` group SGBD (the E60/E61) and
  on the `E65`, `E70`, `E89X`, `F01` and `R56` SGBDs. For another chassis whose group SGBD has these jobs, add it to
  `policy/policy.json` with its test cases ([policy/README.md](policy/README.md)), then rebuild
  and run the Update line (the agent embeds the policy); until then, read modules one SGBD at a
  time.
- **`voltageSources`** in `config.local.json` on the Mac: extra voltage jobs for
  `ediabas_vehicle_status`. Empty by default; the `UTILITY` and `TMODE` readings work on any car.
  An E60/E61 with night vision can add `{ "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" }`
  (that module's supply voltage) ([docs/tools.md](docs/tools.md#ediabas_vehicle_status)).
- **The skills' reference data** (ECU map, job cookbook, playbooks, the INPA screen) was
  recorded on that E61's VM. For another car it is an example only: the skills tell Claude to
  find the car's own names with `ediabas_list_ecus` and `ediabas_describe`.

## What it does and does not do

It does:
- read fault memory (`FS_LESEN`, `IS_LESEN`) from up to 20 ECUs in one call;
- run allowlisted read-only jobs (identification, `STATUS_` reads), one at a time or up to 20
  in one call, and show their results;
- read which modules answer, and all their faults, in one whole-car job (`E60`
  `IDENT_FUNKTIONAL`, `FS_LESEN_FUNKTIONAL` on an E60/E61; not yet run at the car);
- list the SGBD files in the VM and describe an SGBD's jobs, saying which are runnable;
- report ignition and voltage readings;
- list the windows open in the VM and take a screenshot of one, or of the desktop;
- list and read trace files under the configured log folders (by default `C:\EDIABAS\TRACE`
  and `C:\NCSEXPER\WORK`).

It does not, and has no endpoint, tool, flag or setting that could:
- click, type or send any other input to the VM;
- clear faults, code, flash, or run activations or adaptations;
- send raw telegrams or change the EDIABAS configuration;
- use the cable while a BMW tool is open in the VM (there is no override).

Anything that writes to the car is done by the owner, by hand, in INPA or NCS Expert. How each of
these limits is enforced: [docs/safety.md](docs/safety.md).

No BMW software, SGBD files or headers are stored in this repo (see `.gitignore`).

## How a request flows

```
Claude (Claude Code or the desktop app)
  |  MCP over stdio
  v
MCP server on the Mac (mac-mcp)     job policy check, audit line; finds the VM; token from the Keychain
  |  HTTP on UTM's shared network, port 8765, Bearer token
  v
agent in the VM (EdiabasAgent.exe)  source address and token check, job policy check, description
  |                                 screen, BMW-tool conflict check, audit line
  v
EDIABAS (api32.dll)  ->  K+DCAN cable (USB, passed through to the VM by UTM)  ->  car  ->  ECU
```

A refused job stops on the Mac and never reaches the VM; the agent checks again on its own. One
EDIABAS request runs at a time. Details: [docs/architecture.md](docs/architecture.md).

## Quick start

On a Mac with [UTM](https://mac.getutm.app/) and a Windows VM in which BMW Standard Tools
already talk to the car. The full steps, with what success looks like at each, are in
[docs/install.md](docs/install.md#setup-from-scratch).

1. On the Mac: `brew install node` and `brew install dotnet@10`, then `(cd mac-mcp && npm ci)`
   and `./release.sh` (or [use a GitHub release's agent zip](docs/install.md#2-build-and-test)).
2. If the VM is not named `Windows` in UTM, `cp config.local.example.json config.local.json`
   and set `vmName` in it.
3. Start the VM and log in. On the Mac run `python3 dev/serve.py --accept-token`, and paste the
   **Install** line it prints into **Windows PowerShell** run as administrator in the VM. Stop
   `serve.py` with Ctrl-C afterwards.
4. Register the MCP server: `claude mcp add --scope user ediabas-bridge -- /opt/homebrew/bin/node "$PWD/mac-mcp/dist/index.js"`
   from the repo root (the desktop app:
   [install.md step 4](docs/install.md#4-register-the-mcp-server)).
5. Install the skills: `skills/install.sh`.
6. In a new Claude session, ask "is the BMW VM up?". Claude calls `vm_health`.

[Updating](docs/install.md#updating), [uninstalling](docs/install.md#uninstalling) and
[troubleshooting](docs/install.md#troubleshooting) are in the same file.

## Safety

The bridge is read-only by construction, and its hard rules are enforced in code on both sides,
not only in tool descriptions or skills. The rules, the code that enforces each one, the job
policy in plain words and how to approve a single job are in [docs/safety.md](docs/safety.md).
Check any change against that list.

<!-- STATUS: maintained by the coordinator -->
## Status

Version 0.5.0 (agent, MCP server and `VERSION`), job policy version 4, eleven MCP tools.
**0.5.0 is released (tag `v0.5.0`, 2026-11-22) and installed in the VM as an Update from 0.4.0:
the [upgrade steps](docs/install.md#upgrading-to-050) and desk checks pass, except re-recording
the skill references' job verdicts under policy version 4. Nothing in 0.3.0, 0.4.0 or 0.5.0 has
been run at the car.**

0.5.0 in the VM, 2026-11-22 (cable in the VM, not in the car):
- Approvals pinned before the update; `vm_health` then showed 5 pinned, 0 ignored, and policy
  version 4 with the same SHA-256 on both sides.
- `update.ps1` under Windows PowerShell 5.1: the first Update added `WINKFP` and `ISTAGUI` and
  recorded `conflictDefaultsOffered`, with `reviewedJobs` and the other keys unchanged; a second
  Update changed nothing; a default removed by hand stayed removed after a third.
  `vm-setup/*.ps1` also parse under `pwsh` 7.7 on the Mac.
- `--selftest`: PASS (after the self-test's own 8.3 check was fixed, PR #2).
- Desk checks: `TMODE INFO`; `ediabas_vehicle_status` (TMODE lines; `NVE_65` voltage from the
  Mac's `config.local.json`); INPA open gives `cable_in_use`; an INPA screenshot is readable;
  `ediabas_describe E60` shows `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL` runnable; a
  `reviewedJobs` edit shows `[approvals changed]` first on a result and after the `[code]` on an
  error; with the car absent, the info memory line gives the real no-response error. Stopping a
  running job when a BMW tool opens mid-job (`apiBreak`) is covered by the agent's tests only: a
  desk-side describe is too short to open INPA during it.
- Restart checks: after a VM reboot the agent came back by itself at log-on, and the running
  Mac server reconnected without a restart. After the Mac slept, the agent and the bridge stayed
  up, but the cable was handed back to the Mac: a `TMODE` job then failed with error 48 until the
  cable was ticked for the VM again in UTM. The error hint for 48 now says so.

| Phase | State |
|---|---|
| 0 Discovery | done (2026-11-21) |
| 1 Agent skeleton | done (2026-11-21): installed in the VM, self-test passes, came back after a Windows restart |
| 2 EDIABAS at the desk | done (2026-11-21, agent 0.2.0): every desk check passes. Agent 0.2.2 deployed and used at the desk on 2026-11-22 |
| 3 Mac MCP | done: the ten tools of 0.2.x (`ediabas_jobs`, the eleventh, came in 0.3.0). Checked in Claude Code and in the desktop app (`ediabas_describe` on `NVE_65`, 2026-11-21) |
| 4 Screenshots | done (2026-11-21): INPA screenshot readable, locked screen refused |
| 5 Logs | done (2026-11-21): listing, reading and every path refusal pass, in the VM self-test too |
| 6 At the car | built (vehicle status, fault tables). **Not run: this is the owner's, at the car** |
| 7 Skills | both skills installed; replay agent with `idle` and `inpa-open` fixtures; evals 02, 05, 07 and 08 passed with the skills (2026-11-21), but for 07 and 08 the run without the skills was not shown to do worse, so PLAN.md's acceptance is not met for them; skill references filled from the desk recordings of 2026-11-21/22 |
| 8 Hardening | built: `VERSION`, `release.sh`, the docs. The plan's acceptance check (reinstalling from this README alone) has not been run; updates from the README's lines have |

Open:
- **Phase 6 at the car**, the owner's: [docs/at-the-car.md](docs/at-the-car.md).
- **Re-record the skill references' job verdicts under policy version 4**
  (`dev/record-server.ts`, [docs/development.md](docs/development.md)).
- **Evals 01, 03 and 06** wait for the `nve-faults` and `battery` fixtures, recorded at the car;
  eval 04's `needs-review` fixture can be recorded at the desk
  ([dev/fixtures/README.md](dev/fixtures/README.md)).
- **Eval acceptance**: 07 and 08 still need a run without the skills that does worse.
- **Skill reference entries that need car recordings**: the vehicle-status field names, the
  engine SGBD, and the SGBDs that can only be described with the control unit answering.

### What changed in 0.5.0

- **Pin your approvals before updating.** Only pinned entries (`ECU:JOB@hash`) approve anything;
  unpinned ones are ignored. `ediabas_describe` prints the line to paste. Steps:
  [Upgrading to 0.5.0](docs/install.md#upgrading-to-050).
- Approvals are listed in `vm_health`; a change prints `[approvals changed]`; tools refuse with
  `[policy_mismatch]` if the Mac's and the VM's policy files differ.
- Policy version 4: `E65`, `E70`, `E89X`, `F01`, `R56` restricted like `E60`; switching and
  triggering words added to the description screen.
- The agent stops a running job (`apiBreak`) within about 250 ms if a BMW tool starts, and checks
  again just before `apiInit`. `WINKFP` and `ISTAGUI` join the default conflict list (Update adds
  the defaults a `config.json` was never offered, records them in `conflictDefaultsOffered` so
  one you remove stays removed, and keeps `config.json.bak`).
- The Mac sends the token only to an address that proves itself with `GET /hello` in the same
  call; the VM is found from its newest DHCP lease on UTM's network only.
- A job the SGBD lacks is `ediabas_error` again (`job_not_found` only comes from
  `ediabas_describe` now). This reverses a 0.3.0 item.
- `ediabas_vehicle_status` runs as one jobs batch from the Mac. Move `voltageSources` from the
  VM's `config.json` to the Mac's `config.local.json` and delete the VM key (the agent warns
  while it is there).
- `/logs/read` refuses a log file that is itself a link; a failed `apiEnd` shows in `vm_health`;
  screenshots fall back to the screen copy for a window that does not answer in 500 ms.
- Update: backs up the exe and rolls back on failure; the firewall rule is re-pinned to the
  current gateway.
- The recorder moved to `dev/` (`dev/record-server.ts`).
- A 0.5.0 Mac server needs a 0.5.0 agent: `vm_health` warns on a version difference, and a 0.4.0
  agent answers `/hello` with 401, which shows as `[agent_outdated]`.

Earlier versions: [docs/history/changes.md](docs/history/changes.md). The acceptance results of
Phases 0 to 5 are in [docs/history/phase-results.md](docs/history/phase-results.md).
<!-- /STATUS -->

## Where to find what

| Doc | What it holds |
|---|---|
| [docs/install.md](docs/install.md) | setup from scratch, updating, uninstalling, the scripts in the VM, troubleshooting, logs |
| [docs/architecture.md](docs/architecture.md) | components, the request path, the agent's threads, where the policy runs, logs, trust boundaries |
| [docs/safety.md](docs/safety.md) | each read-only guarantee and what enforces it; the job policy in plain words; approving one job |
| [docs/at-the-car.md](docs/at-the-car.md) | checklist for a session at the car (Phase 6) |
| [docs/development.md](docs/development.md) | building and testing without the car, the `dev/` tools, the replay agent, recording fixtures, policy fixtures, releasing |
| [docs/api.md](docs/api.md) | the agent's HTTP contract, including the exact job policy rules |
| [docs/tools.md](docs/tools.md) | the eleven MCP tools and every error code |
| [docs/deploy.md](docs/deploy.md) | the wire contract between the Mac and the VM: `dev/serve.py`, the pasted lines, the token hand-off |
| [mac-mcp/README.md](mac-mcp/README.md) | the MCP server: environment variables, finding the VM, token, recording |
| [vm-agent/README.md](vm-agent/README.md) | the agent: projects, config file, command-line options |
| [policy/README.md](policy/README.md) | the job policy file and its shared test cases |
| [dev/fixtures/README.md](dev/fixtures/README.md) | recorded fixtures: layout, recording and scrubbing a scenario |
| [skills/README.md](skills/README.md) | the two skills, their evals and how to install them |
| [skills/bmw-e61-skill-addition.md](skills/bmw-e61-skill-addition.md) | optional: a section to paste into a car-specific skill of your own (written for a private `bmw-e61` skill), so it hands over to the bridge |
| [docs/history/](docs/history/) | historical: [PLAN.md](docs/history/PLAN.md) (build plan v2), [plan-v1-original.md](docs/history/plan-v1-original.md) (the original, ISTA-based plan), [plan-review.md](docs/history/plan-review.md) (why v2 differs), [plan-review-fixes.md](docs/history/plan-review-fixes.md) (the fixes that became 0.3.0), [phase-results.md](docs/history/phase-results.md) (Phases 0 to 5), [changes.md](docs/history/changes.md) (what changed in 0.3.0 and 0.4.0) |

The historical docs are kept as written at the time, for one owner's car and VM; where they
disagree with the current docs, the current docs win.

Personal settings and notes stay out of the repo: the VM's name or MAC go in the git-ignored
`config.local.json` at the repo root (see [Setup from scratch](docs/install.md#setup-from-scratch)),
and notes about your own car belong in a git-ignored file or your own skill, not in the
committed docs or skill references.

## Layout

| Path | What |
|---|---|
| `vm-agent/` | the Windows agent (C#, .NET 10, win-x86). `vm-agent/build.sh` writes `release/EdiabasAgent.zip` |
| `vm-setup/` | `discover.ps1`, `install.ps1`, `update.ps1`, `uninstall.ps1`, run in the VM |
| `mac-mcp/` | the local MCP server Claude starts (TypeScript); built to `mac-mcp/dist/index.js` |
| `policy/` | `policy.json`, the job policy both sides load, and its shared test cases `policy-cases.json` and `policy-parse-cases.json` |
| `dev/serve.py` | Mac-side server that hands the scripts and the agent to the VM and takes the token back |
| `dev/replay-agent.ts` | a stand-in agent that answers from recorded fixtures (no VM, no car) |
| `dev/scrub-fixtures.ts` | copies a raw recording into `dev/fixtures/` with identifiers removed |
| `skills/` | Claude skills for using the tools, and their evals; `skills/install.sh` installs them |
| `release.sh` | checks versions, runs every test suite, builds the agent zip and the MCP server |
| `config.local.example.json` | example of `config.local.json` (git-ignored): your VM's name or MAC, read by the MCP server and `dev/serve.py` |
