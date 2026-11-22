# policy: the job policy

| File | What |
|---|---|
| `policy.json` | the default-deny job policy, version 3. The agent embeds it at build time; the Mac server reads it at start-up |
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

What the rules mean, in plain words: [docs/safety.md](../docs/safety.md#the-job-policy-in-plain-words).
