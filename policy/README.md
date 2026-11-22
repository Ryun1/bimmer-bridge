# policy: the job policy

| File | What |
|---|---|
| `policy.json` | the default-deny job policy, version 4. The agent embeds it at build time; the Mac server reads it at start-up |
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

Other chassis: the `E60` entry in `allow.restrictedEcus` is the group SGBD of the
E60/E61/E63/E64 (5 and 6 Series), and its functional jobs (`IDENT_FUNKTIONAL`,
`FS_LESEN_FUNKTIONAL`) read identification and fault memory from the ECUs on those cars in one
call. Owners of another chassis can add their own group SGBD with the same kind of functional
read jobs, as a policy change like any other: add cases for it, raise `version`, and rebuild and
deploy so that the Mac server and the agent both run the new version.

What the rules mean, in plain words: [docs/safety.md](../docs/safety.md#the-job-policy-in-plain-words).
