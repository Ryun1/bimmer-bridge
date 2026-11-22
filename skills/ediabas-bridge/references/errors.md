# ediabas-bridge errors that need judgement

Every failed call starts with its code in brackets (`[job_not_allowed] ...`) and then says what
happened and what to do. Relay that message. What each code means is listed once, in the
"Errors" section of `docs/tools.md` in the ediabas-bridge repository (not part of this skill);
this file covers only what to tell the user where the message is not enough.

General rule: tell the user what to do, do not retry in a loop, and call `vm_health` once more
after they say it is fixed. Never fill in results that were not returned. A refusal is the answer:
never look for another tool, job or route (and never computer use on the VM window) round it.

## Reaching the VM

- **`[local_network_blocked]`** is macOS blocking `node`, not the VM being off. Do not say the
  VM is down. The permission is tied to the `node` binary's path, so a Homebrew upgrade of
  node can bring the prompt back.
- **`[no_token]`, `[unauthorized]`, or `[wrong_service]` saying the agent "could not prove it
  holds this Mac's token".** Relay the fix from the message: on the Mac,
  `python3 dev/serve.py --accept-token`, then paste the **Install** line it prints in the VM
  (it keeps the VM's token and sends it to the Keychain; the installer never shows the token).
  Never ask the user to paste the token into the chat or write it into a file.
- **`[agent_outdated]`, `[bad_response]`, or a version warning in `vm_health`.** `vm_health`
  shows the Mac server's version and the agent's. If the Mac server is the older one, Claude
  is running a server from before an update: the user restarts Claude. If the agent is older, it
  needs the Update line (Deploy steps below). Then stop: do not get the result another way.
  A `[bad_response]` that names an HTTP status is different: the agent's address answered with
  an HTTP error that is not in the agent's format. Run `vm_health`, then ask again once if the
  result is still needed.

## Refusals

- **`[policy_mismatch]`.** Nothing was sent. Tell the user the agent or the Mac server is out
  of date: they run the Update line and restart Claude. Then stop; do not look for another way.
- **`[job_not_allowed]`.** If the user believes a job is a pure read that should be allowed, that is
  a change to the policy files in the repo, made deliberately with its test cases, never
  mid-session.
- **`[job_needs_review]`.** The message says which of these it is:
  - the job's description matches a screen term (a word that suggests it may start, write,
    clear, set or change something), or says nothing but a label, or is empty;
  - the job was called with arguments, and the argument description matches a term or the
    arguments are not described at all;
  - an approved job's description has changed since the user approved it, is empty now, or could
    not be read.

  Show the user the quoted text, say in plain English what it seems to say, and that the
  bridge will not run it unless they decide it is a pure read. The safe default is to skip it;
  never present approving it as a routine step, and never hold a session at the car for it. If
  they decide to approve it, `ediabas_describe` on that job prints the exact entry to add to
  `reviewedJobs` in the VM's `config.json`, pinned to the description as it is now; the agent
  picks it up at the next EDIABAS call, no restart. An approval written without the `@hash`
  part approves nothing (since 0.5.0); `vm_health` counts such entries as ignored, and
  `ediabas_describe` prints the pinned entry to replace each with. An approved job called with
  arguments still goes through the argument check.

## EDIABAS

- **A full `ediabas_describe` runs out of time.** Not an error: the call succeeds, but the jobs
  at the end come back unscreened (`not_checked`, not runnable). Seen at the desk on 2026-11-21
  (E61) for `CAS` (150 jobs) and `LM_60` (134 jobs). Describe large files with a `filter` or one job
  at a time; do not repeat the full describe hoping it gets further.
- **`[cable_in_use]` with WinKFP or NCS Expert.** Say it must not be closed or killed mid-flash
  or mid-coding: an interrupted flash can brick an ECU. Wait for the user to say it is finished.
- **`[ediabas_error]` 100 `SYS-0010: INITIALIZATION ERROR` from `ediabas_describe`.** Seen at the
  desk on 2026-11-21 (on the E61's VM) for the four files listed in `ecu-map.md`, which appear
  to need the module to answer even to list their jobs. It does not show whether the file fits
  the car. Plan to
  describe them at the car; do not retry at the desk.
- **After the cable is replugged: `[ediabas_error]` 16 `IFH-0006: COMMAND NOT ACCEPTED` on every
  real SGBD (with `TMODE` still answering), or `[cable_not_found]` after an agent restart.** Seen
  at the desk on 2026-11-21/22. Both cleared when the cable was ticked for the VM again in
  UTM's USB menu. Do not tell the user the ECU did not answer.
- **After the Mac slept: `[ediabas_error]` 48 `IFH-0038: INTERFACE COMMAND NOT IMPLEMENTED`.**
  Seen at the desk on 2026-11-22: sleeping hands the cable back to the Mac, and the agent and
  `vm_health` stay up, so only EDIABAS calls fail. Ask the user to tick the cable for the VM
  again in UTM's USB menu before suggesting anything about the car.
- **`[ediabas_error]` "The job finished but its results could not be read".** The job ran.
  Report EDIABAS's code and text; do not run it again without asking the user.
- **`[ediabas_request_lost]`.** Tell the user the job may have run. Call `vm_health`; repeat
  the job only if the user asks.
- **`[ediabas_error]` on a job run, with a hint about the job name.** A job the SGBD lacks
  also gives `ediabas_error`; the agent does not check. If the name may be wrong, check it with
  `ediabas_describe` (`[job_not_found]` comes from describe only); never try variations of the name.
- **`[not_run]`.** The message says why: the request's time limit, a wait behind another
  EDIABAS request for the whole limit, a BMW tool opened during the request, an earlier job
  that did not stop, or an abandoned request. Read what was not run in a later, smaller call
  only if it is still needed, keeping the car-session clock in mind.
- **`[job_timeout]`.** Do not rerun straight away; each attempt costs ignition-on time. Check
  `ediabas_vehicle_status` or ask the user whether the ignition is on.
- **`[ediabas_unavailable]`, or `vm_health` says "EDIABAS: STUCK".** A job did not stop when
  asked, so the agent leaves EDIABAS alone until it restarts. The user restarts Windows in the VM.
  The bridge never changes EDIABAS's configuration.

## Deploy steps

Use these only when a fix above calls for them. The user runs everything in the VM; give them
one line at a time.

1. On the Mac, with the VM running: `python3 dev/serve.py` (add `--accept-token` to send the
   agent's token to the Keychain). It prints the **Install** and **Update** lines; copy the
   whole line from its output, never an old one. If it says `release/EdiabasAgent.zip` is not
   built, the release has to be built first (`./release.sh`).
2. In the VM, in **Windows PowerShell** run as administrator (not PowerShell 7), paste the
   **Install** line (first install, repair, re-sending the token) or the **Update** line (a
   new build of an installed agent).
3. Stop `serve.py` (Ctrl-C) afterwards. The user restarts Claude if the Mac server was rebuilt.
   Then call `vm_health`.
