# Formatter fixes and whole-car views (0.7.0) — Implementation Plan

> Historical record: carried out 2026-11-22 to 2026-11-23. The checkboxes were not ticked as the work was done, so they do not show what was finished; see [changes.md](../changes.md) for what shipped and [evals-2026-11-23.md](../evals-2026-11-23.md) for what was skipped.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix what the first session at the car showed wrong in the Mac's text output, add
compact views of the two whole-car jobs, and release 0.7.0.

**Architecture:** All changes are in the Mac server's formatter (`mac-mcp/src/formatEdiabas.ts`)
and the docs that describe its output. No agent change, no policy change, no new tool.
`raw: true` keeps returning the agent's JSON unchanged.

**Tech Stack:** TypeScript (Node 20+), vitest, zod.

**Spec:** `docs/superpowers/specs/2026-11-23-phase6-feedback-response.md` (items A1, A2, A3, B1, C1).

**Depends on:** `2026-11-23-phase6-results.md` Task 2 (fixtures `battery` and `nve-faults`) for Task 5's replay test.

## Global Constraints

- The formatter never hides data: every field of every set appears somewhere in the text, or the
  text says `raw: true` shows the rest (existing rule, `formatEdiabas.ts` header).
- Interpretation added by the formatter is labelled as such ("inferred", "seen at the car 2026-11-23"); no meaning is invented.
- Policy version stays 4; `policy/` untouched.
- Version 0.7.0 everywhere in Task 5 only (`VERSION`, `vm-agent/Directory.Build.props`, `mac-mcp/package.json`, `mac-mcp/package-lock.json`).
- Branch `formatter-0.7.0`; tests after every task: `(cd mac-mcp && npm run typecheck && npm test)`.
- Docs that a task's change makes wrong are fixed in that task (`docs/tools.md`, `docs/api.md`, `skills/bimmer-bridge/SKILL.md`).

## Review Focus

1. **A functional result from another chassis or a partial answer** (sets without `ECU_GROBNAME`, `F_ANZ` missing,
   `F_ORT3_NR` without `F_ART3_NR`). Expected: those sets print as generic `key=value`, nothing dropped. Tests in Tasks 3 and 4.
2. **A real distance whose name contains a number token** (`STAT_KM_STAND_WERT_1`, `F_UW_KM`, `STAT_GESAMTE_KM_WERT_0`). Expected: still converted. Test in Task 1.
3. **`F_ART` not a number** (string or missing). Expected: "status not decoded" with the value as given. Test in Task 3.
4. **Negative-response statuses in a batch and in a fault read** (`ediabas_jobs`, `ediabas_read_faults`). Expected: the same note as for `ediabas_job`. Test in Task 2.
5. **Text budget:** a 44-module `IDENT_FUNKTIONAL` view must fit the tool's text budget without truncation. Test in Task 5 (real recording).

---

### Task 1: Trip counts are not distances (A1)

**Files:**
- Modify: `mac-mcp/src/formatEdiabas.ts` (`isKmName`, ~line 50)
- Test: `mac-mcp/test/formatEdiabas.test.ts` (`describe("isKmName")`, `describe("job results")`)

**Interfaces:**
- Produces: `isKmName(name: string): boolean` (unchanged signature).

- [ ] **Step 1: Write the failing tests**

```ts
it("a count is not a distance: ANZAHL token, or KM right after a number token (trip-length buckets)", () => {
  expect(isKmName("STAT_ANZAHL_FAHRTEN_0_5_KM_1")).toBe(false);
  expect(isKmName("STAT_ANZAHL_FAHRTEN_UEBER_100_KM_0")).toBe(false);
  expect(isKmName("STAT_ANZAHL_FAHRTEN_20_100_KM_3")).toBe(false);
  expect(isKmName("STAT_GESAMTE_KM_WERT_0")).toBe(true);
  expect(isKmName("STAT_KM_STAND_WERT_1")).toBe(true);
  expect(isKmName("F_UW_KM")).toBe(true);
});
```
and in "job results": a `KGM_60` `STATUS_HISTORIENSPEICHER_LESEN_BLOCK_2` set
`{ STAT_ANZAHL_FAHRTEN_0_5_KM_1: 4, STAT_GESAMTE_KM_WERT_1: 343, STAT_GESAMTE_KM_EINH_1: "KM" }` (values from the car, 2026-11-23) gives lines
`  STAT_ANZAHL_FAHRTEN_0_5_KM_1=4` and `  STAT_GESAMTE_KM_WERT_1=213 mi (343 km)`.

- [ ] **Step 2: Run to verify they fail**
Run: `cd mac-mcp && npx vitest run test/formatEdiabas.test.ts -t "count is not a distance"`. Expected: FAIL (first `expect` is `true`).

- [ ] **Step 3: Implement** in `isKmName`: return `false` when the name has an `ANZAHL` token (`/(^|_)ANZAHL(_|$)/i`) or `KM` directly after a
digits-only token (`/(^|_)\d+_KM(_|$)/i`), before the existing checks. Update the doc comment.

- [ ] **Step 4: Run tests.** Run: `cd mac-mcp && npm test`. Expected: PASS.

- [ ] **Step 5: Commit** `git commit -am "Formatter: trip counts are not distances (A1, KGM_60 history block 2 at the car)"`

### Task 2: Vehicle status labels and "not supported" statuses (A2, A3, C1)

**Files:**
- Modify: `mac-mcp/src/formatEdiabas.ts` (`readingText`, `VEHICLE_NOTE`, `formatVehicle`, `formatJobResult`, `formatFaultTable`)
- Modify: `mac-mcp/src/server.ts:389` (the `ediabas_vehicle_status` description: "battery (UTILITY STATUS_UBATT)" → "the cable's supply (UTILITY STATUS_UBATT)")
- Modify: `docs/tools.md:15,53`, `docs/api.md:444`, `skills/bimmer-bridge/SKILL.md:38` (same wording)
- Test: `mac-mcp/test/formatEdiabas.test.ts` (`describe("vehicle status")`, `describe("job results")`, `describe("fault tables")`)

**Interfaces:**
- Produces: `export function jobStatusText(status: string | null | undefined): string` — `"job status X"` or `"job status not reported"`,
  plus `NOT_SUPPORTED_NOTE` appended in brackets when `status` matches `/^ERROR_ECU_.*NOT_SUPPORTED/i`. Used by `formatJobResult`, `readingText`,
  `formatFaultTable` and Task 3.
- `const NOT_SUPPORTED_NOTE = "the module answered that it does not support this job: a negative response from the module, not a bridge, cable or policy fault"`.

- [ ] **Step 1: Update the vehicle-status test to the new text** (replace `t[0]`, `t[2]`, `t[4]`, `t[6]` expectations and the length):

```ts
expect(t[0]).toBe("Vehicle status (result fields as returned):");
expect(t[1]).toBe("Ignition (UTILITY STATUS_ZUENDUNG): job status OKAY; STAT_ZUENDUNG=1");
expect(t[2]).toBe("Cable supply (UTILITY STATUS_UBATT; from the cable, not a battery reading): job status OKAY; STAT_UBATT=12480");
expect(t[4]).toBe("Cable KL30 (TMODE LESE_SPANNUNG_KL30; from the cable, not a battery reading): job status OKAY; SPANNUNG_KL30=12510");
expect(t[6]).toBe(IGNITION_NOTE);
expect(t[7]).toBe(CABLE_NOTE);
expect(t).toHaveLength(8);
```
with the two notes (exact copy):
- `IGNITION_NOTE = "Ignition: with the ignition on at the car (2026-11-23) STAT_ZUENDUNG was 1 and the cable's KL15 E02E0000; with no car connected (desk, 2026-11-22) 0 and 00000000. Ignition off with the cable in the car has not been recorded yet."`
- `CABLE_NOTE = "Battery: STAT_UBATT and the cable's KL30 read the same with and without the car (STAT_UBATT=1, KL30 E02E0000), so they show only that the cable is powered. Only a module reading (a voltage source) shows the car's supply voltage."`

- [ ] **Step 2: Write the "not supported" tests**: `formatJobResult` of a `CAS` `STATUS_KL15_ABSCHALTUNG` result with
`jobStatus: "ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT"` has first line containing
`job status ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT (the module answered that it does not support this job`; an `OKAY` result does not contain
`does not support`; `formatFaultTable` with `jobStatus: "ERROR_ECU_SERVICE_NOT_SUPPORTED"` and no sets contains the note.

- [ ] **Step 3: Run to verify they fail.** Run: `cd mac-mcp && npx vitest run test/formatEdiabas.test.ts`. Expected: FAIL on the new expectations.

- [ ] **Step 4: Implement** `jobStatusText` and use it in the four places; relabel the two cable lines; replace `VEHICLE_NOTE` with the two notes; update the server description and the three docs.

- [ ] **Step 5: Run all Mac tests.** Run: `cd mac-mcp && npm run typecheck && npm test`. Expected: PASS (`tools.test.ts:524` still matches the unchanged ignition line).

- [ ] **Step 6: Commit** `git commit -am "Vehicle status: cable readings labelled, ignition values as seen at the car; note for ECU 'not supported' answers (A2, A3, C1)"`

### Task 3: Whole-car fault view for `FS_LESEN_FUNKTIONAL` (B1)

**Files:**
- Modify: `mac-mcp/src/formatEdiabas.ts` (new section "Whole-car jobs"; dispatch in `formatJobResult`)
- Modify: `docs/tools.md` ("Output"), `skills/bimmer-bridge/SKILL.md` (step 3: what the view shows, `raw: true` for every field)
- Test: `mac-mcp/test/formatEdiabas.test.ts` (new `describe("whole-car jobs")`)

**Interfaces:**
- Consumes: `jobStatusText` (Task 2), `faultCode` (existing: `0xA719` style).
- Produces: `export function formatFunctionalFaults(r: JobResult): string`; `export function storageState(fArt: unknown): string`.
  `formatJobResult(r)` returns `formatFunctionalFaults(r)` when `r.job.toUpperCase() === "FS_LESEN_FUNKTIONAL"` and at least one set has an `F_ANZ` key.

`storageState` (the only algorithm here; the KWP2000 storage-state bits, labelled inferred):
```ts
// number n: (n & 0x60) === 0x60 → "present now"; === 0x20 → "stored, not present"; else "status not decoded"
// then always ` (F_ART 0x${hex2})`; non-number → `status not decoded (F_ART ${valueText(v, 20)})`
```

- [ ] **Step 1: Write the failing tests** with sets taken from the car's result (recording `0031`, 2026-11-23):

```ts
const sets = [
  { ECU_GROBNAME: "ALBV_FA", ECU_ADR: "59", F_ANZ: 1, F_ORT1_NR: 42777, F_ART1_NR: 0x28, JOB_STATUS: "OKAY", _TEL_ANTWORT: "85F1595801A7192810" },
  { ECU_GROBNAME: "ZGM", ECU_ADR: "00", F_ANZ: 2, F_ORT1_NR: 37836, F_ART1_NR: 0x61, F_ORT2_NR: 37810, F_ART2_NR: 0x21, JOB_STATUS: "OKAY" },
  { ECU_GROBNAME: "SINE", ECU_ADR: "50", F_ANZ: 1, F_ORT1_NR: 0x9d12, F_ART1_NR: 0x01, JOB_STATUS: "OKAY" },
  { ECU_GROBNAME: "CAS", ECU_ADR: "40", F_ANZ: 0, JOB_STATUS: "OKAY" },
  { ECU_GROBNAME: "VVT", JOB_STATUS: "ERROR_ECU_SERVICE_NOT_SUPPORTED" },
  { SOMETHING_ELSE: 1 },
];
```
Assertions on `formatJobResult(result("E60","FS_LESEN_FUNKTIONAL", sets))`:
- contains `3 modules with faults (4 codes), 1 without, 1 could not be read.`
- contains `ZGM (address 0x00): 0x93CC present now (F_ART 0x61); 0x93B2 stored, not present (F_ART 0x21)`
- contains `ALBV_FA (address 0x59): 0xA719 stored, not present (F_ART 0x28)`
- contains `SINE (address 0x50): 0x9D12 status not decoded (F_ART 0x01)`
- contains `Without faults: CAS`
- contains `VVT (address unknown): job status ERROR_ECU_SERVICE_NOT_SUPPORTED (the module answered that it does not support this job`
- contains `(set 6) SOMETHING_ELSE=1`
- contains `Present/stored is inferred from the F_ART status byte (KWP2000); ediabas_read_faults on the module gives its own wording. raw: true shows every field.`
- does not contain `_TEL_ANTWORT`.
- `storageState("x")` is `status not decoded (F_ART x)`; `storageState(undefined)` is `status not decoded (F_ART null)`.
- An `FS_LESEN_FUNKTIONAL` result whose sets have no `F_ANZ` still prints the generic `Set 1:` view.

- [ ] **Step 2: Run to verify they fail.** Run: `cd mac-mcp && npx vitest run test/formatEdiabas.test.ts -t "whole-car"`. Expected: FAIL.

- [ ] **Step 3: Implement** `storageState` and `formatFunctionalFaults`. Layout: the same first line as the generic view (`ECU JOB: job status …, variant …, N ms, N result sets`),
the summary line, one line per module with faults (address `0x` + `ECU_ADR` as given, `address unknown` without one; codes `F_ORT1_NR`…`F_ORT<F_ANZ>_NR` with matching `F_ART<n>_NR`), one line per module whose `JOB_STATUS` is not `OKAY`
(via `jobStatusText`), `Without faults: …` (comma list in result order), any other set as `(set n) key=value …`, then the inferred/raw line. Telegram fields
(`_TEL_*`) and the fields consumed above are not repeated.

- [ ] **Step 4: Run tests.** Run: `cd mac-mcp && npm test`. Expected: PASS.

- [ ] **Step 5: Commit** `git commit -am "ediabas_job: whole-car fault view for FS_LESEN_FUNKTIONAL, present/stored inferred from F_ART (B1)"`

### Task 4: Whole-car module list for `IDENT_FUNKTIONAL` (B1)

**Files:**
- Modify: `mac-mcp/src/formatEdiabas.ts` (same section as Task 3)
- Modify: `docs/tools.md`, `skills/bimmer-bridge/SKILL.md` step 3
- Test: `mac-mcp/test/formatEdiabas.test.ts` (`describe("whole-car jobs")`)

**Interfaces:**
- Produces: `export function formatFunctionalIdent(r: JobResult): string`; dispatched from `formatJobResult` when
  `r.job.toUpperCase() === "IDENT_FUNKTIONAL"` and at least one set has `ECU_SGBD`.

- [ ] **Step 1: Write the failing tests** with a set from recording `0029`:
`{ ECU_ADR: "59", ECU_NAME: "Aktive Lehnenbreitenverstellung Fahrer", ECU_SGBD: "ALBV60F", ECU_GRUPPE: "D_ALBVF", ECU_GROBNAME: "ALBV_FA", ID_BMW_NR: "6938911", ID_DATUM: "09.06.2004", JOB_STATUS: "OKAY" }`,
a failed set `{ ECU_GROBNAME: "XYZ", ECU_ADR: "12", JOB_STATUS: "ERROR_ECU_NO_RESPONSE" }`, and `{ OTHER: 2 }`. Assertions:
- contains `1 module answered, 1 did not.`
- contains `ALBV_FA (address 0x59): ALBV60F, group D_ALBVF, Aktive Lehnenbreitenverstellung Fahrer, BMW no. 6938911, date 09.06.2004`
- contains `XYZ (address 0x12): job status ERROR_ECU_NO_RESPONSE`
- contains `(set 3) OTHER=2`
- contains `raw: true shows every field (hardware and software numbers, coding index).`

- [ ] **Step 2: Run to verify they fail.** Expected: FAIL.

- [ ] **Step 3: Implement** `formatFunctionalIdent`: first line as generic; summary; one line per module in result order; other sets generic; closing raw line.

- [ ] **Step 4: Run tests.** Run: `cd mac-mcp && npm test`. Expected: PASS.

- [ ] **Step 5: Commit** `git commit -am "ediabas_job: module list view for IDENT_FUNKTIONAL (B1)"`

### Task 5: Replay check on the real recordings, docs, release 0.7.0

**Files:**
- Modify: `dev/test/mcp-session.test.ts` (new `it`)
- Modify: `VERSION`, `vm-agent/Directory.Build.props`, `mac-mcp/package.json`, `mac-mcp/package-lock.json`
- Modify: `README.md` Status, `docs/history/changes.md` ("What changed in 0.7.0")

- [ ] **Step 1: Write the replay test**: start the replay agent on scenario `battery`, call `ediabas_job` `E60` `IDENT_FUNKTIONAL` and `FS_LESEN_FUNKTIONAL`
through the real MCP server. Assert: the first text contains `43 modules answered` and `PGS (address 0x27): PGS_65_2`; the second contains
`15 modules with faults (24 codes)` and `PGS (address 0x27): 0xA0B7 present now (F_ART 0x60)`; neither contains `… cut at` (the text budget's marker, `textBudget.ts`).
Also `ediabas_jobs` on the 12-item battery batch: contains `STAT_ANZAHL_FAHRTEN_0_5_KM_1=4` and the `CAS` not-supported note.

- [ ] **Step 2: Run.** Run: `cd mac-mcp && npm run test:dev`. Expected: PASS (if the module count differs from 43 because of the unnamed 44th set, fix the summary rule in Task 4, not the test's number).

- [ ] **Step 3: Bump to 0.7.0** in the four version files; add "What changed in 0.7.0" to `changes.md` (A1, A2/A3, C1, B1 in plain words) and update the README Status.

- [ ] **Step 4: Release check.** Run: `./release.sh`. Expected: versions agree, every suite passes, zips and `.skill` files built.

- [ ] **Step 5: Commit** `git commit -am "0.7.0: formatter fixes and whole-car views from the first session at the car"`

- [ ] **Step 6: Hand over to the owner**: update the VM agent ([install.md#updating](../../install.md#updating)) and re-upload the `.skill` files to claude.ai;
  `vm_health` must show 0.7.0 on both sides.
