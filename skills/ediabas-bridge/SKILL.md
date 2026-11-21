---
name: ediabas-bridge
description: Operate the read-only ediabas-bridge tools that look into the Windows VM (UTM) on Ryan's Mac where BMW Standard Tools and EDIABAS talk to his E61 over a K+DCAN cable. Use this skill whenever Ryan wants to read faults, fault codes or live values from the car, mentions EDIABAS, INPA, Tool32, NCS Expert, WinKFP, an SGBD or a .prg file, asks "is the VM up", "is the agent running" or what is open in the VM, or whenever any vm_* or ediabas_* tool is available or about to be used, even if he does not name the bridge. It covers what the tools can and cannot do, the health check to run first, what each error means and what to tell Ryan, and what is not built yet.
---

# ediabas-bridge

The `ediabas-bridge` MCP server lets you look into the Windows VM in UTM on Ryan's Mac. That VM
runs BMW Standard Tools (INPA, Tool32, NCS Expert, WinKFP, NCS Dummy, BMW Coding Tool) over
EDIABAS and a K+DCAN cable to his 2005 BMW E61 550i.

This skill is guidance. The limits are enforced by the tools themselves, on the Mac and in the
VM; nothing here is a way around them.

## What you can and cannot do

- You can only read. Every tool is read-only.
- You cannot click, type or send input to the VM, and you cannot clear faults, code, flash, run
  activations or adaptations. No tool does that, so never promise it.
- You cannot start the VM or log in to Windows. Ryan does both.

If Ryan asks for something that writes (clearing faults, coding, flashing), say the bridge
cannot do it and that it is a job for him to do by hand in INPA or NCS Expert.

Never use computer use, screen control or any other route to operate the UTM window to get
around a refusal or a missing tool. A refusal is the answer; report it.

## Tools available today

| Tool | What it returns |
|---|---|
| `vm_health` | Agent version and uptime, Windows version, Windows session (interactive or session 0), user and elevation, listening address, which BMW tools are running, whether the EDIABAS DLL was found, last cable use |
| `vm_list_windows` | Visible top-level windows, front-most first: title, process, PID, size, minimised |

Refer to tools by these base names; the prefix differs between Claude Code and the desktop app.

## Session routine

1. Call `vm_health` first, every session, and again after any tool fails.
2. Read it, and tell Ryan only what matters:
   - **Session.** "Windows session 0 — NOT an interactive session" means the agent is not on
     the desktop. Ask Ryan to log in to Windows.
   - **BMW tools running.** "none" means nothing else is holding the cable. If any are
     listed, the bridge will not use the cable while they run (see below).
   - **EDIABAS DLL.** "NOT found" means EDIABAS is not where the agent was configured to look.
     Tell Ryan; car-facing reads will not work until it is fixed.
   - **Last cable use** is "never (since the agent started)" until the EDIABAS tools exist.
3. Call `vm_list_windows` when you need to know what Ryan has open, for example which INPA or
   Tool32 screen is in front. "No visible windows with a title" usually means nobody is logged
   in to Windows.

## Cable sharing

Only one program can use the cable at a time. While INPA, Tool32, NCS Expert, WinKFP or BMW
Coding Tool is running, the bridge will not use the cable, and there is no override.

- If one of these is open and Ryan wants the bridge to read the car, name the tool and ask him
  to close it when he is finished with it. Then stop and wait; do not keep polling.
- WinKFP (flashing) and NCS Expert (coding) must never be interrupted. A job arriving mid-flash
  can brick an ECU. If either is running, do not ask Ryan to close it in a hurry and do not
  suggest killing it; wait until he says it has finished.

## When something fails

A failed tool call returns a message that already says what to do. Look up the code and the fix
in `references/errors.md` when you need more than the message.

- Tell Ryan plainly what he needs to do (start the VM, log in, allow `node` under macOS
  Local Network, re-run the installer).
- `local_network_blocked` means macOS stopped the connection, not that the VM is down. Do not
  tell Ryan the VM is off; give him the Local Network fix from the message.
- Do not retry in a loop. Call `vm_health` once more after Ryan says he has fixed it.
- Never invent or guess results. If something could not be read, say so.

## Battery care

The car has a recurring battery-drain fault. When the car-facing tools arrive, keep every
session with the ignition on short: plan the reads first, batch them, do not leave the
ignition on while idle, and tell Ryan to switch the ignition off when you are done. Today's
tools do not touch the car, so this does not apply yet.

## Reporting

- Distances in miles first, km in brackets: `121,609 mi (195,712 km)`.
- Say plainly when something could not be read, and why, rather than leaving a gap.
- Report what the tool returned. Do not add diagnostic claims that the output does not support.

## Hand-off to bmw-e61

Ryan's `bmw-e61` skill interprets fault codes and keeps his car's fault log. Pass any fault
codes and readings to it for interpretation and logging, rather than interpreting them here.

## Not available yet

These are planned but not built. If Ryan asks for something that needs them, tell him it is
not built yet. Do not improvise with other tools, and do not try to call them.

- `ediabas_read_faults` — read fault memory from one or more ECUs
- `ediabas_job` — run one allowed read-only EDIABAS job
- `ediabas_describe` — list an SGBD's jobs, or one job's arguments and results
- `ediabas_vehicle_status` — ignition and battery voltage
- `vm_screenshot` — a picture of a VM window or the desktop
- `vm_logs_list`, `vm_logs_read` — list and read EDIABAS / NCS Expert trace files

So today you cannot read faults or live values from the car, and you cannot see an INPA screen.
`vm_list_windows` can tell you which window is open, not what it shows. For readings, Ryan can
use INPA himself and tell you what it shows.
