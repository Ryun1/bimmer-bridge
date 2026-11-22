---
name: bmw-standard-tools
description: Help the user drive BMW Standard Tools by hand in their Windows VM (UTM) for their BMW - INPA, Tool32, NCS Expert, WinKFP, NCS Dummy and BMW Coding Tool. Use this skill whenever they ask how to do something in INPA, Tool32 or NCS Expert, "what do I press", what a screen or menu shows, how to clear faults themselves, or ask about coding (mirrors, lights, any retrofit), flashing or programming a module, SP-Daten, NCS Expert trace files (FSW_PSW.TRC, NETTODAT.TRC) or what the cable is doing - even when they do not name the tool. Covers what each tool is for and how risky it is, watching mode (Claude reads a vm_screenshot, the user presses the keys), when a job belongs in INPA rather than the read-only bridge, what to back up and check before coding or flashing, and reading trace files with vm_logs_read.
---

# bmw-standard-tools

The user's Windows VM in UTM runs BMW Standard Tools over EDIABAS and a cable (usually K+DCAN)
to their BMW. This skill is for helping them use those tools by hand. Reading the car through
the bridge's own tools is the `ediabas-bridge` skill.

This skill is guidance. The bridge's tools enforce their own limits (read-only, no input into
the VM); nothing here is a way around them.

## The tools and their risk

Process names are from the VM discovery run of 2026-11-21 (the VM the bridge was built on); `vm_health` lists any of these
except NCS Dummy as "BMW tools running", because the bridge treats them as holding the cable.
What each tool does and how risky it is, is general knowledge about these programs, not
something recorded on a car.

| Tool | Process | What it is for | Uses the cable | Risk |
|---|---|---|---|---|
| INPA | `INPALOAD` | Diagnostics: fault memory, live values, activations | Yes | Low to medium: reading is safe; clearing loses evidence and an activation moves real parts |
| Tool32 | `TOOL32` | Runs any single EDIABAS job from any SGBD | Yes | High: any job, including writes |
| NCS Expert | `NCSEXPER` | Reads and writes module coding | Yes | High: wrong coding disables functions |
| WinKFP | `WINKFPT` | Flashes module firmware | Yes | Very high: an interrupted flash (power, cable, VM or Mac sleep, closing it) can brick a module, and recovery may not be possible at home |
| NCS Dummy | `NcsDummy` | Offline viewer/editor for NCS Expert trace files | No | Low, but its edits are what NCS Expert later writes to the car |
| BMW Coding Tool | `BMW Coding Tool` | Copies SP-Daten into the NCS Expert, WinKFP and EDIABAS folders | No | Medium: replaces the SGBD and data files every other tool and the bridge use |

Nothing in Tool32, NCS Expert or WinKFP stops a write; care and a backup do.

## What you do and what the user does

You cannot press, click or type anything in the VM; no bridge tool does that, deliberately.
The user presses every key. Never say or imply you pressed something, and never use computer use
or screen control on the UTM window: a key pressed by mistake in these tools can write to the
car, and the person at the car needs to be the one deciding.

## Watching mode

When the user wants help with a screen ("what do I press", "what does this show"). It uses only
`vm_list_windows` and `vm_screenshot`, which work while INPA is open; only the bridge's
`ediabas_*` calls are refused while a BMW tool holds the cable.

1. `vm_list_windows` to see what is open, then `vm_screenshot` of that window.
2. Describe what is on the screen, in plain English (much of it is in German): title, the
   values shown, and the function-key or menu labels visible.
3. Say which key or menu does what *according to what the screen shows*, and which one gets
   them where they want to go. Then the user presses it, and you take a new screenshot to follow.

Read the labels from the screenshot rather than reciting INPA menus from memory: INPA scripts
differ by version and by car, and `references/inpa-screens.md` holds only screens seen on the
install it was built on (so far one: the loader menu). If a label is unreadable, ask for a larger screenshot
or ask the user.

Point out any key on screen that writes or actuates (clear, delete, activate, code, program,
or German labels like "löschen", "Ansteuern") before they press anything near it.

## When to use INPA by hand instead of the bridge

The bridge only reads. Anything that writes is the user's job in INPA (or NCS Expert / WinKFP for
coding and flashing). The common case is **clearing faults**:

- First read and record the faults (through the bridge or INPA), so the codes are on record (in
  the car's fault log, or a car-specific skill's log if the user has one) before they disappear.
- Then, in INPA, the user opens the module's screen and uses its fault-memory function to
  clear. Use watching mode to find the right key on their screen rather than naming one from
  memory.
- Remind them to close INPA when done: while it is open, the bridge cannot use the cable.

This is the sanctioned route for writes the bridge does not offer. It is not a route round a
bridge refusal: if the bridge refused a read job (`job_not_allowed`, `job_needs_review`), do
not send the user to Tool32 to run the same job. Tool32 runs anything, including writes, which is
exactly what the refusal was guarding against.

## Coding and flashing

Never present coding or flashing as routine, even for a popular change such as folding
mirrors on lock. Explain what is involved, what can go wrong, and let the user decide. A coding or
flashing answer tells them each of these before they start (general practice for these tools,
not steps verified on any particular car):

- **Back up first.** Read and save the module's current coding (NCS Expert can read it out)
  and keep the trace files, so the change can be reversed.
- **Battery.** Coding and especially flashing need a
  steady supply for the whole job; a voltage drop mid-flash is a common way to brick a module.
  A battery charger or power supply on the car is the usual precaution, all the more on a car
  with a battery-drain fault.
- **Never interrupt.** Do not close NCS Expert or WinKFP, unplug the cable, switch off the
  ignition or let the VM or the Mac sleep in the middle of a job. When it is finished, unplug
  the cable from the car's OBD port.
- **You cannot read the car during the job.** Tell the user that while NCS Expert or WinKFP
  is open the bridge refuses the cable (`cable_in_use`), on purpose, so you cannot read the car
  until they close it, and that you will not ask them to close it mid-job so the bridge can
  read. Any reading waits until they say the job is finished.
- **SP-Daten.** Changing the data files with BMW Coding Tool replaces the SGBDs everything
  else uses; do it only deliberately, with a copy of the old files.

General step-by-step coding procedures from the web are not verified on the user's car. If
the user wants one, say so, and suggest checking each step against what their screens show.

## Trace files

NCS Expert writes trace files when it reads or codes a module, and EDIABAS can write traces.
Read them with `vm_logs_list` (newest first; use `glob`, for example `*.TRC`) and
`vm_logs_read` (use `tailBytes` or `grep` to keep it short). What the files hold is in
`references/trace-files.md`. The bridge only reads them; editing (NCS Dummy) is the user's.

## Hand-off

Fault codes and readings the user reads in INPA go to their car-specific skill, if they have
one (for example a `bmw-e61` skill), for interpretation and the car's fault log. Distances
miles first: `100,000 mi (160,935 km)`.

## References

- `references/inpa-screens.md` — INPA screens and keys seen on the install the bridge was built on
  (a 2005 E61's VM; built from real screenshots only).
- `references/trace-files.md` — what `FSW_PSW.TRC` and `NETTODAT.TRC` are for, and where
  traces live on that install (standard paths; check with `vm_logs_list`).
