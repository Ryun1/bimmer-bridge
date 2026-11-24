# policy: the job policy

| File | What |
|---|---|
| `policy.json` | the default-deny job policy, version 5. The agent embeds it at build time; the Mac server reads it at start-up |
| `policy-cases.json` | ECU/job pairs with the expected verdict (`allowed`, `rule`, `screen`) |
| `policy-parse-cases.json` | whole policy texts each parser must accept or refuse |

Both test suites (`vm-agent/EdiabasAgent.Tests/JobPolicyTests.cs` and
`mac-mcp/test/policy.test.ts`) run every case in both case files, so the C# and TypeScript
sides cannot drift apart.

Changing the policy:
1. Change `policy.json` and add or change cases in the same commit. A policy change without a
   case change is a bug.
2. If the file's shape or meaning changes, update [docs/api.md "Job policy"](../docs/api.md#job-policy)
   first, raise `version`, and change both parsers.
3. Run `./release.sh`, deploy with the Update line and restart Claude, so that both sides run the
   same version (`vm_health` warns when they differ).

Jobs that look like reads but write: the name rules and the description screen trust the
SGBD's job name and description. `dev/sgbd-sweep.ts` reads the SGBD files themselves and lists
every job the policy allows that sends a write service, under the SGBD's name and every group
that can resolve to it (copy `C:\EDIABAS\ECU\*.prg`, `*.PRG`, `*.grp` and `*.GRP` from the VM
into one folder on the Mac first; no cable or EDIABAS is used):

    node --import ./dev/ts-resolve.mjs dev/sgbd-sweep.ts <folder> --json sweep.json

Put every confirmed job in its `write` section into `deny.ecuJobs`, under every name it lists
(it prints the entries), with a case each. A write marked "not confirmed at a send" comes only
from a constant the replay never saw sent: check it by hand (`EHC2RR` `STATUS_CALIBRATION`
holds a `$3D` template but sends `$31`). Run it again after a policy change that allows more,
or a new SGBD set. Its `control` (input/output control,
routines), `define` (dynamic read definitions) and `session` sections are not put in this list:
many of those jobs are reads in practice (`$30` with "report current state", as `NVE_65`
`STATUS_BORDNETZSPANNUNG` sends) and stay with the description screen and the owner's review.
The sweep replays each job's code in order without following jumps or subroutine calls. Jobs
whose request it cannot follow and that hold no request constant are listed as
`unresolvedOnly` (about 10,700 on 2026-11-24, in UDS and ASAM-generated engine SGBDs; none on
the E61's 43 modules), so a job missing from its `write` section is not proven to be a read.

Other chassis: the `E60` entry in `allow.restrictedEcus` is the group SGBD of the
E60/E61/E63/E64 (5 and 6 Series), and its functional jobs (`IDENT_FUNKTIONAL`,
`FS_LESEN_FUNKTIONAL`) read identification and fault memory from the ECUs on those cars in one
call. Owners of another chassis can add their own group SGBD with the same kind of functional
read jobs, as a policy change like any other: add cases for it, raise `version`, and rebuild and
deploy so that the Mac server and the agent both run the new version.

What the rules mean, in plain words: [docs/safety.md](../docs/safety.md#the-job-policy-in-plain-words).
