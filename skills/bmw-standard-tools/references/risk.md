# Risk of each BMW Standard Tools program

From the project plan's tool table (section 0), with install details from the VM discovery
run of 2026-11-21. Process names are as discovery found them; `vm_health` lists any of these
except NCS Dummy as "BMW tools running", because the bridge treats them as holding the cable.

| Tool | Process | Uses the cable | Risk | The bridge's role |
|---|---|---|---|---|
| INPA | `INPALOAD` | Yes | Low to medium: can activate components and clear faults | Ryan's manual tool; Claude sees it through screenshots only |
| Tool32 | `TOOL32` | Yes | High: runs any job, including writes | Reference only; the bridge does the same for allowed read jobs |
| NCS Expert | `NCSEXPER` | Yes | High: wrong coding disables functions | Never driven; its trace files are readable |
| WinKFP | `WINKFPT` | Yes | Very high: an interrupted flash can brick a module | Never driven; the bridge refuses all cable use while it runs |
| NCS Dummy | `NcsDummy` | No | Low: edits files that NCS Expert later writes to the car | None |
| BMW Coding Tool | `BMW Coding Tool` | No | Medium: replaces SGBD and data files | The bridge refuses cable use while it runs |
| EDIABAS | — | Yes | — | The layer under all of the above; what the bridge calls |

## What the risk means in practice

- **Low to medium (INPA).** Reading is safe. Its activation and clear functions change the
  car's state: a cleared fault memory loses evidence, and an activation moves real parts.
  Read and record faults before clearing them.
- **High (Tool32, NCS Expert).** One wrong job or coding value can switch off a function or
  leave a module misconfigured. Nothing stops a write in these tools; care and a backup do.
  The bridge never sends Ryan to Tool32 to run a job it refused.
- **Very high (WinKFP).** A flash that is interrupted (power, cable, VM or Mac sleep, closing
  the program) can leave a module unable to start. Recovery may not be possible at home.
- **Medium (BMW Coding Tool).** Replacing SP-Daten changes the SGBD files every other tool
  and the bridge rely on. Keep a copy of the old files.

## Precautions before coding or flashing

These are general practice, not steps verified on this car:

- Save the module's current coding and keep the trace files, so the change can be undone.
- Keep the car's voltage steady for the whole job (charger or power supply); this car has a
  battery-drain fault, so do not rely on the battery alone.
- Stop the VM and the Mac from sleeping, and leave the cable alone, until the job finishes.
- Do not start a bridge read while NCS Expert or WinKFP is open; the bridge refuses anyway.
