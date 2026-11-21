---
name: ediabas-bridge
description: Operate the read-only ediabas-bridge tools (vm_* and ediabas_*) that look into the Windows VM in UTM on Ryan's Mac, where BMW Standard Tools and EDIABAS talk to his E61 over the cable. Use this skill whenever Ryan wants to read the faults, fault codes or live values from the car (night vision, engine, any module), asks about the battery drain, mentions EDIABAS, an SGBD or a .prg/.grp file, asks "is the VM up" or what is open in the VM, wants a screenshot of the VM or an EDIABAS / NCS Expert trace file, or whenever any vm_* or ediabas_* tool is available or about to be used, even if he does not name the bridge. Covers the health check to run first, keeping ignition-on time short, finding jobs with ediabas_describe instead of guessing, what each error and refusal means and what to tell Ryan, and handing results to the bmw-e61 skill.
---

# ediabas-bridge

The `ediabas-bridge` MCP server lets you look into the Windows VM in UTM on Ryan's Mac. The VM
runs BMW Standard Tools (INPA, Tool32, NCS Expert, WinKFP) over EDIABAS and a K+DCAN cable to
his 2005 BMW E61 550i.

This skill is guidance. The limits are enforced by the tools themselves, on the Mac and in the
VM; nothing here is a way around them, and none is needed.

## What you can and cannot do

- You can read: fault memory, allowed read-only jobs, VM screenshots, trace files.
- You cannot write: no clearing faults, coding, flashing, activations or adaptations. No tool
  does it, so never promise it. Those are Ryan's to do by hand (see `bmw-standard-tools`).
- You cannot click, type or press anything in the VM, or start the VM or the ignition. Ryan does.

Never use computer use or screen control on the UTM window to do what a tool refused or what
no tool offers. The refusals exist because a wrong job can change or damage an ECU; a refusal
is a decision, not an obstacle. Report it.

## Which tools exist in this session

| Tool | Use |
|---|---|
| `vm_health` | agent, Windows session, BMW tools running (they hold the cable), EDIABAS DLL, policy version |
| `vm_list_windows` | what is open in the VM, front first |
| `vm_screenshot` | a picture of one window or the desktop |
| `vm_logs_list`, `vm_logs_read` | list and read EDIABAS / NCS Expert trace files |
| `ediabas_list_ecus` | SGBD names (`prg`) and group names (`grp`) installed in the VM |
| `ediabas_describe` | an SGBD's jobs and whether each is runnable; or one job's arguments and results |
| `ediabas_vehicle_status` | ignition, battery and cable-side voltage readings |
| `ediabas_read_faults` | fault memory (and optionally info memory) from 1–20 ECUs in one call |
| `ediabas_job` | one allowed read-only job |

Tools are named here by base name; the prefix differs between Claude Code and the desktop app.
The bridge is being built in stages, so a session may offer only some of these. A tool that is
not offered, or one that answers `agent_outdated`, is not installed yet: tell Ryan that and
stop (for `agent_outdated`, the VM agent needs the Update line once a build with that tool
exists; see `references/errors.md`). Do not try to get the same result another way.

## Session routine

The car has a recurring battery-drain fault, so ignition-on time is the scarce resource. Do
everything that does not need the ignition before Ryan turns it on.

1. **`vm_health` first**, every session and after any failure. If it lists a BMW tool as
   running, name it, ask Ryan to close it when he is finished with it, and stop until he says
   it is closed. If it is WinKFP or NCS Expert, add that it must never be closed mid-flash or
   mid-coding: an interrupted flash can brick an ECU. Do not poll while you wait.
2. **Find the names.** Use `ediabas_list_ecus` (with a `filter`) and `ediabas_describe` to find
   the SGBD and the jobs. Never guess an SGBD or job name, from memory or the web: a wrong name
   wastes ignition time at best. `references/ecu-map.md` and `references/job-cookbook.md` hold
   names confirmed on this car; anything not in them is unconfirmed until a tool shows it.
   A job is runnable only if `ediabas_describe` shows it allowed and not "needs review".
   `ediabas_describe` reads the SGBD file rather than the car, so it can usually be done
   before the ignition goes on.
3. **State the plan:** which ECUs and jobs, in how many calls, and a rough time. Then ask Ryan
   to switch the ignition on and say when it is.
4. **`ediabas_vehicle_status`**, then the reads, batched: one `ediabas_read_faults` call for
   all ECUs wanted (up to 20), and any `ediabas_job` calls back to back with no pauses.
5. **Report**, then say "ignition off" plainly at the end.

Keep each session under 10 minutes of ignition-on time. Say so when that is getting close and
offer to stop and continue another time rather than adding reads. Read only what the question
needs; reading every job on every ECU drains the battery and buries the answer.

Run anything that talks to the car only when Ryan has asked for it and is with the car.

## Refusals and errors

A failed call returns one message saying what happened and what to do. The full list, with
causes and what to tell Ryan, is in `references/errors.md`; read it when the message is not
enough. The ones that need judgement:

- **`job_not_allowed`** — the policy refused the job by name. Tell Ryan which rule, and do not
  look for another job, tool or route that does the same thing.
- **`job_needs_review`** — the job's own description suggests it may not be a pure read (or it
  has none). Show Ryan the quoted description, say what it seems to mean, and leave the
  decision to him. The safe default is not to run it; never present approving it as a routine
  step to get the read done.
- **`cable_in_use`** — a BMW tool is open; the message names it. Ask Ryan to close it (step 1)
  and wait. Do not retry in a loop.
- **`ecu_not_found`, `job_not_found`** — the name was wrong. Go back to `ediabas_list_ecus` /
  `ediabas_describe`; do not try variations of the name.
- **`ediabas_error`** — EDIABAS's own code and text, for example an ECU that did not answer.
  Report the code and text as returned; do not guess what they mean on this car.
- **Reaching the VM** (`vm_not_running`, `agent_not_answering`, `local_network_blocked`, ...)
  — tell Ryan the fix from the message. `local_network_blocked` is macOS blocking `node`, not
  the VM being off.

Never retry in a loop: call `vm_health` once more after Ryan says he has fixed something.
Never fill in a result that was not returned; say what could not be read and why.

## Reporting

- Fault tables as the tool returned them: code, text, present or stored, frequency, conditions.
  An ECU with no faults says so; an ECU that errored says why. Do not drop either.
- Distances in miles first, km in brackets: `121,609 mi (195,712 km)`.
- Raw values (`raw: true`, every result field) only when Ryan asks for them.
- `ediabas_vehicle_status` may show readings as `key=value` because the field names are not
  yet confirmed for this car. Report them as shown; do not infer "ignition on" or a voltage
  from a field name you have not seen confirmed.
- Report what the tools returned. Do not add diagnostic claims the output does not support.

## Hand-off to bmw-e61

Ryan's `bmw-e61` skill interprets fault codes and keeps his car's fault log. Pass the fault
codes and readings to it for interpretation and for the log, with the date and mileage, rather
than interpreting them here.

## Screenshots and trace files

`vm_screenshot` shows a window; `vm_list_windows` gives the titles to ask for. A minimised
window or a locked screen is refused; Ryan restores or unlocks it. When Ryan wants help driving
INPA, Tool32 or NCS Expert by hand from a screenshot, use the `bmw-standard-tools` skill.
`vm_logs_list` then `vm_logs_read` (with `grep` or `tailBytes` to keep it short) read trace
files under the allowed folders only.

## References

- `references/errors.md` — every error code, its cause and what to tell Ryan.
- `references/ecu-map.md` — this car's ECUs and SGBD names, as confirmed by real sessions.
- `references/job-cookbook.md` — jobs confirmed on this car, with arguments and result fields.
- `references/playbooks/full-fault-sweep.md` — reading fault memory across many ECUs.
- `references/playbooks/battery-drain.md` — gathering evidence for the battery drain.

The reference files about the car hold only what a real session returned. When a session
confirms a name or a result field, tell Ryan so it can be recorded there; do not add anything
from memory or the web.
