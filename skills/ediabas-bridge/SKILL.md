---
name: ediabas-bridge
description: Operate the read-only ediabas-bridge tools (vm_* and ediabas_*) that look into the Windows VM in UTM on Ryan's Mac, where BMW Standard Tools and EDIABAS talk to his E61 over the cable. Use this skill whenever Ryan wants to read the faults, fault codes or live values from the car (night vision, engine, any module), asks about the battery drain, mentions EDIABAS, an SGBD or a .prg/.grp file, asks "is the VM up" or what is open in the VM, or wants a screenshot of the VM or an EDIABAS / NCS Expert trace file, even if he does not name the bridge. Covers the health check to run first, keeping ignition-on time short, finding jobs with ediabas_describe instead of guessing, what each error and refusal means and what to tell Ryan, and handing results to the bmw-e61 skill.
---

# ediabas-bridge

The `ediabas-bridge` MCP server lets you look into the Windows VM in UTM on Ryan's Mac. The VM
runs BMW Standard Tools (INPA, Tool32, NCS Expert, WinKFP) over EDIABAS and a K+DCAN cable to
his 2005 BMW E61 550i.

This skill is guidance. The tools enforce the limits themselves, on the Mac and in the VM;
nothing here is a way around them, and none is needed.

## What you can and cannot do

- You can read: fault memory, allowed read-only jobs, VM screenshots, trace files.
- You cannot write: no clearing faults, coding, flashing, activations or adaptations. No tool
  does it, so never promise it. Those are Ryan's to do by hand (see `bmw-standard-tools`).
- You cannot click, type or press anything in the VM, or start the VM or the ignition. Ryan does.

Never use computer use or screen control on the UTM window to do what a tool refused or what
no tool offers. The refusals exist because a wrong job can change or damage an ECU; a refusal
is a decision, not an obstacle. Report it.

## The tools

| Tool | Use |
|---|---|
| `vm_health` | both versions (Mac server, agent), BMW tools running (they hold the cable), EDIABAS DLL and whether it is loaded, policy version, approved jobs |
| `vm_list_windows`, `vm_screenshot` | what is open in the VM, front first; a picture of one window or the desktop |
| `vm_logs_list`, `vm_logs_read` | list and read EDIABAS / NCS Expert trace files |
| `ediabas_list_ecus` | SGBD names (`prg`) and group names (`grp`) installed in the VM |
| `ediabas_describe` | an SGBD's jobs and whether each is runnable; or one job's arguments and results |
| `ediabas_vehicle_status` | ignition, battery and cable-side voltage readings |
| `ediabas_read_faults` | fault memory (and optionally info memory) from 1–20 ECUs in one call |
| `ediabas_job`, `ediabas_jobs` | one allowed read-only job; or 1–20 of them in one call |

Names are base names; the prefix differs between Claude Code and the desktop app. All eleven
are built. If a session offers fewer, Claude is running an old Mac server: ask Ryan to restart
Claude. `[agent_outdated]` means the VM agent is older than the Mac server (`vm_health` shows
both): it needs the Update line; say so and stop. Do not get the result another way.

## Session routine

The car has a battery-drain fault, so ignition-on time is the scarce resource. Do as much as
you can before Ryan turns it on.

1. **`vm_health` first, on its own**, every session and after any failure. Wait for its answer
   before any other bridge tool. If it fails, stop and tell Ryan the fix from the message. If
   it warns that the two versions differ, tell Ryan before reading the car. If it lists a BMW tool as running, `ediabas_*` calls are refused (`cable_in_use`):
   name the tool, ask Ryan to close it when he has finished with it, and make no `ediabas_*`
   call until he says it is closed. If it is WinKFP or NCS Expert, add that it must never be
   closed mid-flash or mid-coding: an interrupted flash can brick an ECU. `vm_*` tools are fine
   while a BMW tool runs (that is how watching mode in `bmw-standard-tools` works). Do not poll.
2. **Find the names before the ignition goes on.** Never guess an SGBD or job name, from memory
   or the web. `references/ecu-map.md` and `references/job-cookbook.md` hold what sessions have
   recorded; anything not in them is unconfirmed until a tool shows it. Use `ediabas_list_ecus`
   (with a `filter`) and `ediabas_describe`. A job is runnable only if `ediabas_describe` shows
   it allowed and not "needs review". `ediabas_describe` needs the cable attached to the VM,
   but for most files not the car (desk, 2026-11-21): so have Ryan plug the cable into the Mac
   and tick it for the VM in UTM, and do the looking-up then. Four files would not describe
   without the module answering (`N62_TUE2`, `ME9N62_2`, `GS19D`, `SZL_60`; see
   `references/ecu-map.md`). Describe large files with a `filter`: a full list of `CAS` or
   `LM_60` took the whole 60 s limit and left its last jobs unscreened.
3. **Learn which modules the car has from the car, not by guessing names.** *Not yet run at
   the car.* Plan these as the first reads: `ediabas_job` on `E60` with `IDENT_FUNKTIONAL`
   lists the modules that answer, `FS_LESEN_FUNKTIONAL` reads all their faults in one job, and
   `C_FA_LESEN` on `CAS` or `LM_60` reads the vehicle order. Describe them beforehand to see
   the result names, and use what they return to pick SGBDs.
4. **State the plan:** which ECUs and jobs, in how many calls, and a rough time. Then ask Ryan
   to switch the ignition on and say when it is. Suggest a battery charger or support unit on
   the car for the session (general knowledge, not from this car): it takes away most of the
   time pressure and avoids communication errors from low voltage.
5. **`ediabas_vehicle_status`**, then the reads, batched: one `ediabas_read_faults` for all
   the ECUs wanted (up to 20), and the status jobs in one `ediabas_jobs` call rather than one
   `ediabas_job` each.
6. **Report**, then say "ignition off" plainly, and remind Ryan to unplug the cable from the
   car's OBD port when he has finished (general knowledge: a connected interface can keep the
   car from sleeping).

**The car-session clock.** Every result of `ediabas_job`, `ediabas_jobs`,
`ediabas_read_faults` and `ediabas_vehicle_status` ends with "Car session: … since the first
read from the car (aim to finish within 10 min)." It counts from the first read, not from the
ignition. At about 8 minutes, wrap up: finish the call in hand, add no new reads, and report.
At 10 minutes, stop and ask Ryan before any further read; offer to continue another time.
Read only what the question needs; reading every job on every ECU drains the battery and
buries the answer.

**The first call after a pause is slow.** The agent unloads EDIABAS after a short idle spell
(20 s by default), so the next `ediabas_*` call loads it again and can take 10–15 s (desk,
2026-11-21). That is normal: wait rather than retrying, and allow for it in the rough time.
`vm_health` shows whether EDIABAS is loaded. For a session at the car Ryan can raise the idle
time (`docs/at-the-car.md` in the repo).

## Refusals and errors

A failed call starts with its code in brackets, `[code]`, then a message saying what happened
and what to do (for reaching the VM, relay its fix). `references/errors.md` covers the cases
where the message is not enough. The ones that need judgement here:

- **`[job_not_allowed]`** — refused by name. Say which job and rule; do not look for another
  job, tool or route that does the same thing.
- **`[job_needs_review]`** — the job's own description (or its arguments' description)
  suggests it may not be a pure read, has none, or has changed since Ryan approved it. Show
  Ryan the quoted description, say what it seems to mean, and leave the decision to him. The
  safe default is not to run it; never present approving it as a routine step. If he decides
  to approve it, `ediabas_describe` on the job prints the exact line to add in the VM; no
  restart is needed.
- **`[cable_in_use]`** — a BMW tool is open; see step 1. **`[cable_not_found]`** — ask Ryan
  to tick the cable for the VM in UTM's USB menu (UTM does not hand back a replugged cable).
- **`[ecu_not_found]`, `[job_not_found]`** — the name was wrong; go back to the lookup tools.
- **`[ediabas_error]`** — EDIABAS's own code and text. Report them as returned, with the ECU;
  do not guess what they mean on this car beyond what the message says.
- **`[ediabas_request_lost]`** — the job may have run. Do not repeat it unless Ryan asks.
- **`[ediabas_busy]`** — another EDIABAS call was still running; this one was not sent. Wait
  for the other answer, then ask again once.
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

Ryan's `bmw-e61` skill interprets fault codes and keeps his car's fault log. Pass the codes and
readings to it, with the date and mileage, rather than interpreting them here.

## Screenshots and trace files

`vm_list_windows` gives the titles; `vm_screenshot` shows one window. A minimised window or a
locked screen is refused; Ryan restores or unlocks it. For help driving INPA, Tool32 or NCS
Expert by hand from a screenshot, use the `bmw-standard-tools` skill. `vm_logs_list` then
`vm_logs_read` (with `grep` or `tailBytes`) read trace files under the allowed folders only.

## References

`references/errors.md` (errors that need judgement), `references/ecu-map.md` (SGBD names seen
on this install), `references/job-cookbook.md` (jobs, arguments and result names), and
`references/playbooks/` (`full-fault-sweep.md`, `battery-drain.md`: what is specific to each;
the routine above still applies). The files about the car hold only what a real session
returned. When a session confirms a name or result field, tell Ryan so it can be recorded
there; add nothing from memory or the web.
