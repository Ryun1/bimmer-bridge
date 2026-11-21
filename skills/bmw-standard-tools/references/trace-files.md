# Trace files

What the common trace files are for, in general terms. **Not yet checked on this install:**
no NCS Expert or EDIABAS trace has been read through the bridge, so file names, locations and
contents below are general knowledge to confirm against a real file before relying on them.
Read files with `vm_logs_list` and `vm_logs_read`; the bridge only reads them.

## What the VM discovery found (2026-11-21)

- EDIABAS traces would go to `C:\EDIABAS\TRACE`. EDIABAS tracing is switched off in its
  configuration (`ApiTrace = 0`, `IfhTrace = 0`), so the folder held one empty file. Turning
  tracing on means editing `EDIABAS.INI`, which the bridge never does; that is Ryan's choice.
- NCS Expert's working folder `C:\NCSEXPER\WORK` had no module trace yet.

## NCS Expert

| File | What it is for (general, unverified here) |
|---|---|
| `FSW_PSW.TRC` | The coding of the last module read or coded, as readable function names (FSW) and their values (PSW). This is what people edit (by hand or in NCS Dummy) to change a coding option, and keep as a record of the coding before a change. |
| `NETTODAT.TRC` | The same module's coding as raw data (net data), as NCS Expert read or would write it. Useful to compare before and after, less readable. |

Both are overwritten by the next read or coding job, so a copy is the only backup. Ryan makes
that copy; the bridge cannot write files.

When reading one for Ryan: say which module and when (the file's modified time from
`vm_logs_list`), and quote values as they appear. Do not suggest coding changes from it as a
routine step; coding is covered in `../SKILL.md`.

## EDIABAS

With tracing on, EDIABAS writes API and interface traces of every job it runs. They help
diagnose communication failures. On this install tracing is off (see above), so expect little.

## Entries confirmed on this install

None yet. When a real file has been read, record its path, what it contained and the date
here.
