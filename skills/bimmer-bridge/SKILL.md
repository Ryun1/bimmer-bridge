---
name: bimmer-bridge
description: Operate the read-only bimmer-bridge tools (vm_* and ediabas_*) that look into the Windows VM in UTM on the user's Mac, where BMW Standard Tools and EDIABAS talk to the user's BMW over the cable. Use this skill whenever the user wants to read the faults, fault codes or live values from their BMW (night vision, engine, any module), asks about a battery drain, mentions EDIABAS, an SGBD or a .prg/.grp file, asks "is the VM up" or what is open in the VM, or wants a screenshot of the VM or an EDIABAS / NCS Expert trace file, even if they do not name the bridge. Covers the health check to run first, keeping ignition-on time short, finding ECUs and jobs with ediabas_list_ecus and ediabas_describe instead of guessing, what each error and refusal means and what to tell the user, and handing results to a car-specific skill if one is installed.
---

# bimmer-bridge

The `bimmer-bridge` MCP server lets you look into the Windows VM in UTM on the user's Mac. The
VM runs BMW Standard Tools (INPA, Tool32, NCS Expert, WinKFP) over EDIABAS and a cable (usually
K+DCAN) to the user's BMW. Which car it is, and which modules it has, comes from the car and
the VM (`ediabas_list_ecus`, `ediabas_describe`), not from this skill. The bridge was built on a
2005 E61 550i; notes below that hold only for an E60/E61 say so, and the reference files were
recorded on that car.

This skill is guidance. The tools enforce the limits themselves, on the Mac and in the VM;
nothing here is a way around them, and none is needed.

## What you can and cannot do

- You can read: fault memory, allowed read-only jobs, VM screenshots, trace files.
- You cannot write: no clearing faults, coding, flashing, activations or adaptations. No tool
  does it, so never promise it. Those are the user's to do by hand (see `bmw-standard-tools`).
- You cannot click, type or press anything in the VM, or start the VM or the ignition. The user does.

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
| `ediabas_vehicle_status` | one batch: ignition (`UTILITY STATUS_ZUENDUNG`), battery (`UTILITY STATUS_UBATT`), the interface's KL30/KL15 voltages (`TMODE`), and any voltage sources configured on the Mac (`voltageSources` in `config.local.json`; none by default). On the E61 (2026-11-23, ignition on) `STAT_ZUENDUNG` read 1 and the cable's KL15 `E02E0000`; with no car connected (2026-11-22) 0 and `00000000`. Ignition off at the car is not recorded yet. `STAT_UBATT` and KL30 come from the cable, so only a voltage source shows the car's supply voltage |
| `ediabas_read_faults` | fault memory (and optionally info memory) from 1–20 ECUs in one call |
| `ediabas_job`, `ediabas_jobs` | one allowed read-only job; or 1–20 of them in one call |

Names are base names; the prefix differs between Claude Code and the desktop app. All eleven
are built. If a session offers fewer, Claude is running an old Mac server: ask the user to
restart Claude. `[agent_outdated]` means the VM agent is older than the Mac server (`vm_health` shows
both): it needs the Update line; say so and stop. Do not get the result another way.

## Session routine

Ignition-on time drains the battery (more so on a car with a battery-drain fault, as the car
this was built on has), so it is the scarce resource. Do as much as you can before the user
turns it on.

1. **`vm_health` first, on its own**, every session and after any failure. Wait for its answer
   before any other bridge tool. If it fails, stop and tell the user the fix from the message. If
   it warns that the two versions differ, tell the user before reading the car. If it lists a
   BMW tool as running, `ediabas_*` calls are refused (`cable_in_use`): name the tool, ask the
   user to close it when they have finished with it, and make no `ediabas_*` call until they
   say it is closed. If it is WinKFP or NCS Expert, add that it must never be
   closed mid-flash or mid-coding: an interrupted flash can brick an ECU. `vm_*` tools are fine
   while a BMW tool runs (that is how watching mode in `bmw-standard-tools` works). Do not poll.
2. **Find the names before the ignition goes on.** Never guess an SGBD or job name, from memory
   or the web. `references/ecu-map.md` and `references/job-cookbook.md` hold what sessions on a 2005
   E61 recorded; on another car treat them as examples only, and even on an E61 anything not
   in them is unconfirmed until a tool shows it. Use `ediabas_list_ecus`
   (with a `filter`) and `ediabas_describe`. A job is runnable only if `ediabas_describe` shows
   it allowed and not "needs review". `ediabas_describe` needs the cable attached to the VM,
   but for most files not the car (desk, 2026-11-21): so have the user plug the cable into the
   Mac and tick it for the VM in UTM, and do the looking-up then. Some files would not describe
   without the module answering (on the E61: `N62_TUE2`, `ME9N62_2`, `GS19D`, `SZL_60`; see
   `references/ecu-map.md`). Describe large files with a `filter`: on the E61 a full list of
   `CAS` or `LM_60` took the whole 60 s limit and left its last jobs unscreened.
3. **Learn which modules the car has from the car, not by guessing names.** On an E60/E61
   (the `E60` group SGBD), run these first: `ediabas_job` on `E60` with `IDENT_FUNKTIONAL` lists the modules that answer, `FS_LESEN_FUNKTIONAL` reads
   all their faults in one job, and `C_FA_LESEN` on `CAS` or `LM_60` reads the vehicle order.
   Describe them beforehand to see the result names, and use what they return to pick SGBDs.
   Run each functional job as its own `ediabas_job` with `timeoutSeconds: 60`, after an
   earlier call has loaded EDIABAS (`ediabas_vehicle_status`): a request gets at most 60 s,
   and on the E61 (2026-11-23) `IDENT_FUNKTIONAL` took 3,925 ms and `FS_LESEN_FUNKTIONAL` 3,964 ms (recordings 0029, 0031), about 4 s each. Then name only the modules that
   answered in `ediabas_read_faults`: one that does not answer can use its whole timeout.
   The job policy allows these whole-car jobs only on `E60`, `E65`, `E70`, `E89X`, `F01` and `R56`; on another
   chassis `ediabas_describe` on its group SGBD shows them refused until the owner adds that group to
   the policy (`policy/README.md` in the repo). Until then, find the car's modules with
   `ediabas_list_ecus` and read them one SGBD at a time.
4. **State the plan:** which ECUs and jobs, in how many calls, and a rough time. Then ask the
   user to switch the ignition on and say when it is. Suggest a battery charger or support unit on
   the car for the session (general knowledge, not from this car): it takes away most of the
   time pressure and avoids communication errors from low voltage.
5. **`ediabas_vehicle_status`** (on the E61, 2026-11-23, ignition on: `STAT_ZUENDUNG` 1, the cable's
   KL15 `E02E0000`; with no car connected, 2026-11-22: 0 and `00000000`; ignition off at the car
   is not recorded yet. `STAT_UBATT` and KL30 come from the cable, and only a voltage source
   shows the car's supply voltage), then the reads, batched: one `ediabas_read_faults` for all
   the ECUs wanted (up to 20), and the status jobs in one `ediabas_jobs` call rather than one
   `ediabas_job` each.
6. **Report**, then say "ignition off" plainly, and remind the user to unplug the cable from
   the car's OBD port when they have finished (general knowledge: a connected interface can keep the
   car from sleeping).

**The car-session clock.** Every result of `ediabas_job`, `ediabas_jobs`,
`ediabas_read_faults` and `ediabas_vehicle_status` ends with "Car session: … since the first
read from the car (aim to finish within 10 min)." It counts from the first read from a module (not `TMODE` or `UTILITY`), not from the
ignition, and the clock resets after 5 minutes without such a read. At about 8 minutes, wrap up: finish the call in hand, add no new reads, and report.
At 10 minutes, stop and ask the user before any further read; offer to continue another time.
Read only what the question needs; reading every job on every ECU drains the battery and
buries the answer.

**The first call after a pause is slow.** The agent unloads EDIABAS after a short idle spell
(20 s by default), so the next `ediabas_*` call loads it again and can take 10–15 s (desk,
2026-11-21). That is normal: wait rather than retrying, and allow for it in the rough time.
`vm_health` shows whether EDIABAS is loaded. For a session at the car the user can raise the
idle time (`docs/at-the-car.md` in the repo).

**If the bridge tools disappear mid-session**, do not retry anything. Ask the user to switch the
ignition off while the link is down. When the tools return, `vm_health` "Last cable use" shows
whether the last job reached the car (2026-11-23: a link drop over mobile data; the agent was
unaffected).

**Long results.** Pass `results` (a semicolon list of result names, from `ediabas_describe`) to
trim history jobs such as the `KGM_60` blocks. A batch's items share one 60 s limit (the 12-item
battery batch took 19 s on 2026-11-23).

## Refusals and errors

A failed call starts with its code in brackets, `[code]`, then a message saying what happened
and what to do (for reaching the VM, relay its fix). `references/errors.md` covers the cases
where the message is not enough. The ones that need judgement here:

- **`[job_not_allowed]`** — refused by name. Say which job and rule; do not look for another
  job, tool or route that does the same thing.
- **`[job_needs_review]`, or a job `ediabas_describe` shows as "needs review"** — the job's
  own description (or its arguments' description) suggests it may not be a pure read, has
  none, or has changed since the user approved it. In your answer:
  - quote the description as returned, and say in plain English what it seems to mean;
  - say, in so many words, that the decision is the user's and **the safe default is not to
    run it**;
  - stop there. Do not give the `reviewedJobs` line, the hash or the steps to approve it, even
    though `ediabas_describe` prints them, and do not end by offering to run it once approved.
    Only if the user then says they want to approve it, give them the line `ediabas_describe`
    printed (no restart is needed);
  - do not point to INPA, Tool32 or another job or ECU as a way to get the same reading.
- **`[cable_in_use]`** — a BMW tool is open; see step 1. **`[cable_not_found]`** — ask the
  user to tick the cable for the VM in UTM's USB menu (UTM does not hand back a replugged cable).
- **`[ecu_not_found]`, `[job_not_found]`** — the name was wrong; go back to the lookup tools.
- **`[ediabas_error]`** — EDIABAS's own code and text. Report them as returned, with the ECU;
  do not guess what they mean on this car beyond what the message says.
- **`[ediabas_request_lost]`** — the job may have run. Do not repeat it unless the user asks.
- **`[ediabas_busy]`** — another EDIABAS call was still running; this one was not sent. Wait
  for the other answer, then ask again once.
Never retry in a loop: call `vm_health` once more after the user says they have fixed something.
Never fill in a result that was not returned; say what could not be read and why.

## Reporting

- Fault tables as the tool returned them: code, text, present or stored, frequency, conditions.
  An ECU with no faults says so; an ECU that errored says why. Do not drop either.
- Distances in miles first, km in brackets: `100,000 mi (160,935 km)`.
- Raw values (`raw: true`, every result field) only when the user asks for them.
- `ediabas_vehicle_status` may show readings as `key=value` because the field names are not
  yet confirmed for this car. Report them as shown; do not infer "ignition on" or a voltage
  from a field name you have not seen confirmed.
- Report what the tools returned. Do not add diagnostic claims the output does not support.

If the user has a car-specific skill that interprets fault codes and keeps the car's fault
log (the owner of the E61 this was built on uses one called `bmw-e61`), pass the codes and
readings to it, with the date and mileage, rather than interpreting them here.

## Screenshots and trace files

`vm_list_windows` gives the titles; `vm_screenshot` shows one window. A minimised window or a
locked screen is refused; The user restores or unlocks it. For help driving INPA, Tool32 or NCS
Expert by hand from a screenshot, use the `bmw-standard-tools` skill. `vm_logs_list` then
`vm_logs_read` (with `grep` or `tailBytes`) read trace files under the allowed folders only.

## References

`references/errors.md` (errors that need judgement), `references/ecu-map.md` (SGBD names seen
on the 2005 E61's install), `references/job-cookbook.md` (jobs, arguments and result names), and
`references/playbooks/` (`full-fault-sweep.md`, `battery-drain.md`: what is specific to each;
the routine above still applies). The files about the car hold only what a real session on
that E61 returned; another car's ECUs and jobs differ, so check them with `ediabas_list_ecus`
and `ediabas_describe`. When a session confirms a name or result field, tell the user so it
can be recorded (in the user's own copy or notes for another car); add nothing from memory or
the web.
