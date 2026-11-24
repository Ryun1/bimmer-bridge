# Phase 6 at the car: feedback and our response

> Historical record: design notes from 2026-11-22 to 2026-11-23. See [changes.md](../changes.md) for what shipped and [evals-2026-11-23.md](../evals-2026-11-23.md) for what was skipped.

**Date:** 2026-11-23. **Input:** the first session at the car, run from Claude Desktop with the
recording server (`~/ediabas-recordings/car-20261123`, 31 exchanges, outside the repo), and the
feedback Claude Desktop wrote afterwards (items A1–F3 below). Every claim was checked against
the code and the recordings before a decision was made. Versions during the session: Mac server and agent
0.6.2, policy version 4 (sha `5d0afef1659b`).

Plans that carry this out:

- [2026-11-23-phase6-results.md](../plans/2026-11-23-phase6-results.md): fixtures, skill
  references, docs, VM and Mac config. No code.
- [2026-11-23-formatter-fixes.md](../plans/2026-11-23-formatter-fixes.md): Mac formatter
  fixes and the functional-job formatters, released as 0.7.0.

## What the session established

| Fact | Evidence (recording / audit) |
|---|---|
| Ignition was on for every read (owner, 2026-11-23); ignition-on time about 18 min, over the 10 min target, mostly lost to a Claude Desktop link drop 19:52–19:55 (mobile data) | Desktop log `remote-tools-device ... ERR_INTERNET_DISCONNECTED` / handshake timeouts; bridge audit log shows no gap in the agent |
| `UTILITY` `STAT_ZUENDUNG` = 1 and `TMODE` KL15 = `E02E0000` with the ignition on; both 0 / `00000000` with no car (desk, 22/11) | `0022`, `dev/fixtures/no-car` |
| `STAT_UBATT` = 1 and KL30 = `E02E0000` identical with and without the car: cable-side | `0022`, `no-car` |
| `NVE_65` `STATUS_BORDNETZSPANNUNG` = 12 (whole volts) | `0022` |
| `D_MOTOR` resolves to `N62_TUE`; `d_0012` gives `IFH-0009` | `0024`, `0018` |
| `E60` `IDENT_FUNKTIONAL` 3,925 ms, 44 sets (43 modules + 1 without a name); `FS_LESEN_FUNKTIONAL` 3,964 ms, 45 sets, 15 modules with 24 codes; `VVT` and `VVT2` answer `ERROR_ECU_SERVICE_NOT_SUPPORTED` | `0029`, `0031` (agent `durationMs`) |
| The 12-item battery batch took 19,154 ms (2 items `IFH-0009` on `MPM_60`) | `0016` |
| DME: `2EF4` map thermostat (present), `2DED` quiescent-current violation (stored) | `0027` |
| `NVE_65` fault and info memory empty: the A752/A751 pair from 11/09 is gone | `0014`, `0020` |
| Three pinned `CAS` approvals answer `ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT` (`7F 31 12`, `7F 21 12`) | `0016` |
| `KOMB60` `STATUS_KLEMMENSPANNUNG` declares `STAT_KLEMMENSPANNUNG_WERT` (int) and `_EINH` "normally mV" | desk describe, `policy-v4-2026-11-22/...-0042-...` |
| `FS_LESEN_DETAIL` and `IS_LESEN_DETAIL` are already allowed by name (`allow.globalExact`) | `policy/policy.json` |
| The Mac already writes an audit line per call to `~/Library/Logs/ediabas-bridge/audit.log` (time, tool, outcome, ms, ecu, job) | 21 lines for the session |
| Claude Desktop reads its MCP servers from `~/Library/Application Support/Claude/claude_desktop_config.json`, not from `claude mcp` | the first `vm_health` from Desktop ran the production server and recorded nothing |

Phase 6 acceptance (PLAN.md §9): ignition and a voltage in volts shown: **pass**. `IDENT`
through `D_MOTOR` names the engine SGBD: **pass** (`N62_TUE`). `ediabas_read_faults` on `NVE_65`
shows A752/A751: **read passed, pair absent from the car** (cleared by the car or by the owner; not a bridge
fault). Ignition-on under 10 min: **fail** (about 18 min, link drop). Responses saved as fixtures:
**recorded**; scrubbing is in the results plan.

## Decisions, item by item

| Item | Decision | Why | Where |
|---|---|---|---|
| A1 trip counts formatted as miles | **Fix.** A name with an `ANZAHL` token, or with `KM` straight after a number token (`_0_5_KM`, `_UEBER_100_KM`), is not a distance. **Keep** whole-mile rounding (`0 mi (1 km)`): km in brackets carries the exact value and odometer style is deliberate (`formatKm` doc). | Confirmed in `formatEdiabas.ts` `isKmName`; the `_EINH` sibling rule already exists for `_WERT` fields and stays. | formatter plan, Task 1 |
| A2 KL30 / `STAT_UBATT` look like battery readings | **Relabel, don't drop.** Lines become "from the cable, not a battery reading". | They still show the cable is powered and answering; dropping them loses a cable check. | formatter plan, Task 2 |
| A3 ignition fields do change | **Label with what was seen**, and say ignition-off with the cable in the car is not yet recorded. | One observation (on) and one no-car run; "off at the car" is unobserved. | formatter plan, Task 2; results plan, Task 3 |
| B1 decode `FS_LESEN_FUNKTIONAL` | **Do**, plus a compact `IDENT_FUNKTIONAL` view (one line per module with its SGBD), so the two can be joined by eye. Present/stored is inferred from the KWP2000 storage-state bits (`F_ART & 0x60`: `0x60` present, `0x20` stored only), labelled "inferred"; other values print the byte undecoded. No automatic follow-up read. | 7 of 7 cross-checked codes agree; `0xB2` (PDC) and `0x01` (SINE) do not fit a single-bit rule. A follow-up read is the model's next call, not a hidden extra car read. | formatter plan, Tasks 3–4 |
| B2 fault detail tool | **No new tool now.** `FS_LESEN_DETAIL` is already allowed; describe it (desk first, else next car session), then add a cookbook entry. Revisit a tool only if the generic output is unusable. | Policy allows it; the gap is knowing its argument, not a tool. | results plan, Task 4 (cookbook "next session" list) |
| B3 long history results | **Skill note now** (use `results` to trim); summary formatter deferred. | One job needs it so far; YAGNI. | results plan, Task 4 |
| B4 per-call log | **Already exists** (`audit.log`). Document its path for at-car use. A tool to read it back is deferred. | Desktop did not know it exists. | results plan, Tasks 4–5 |
| B5 better voltage source | **Add `KOMB60` `STATUS_KLEMMENSPANNUNG` as the first `voltageSources` entry** in the Mac's `config.local.json` (not committed), keep `NVE_65` second. Confirm resolution at the next session. Added only **after** the evals run: the eval fixtures' vehicle-status batch was recorded with `NVE_65` alone, and the replay agent matches the request body exactly. | Declared int in "normally mV". | results plan, Task 5 |
| B6 request ids / last jobs in `vm_health` | **Decline for now.** "Last cable use" plus `audit.log` answered "was it sent?" exactly. | No gap shown. | — |
| C1 three useless `CAS` approvals | **Remove them** (owner's go-ahead needed), and print a plain note for any `ERROR_ECU_*NOT_SUPPORTED*` job status. | Fewer pinned approvals = smaller surface; the note stops readers taking a negative response for a bridge fault. | results plan, Task 1; formatter plan, Task 2 |
| C2 `NVE_65` `STATUS_PAIRING` | **Owner's decision**, nothing to build. | Policy flags it by design. | — |
| D1–D4 skill references | **Do**, with the corrections in this document (24 codes in 15 modules; durations from the agent). | Recorded evidence. | results plan, Tasks 3–4 |
| E `bmw-e61` skill | **Out of scope:** lives outside this repo. | — | — |
| F1 bit 0x40 rule | Answered by B1: two-bit storage state, "inferred". | — | — |
| F2 `KOMB60` resolution | Answered by B5: declared mV; confirm at the car. | — | — |
| F3 SINE at 0x50 | Next session: `ediabas_describe` / `IDENT` on `D_SINE`. | — | results plan, Task 4 |

## Not done in this round

A fault-detail tool (B2), a summary formatter for history blocks (B3), a tool to read
`audit.log` back (B4), request ids (B6). Each waits for evidence that the generic path is not
enough.
