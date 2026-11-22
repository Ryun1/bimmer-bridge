# Plan review — 2026-11-21

> Historical record: the review of the original plan, 2026-11-21. For the current state, see the [README](../README.md).

> **Status:** adopted. [PLAN.md](../PLAN.md) v2 incorporates this review, with the
> recommended option taken for each decision at the end. See the addendum at the bottom for
> what changed after the review was written. This review refers to the
> [original plan](plan-v1-original.md).

Review of the [original plan](plan-v1-original.md) before building. The architecture (elevated agent in the
interactive session, UIA first, screenshots as fallback, two-sided job policy, init/end per
request) is sound and stays. The items below are corrections, safety gaps and re-ordering.

Items marked **(verify)** are my belief, not confirmed on this VM.

## Checked on this Mac

| Fact | Result | Affects |
|---|---|---|
| Claude Code's shell | Native macOS, not a sandboxed Linux VM. It can reach the UTM network directly | §0, §8, §11 |
| .NET SDK | Not installed. Homebrew offers 11.0.100 | §1, build |
| Node | v24.16.0 under nvm | §7 |
| `claude_desktop_config.json` | Has no `mcpServers` key. "Control Chrome" is an extension, not a config entry | §7 |
| UTM VM "Windows" | QEMU backend, aarch64, network mode Shared, MAC `F2:E5:81:1E:82:07`, currently stopped | §4, §6 |
| `utmctl` | Present at `/Applications/UTM.app/Contents/MacOS/utmctl`, not on PATH | §10 |

Two of the four "ask Ryan" questions in §4 are now answered (network mode is Shared; the VM
is Windows on ARM under QEMU).

## A. Corrections — the plan is wrong or will break

1. **.NET 8 is out of support** (ended 10 Nov 2026). Target `net10.0` (LTS) instead.
   The SDK needs installing on the Mac first.
2. **The test layout cannot run on the Mac.** `vm-agent.Tests` references a
   `net8.0-windows` / `win-x86` WinExe, which will not execute on macOS arm64. Split it:
   - `IstaAgent.Core` (`net10.0`, no Windows APIs): `JobPolicy`, path rules, result shaping,
     the UIA text renderer working on a plain node model. Tested on the Mac.
   - `IstaAgent` (Windows host): Kestrel, FlaUI, capture, P/Invoke.
   - Windows-only behaviour (junctions, alternate data streams, 8.3 names, device names) can't
     be tested faithfully on macOS. Run those through `IstaAgent.exe --selftest` in the VM,
     and optionally on a GitHub Actions `windows-latest` runner.
3. **The sandbox premise in §0 doesn't apply to Claude Code.** I can run the `curl`
   acceptance checks for Phases 1–4 myself and iterate without Ryan relaying output. The
   native MCP server is still needed for the desktop app, and it can also be registered in
   Claude Code. §11 "Ryan runs everything inside the VM" still holds.
4. **The VM's IP is a DHCP lease, but the plan hard-codes it** in `ISTA_AGENT_URL`,
   `bindAddress` and the firewall rule. Move IP discovery from v2 into v1:
   - Mac: resolve the IP from the VM's fixed MAC via `arp -an`, with `utmctl ip-address` as a
     second source and `ISTA_AGENT_URL` as an override.
   - Agent: default `bindAddress` to `auto`, and rebind when the address changes.
   - Don't assume `192.168.64.0/24`. Derive the subnet from the adapter; the allowed remote
     address is the adapter's default gateway.
5. **`/ediabas/vehicle` probably can't return a voltage.** With a K+DCAN cable,
   `UTILITY STATUS_UBATT` reports battery present / not present, not volts **(verify)**. A
   real voltage has to come from an ECU job (DME / IBS), found with `_JOBS`. This matters
   because of the battery-drain fault.
6. **The denylist blocks useful reads.** Substring matching rejects valid `STATUS_` jobs:
   `ADAPT` blocks `STATUS_ADAPTION…`, `LERN` blocks `STATUS_LERNWERTE`, `ENDE` blocks any
   name containing `SENDER` or `BLENDE`, and `SPEICHER`, `START`, `STOP`, `INIT`, `PROG`
   behave the same way. It fails safe, but it removes the adaptation and learned-value reads
   that are most useful on an N62. Proposal:
   - keep substring matching for unambiguous write verbs (`SCHREIBEN`, `LOESCHEN`, `STEUERN`,
     `CODIER`, `FLASH`, `VORGEBEN`, `SETZEN`, `ABGLEICH`, `KALIB`);
   - match the ambiguous words (`START`, `STOP`, `ENDE`, `INIT`, `RESET`, `LERN`, `ADAPT`,
     `PROG`, `SPEICHER`) only as whole `_`-delimited tokens;
   - log every rejected job name so over-blocking is visible.

   **Decision needed** — see the end.
7. **Keep the policy as data, not as two implementations.** One `policy.json` (exact names,
   patterns, denied words) loaded by both the agent and the Mac server, plus the shared
   fixture. The two sides then can't drift.

## B. Safety gaps

8. **The macOS dialog is not proof of a human.** Any Claude session with computer use on
   this Mac can click "Clear faults". The same applies to ISTA itself through the UTM
   window, which is outside this project's control. Options:
   - **Defer fault clearing out of v1 (recommended).** INPA clears faults in seconds; without
     Phase 6, v1 has no write path at all.
   - Or gate it with Touch ID (a small Swift `LAContext` helper) instead of a button.
9. **Table virtualisation conflicts with "no input".** WPF DataGrid and ListView expose only
   the rows currently on screen. Reading the rest needs `ScrollPattern` or
   `ItemContainerPattern`, which changes ISTA's UI. Spec:
   - the reader never calls a state-changing pattern (Scroll, Invoke, Realize, SetValue,
     SetFocus); enforce this by wrapping FlaUI behind a read-only interface;
   - return `rowCount` and `rowsReturned`, so Claude knows the table is partial and asks
     Ryan to scroll.
10. **`grep=<regex>` runs in an elevated process.** Use `RegexOptions.NonBacktracking` and
    cap the pattern length.
11. **The process check in rule 5 is only a proxy for "cable in use".** ISTA can leave
    helper processes behind, and EDIABAS may run its own host process **(verify)**.
    Phase 0 should list them; the runner should also map EDIABAS interface errors (port
    busy) to `cable_in_use`.
12. **Deployment integrity.** Print the zip's SHA-256 on the Mac and check it with
    `Get-FileHash` in the VM before installing. `install.ps1` must be safe to re-run and
    must keep the existing token and config on upgrade.
13. **Repo hygiene.** `.gitignore` already excludes BMW binaries, `api.h`, SGBD files,
    discovery output and tokens.

## C. De-risking and order, for building in one day

14. **Spike UIA in Phase 0.** The largest unknown is how much of ISTA is exposed through
    UIA. Add a short Windows PowerShell 5.1 script (`System.Windows.Automation`, nothing to
    install) that dumps the ISTA window tree. Five minutes with ISTA open decides whether
    Phase 3 is text-first or screenshot-first, before any C# is written.
15. **Give discovery a return channel.** A small receiver on the Mac, bound to the bridge
    address, so `discover.ps1` uploads its output, the `api32.dll` export list and `api.h`
    as files instead of Ryan pasting text back. These stay local and are git-ignored.
16. **Reorder the phases:** 0 → 1 skeleton → 3 screen → 2 MCP → 4 logs → 5 EDIABAS. Seeing
    ISTA is the main value and can be tested with `curl` from here; the MCP layer is thin
    once the endpoints exist.
17. **Split Phase 5 into desk and car.**
    - 5a, at the desk with the cable unplugged: `INFO` and `_JOBS` on `NVE_65` exercise
      init → job → result sets → end without a car **(verify they run offline)**.
    - 5b, at the car: only `IDENT`, `FS_LESEN` and the conflict check. This keeps
      ignition-on time short.
18. **One-command agent update.** `irm http://<host>:8000/update.ps1 | iex` stops the task,
    verifies the hash, swaps the exe and restarts. There will be many rebuilds today.

## D. Smaller spec gaps

19. **UIA reader:**
    - add `TextPattern`, which is how FlowDocument and document viewers expose their text
      (likely where test-plan text lives);
    - use a `CacheRequest` to fetch properties in bulk; 4,000 cross-process calls from an
      emulated x86 process will exceed the 10 s budget;
    - report a minimised window as such instead of restoring it.
20. **Logs:** open files with `FileShare.ReadWrite | Delete`, because ISTA holds its logs open.
21. **`ediabasConfigOverride`** appears in the config with no specification. Define it or
    remove it.
22. **Raw JSON after every tool result** roughly doubles the output. Make it opt-in
    (`raw: true`).
23. **`X-Window-Title`** must be percent-encoded; window titles can be non-ASCII.
24. **Node path:** the nvm path in the desktop config breaks on the next Node upgrade.
    Point it at a stable symlink.
25. **Cloud sessions:** the claim that local MCP servers are proxied into linked cloud
    sessions is untested. If that matters, add it to the Phase 2 acceptance check.

## Decisions for Ryan

1. **Fault clearing:** defer out of v1 (recommended), keep with Touch ID, or keep as planned.
2. **Job policy:** token-based denylist as in item 6 (recommended), or keep the strict
   substring denylist and accept the blocked reads.
3. **Everything else:** accept as listed, or call out items to drop.

Once these are settled, the changes get folded into PLAN.md as v2 and the build starts at
Phase 0.

## Addendum — after the review

**The VM has BMW Standard Tools, not ISTA** (INPA, Tool32, NCS Expert, WinKFP, NCS Dummy,
BMW Coding Tool on the desktop). Consequences taken into v2:

- The UI Automation reader is dropped (items 9 and 19 no longer apply). INPA draws its own
  screens, so screenshots are the only view of it.
- The EDIABAS runner becomes the core and moves to Phase 2.
- The conflict list grows to cover NCS Expert, WinKFP and BMW Coding Tool, plus any process
  with `api32.dll` loaded.
- The agent is no longer elevated by default.
- The project is renamed from `ista-bridge` to `ediabas-bridge`.

**Findings from looking up the installed software and SGBDs:**

- `TMODE` is a simulated ECU that answers with no car and no cable, which gives a real desk
  test for the P/Invoke layer. It also contains raw-telegram and programming-voltage jobs,
  so it gets an exact-name allowlist.
- `UTILITY` contains `ABS_RELAIS` and `IF_RESET` beside its two status jobs, so it also gets
  an exact-name allowlist. Its `STATUS_UBATT` result has no unit.
- `NVE_65 STATUS_BORDNETZSPANNUNG` returns volts, so a real voltage is available from a
  known ECU.
- Reads such as `STATUS_NWGADAPTION` and `STATUS_DKLERN_KT` exist on the N62 engine ECU and
  would have been blocked by substring matching (item 6).
- A `STATUS_` name is not a guarantee of a pure read (one N62 job's description reportedly
  mentions starting a routine). v2 adds a screen on each job's description, with a
  per-job approval list.
- The `pydiabas` source gives the api32.dll signatures and constants to check against.
