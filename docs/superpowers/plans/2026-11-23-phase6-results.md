# Phase 6 results: fixtures, skill references, docs and config — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the 2026-11-23 session at the car into committed fixtures, filled skill
references and updated docs, and put the VM and Mac config back to normal.

**Architecture:** No code changes. Recordings → `dev/scrub-fixtures.ts` → `dev/fixtures/<scenario>/`;
skill reference files filled from the same recordings in the same commit; docs follow.

**Tech Stack:** Markdown, `dev/scrub-fixtures.ts` (Node 24+), the replay agent, vitest dev tests.

**Spec:** `docs/superpowers/specs/2026-11-23-phase6-feedback-response.md` (read it first: the
"What the session established" table is the source for every value below).

## Global Constraints

- Nothing in this plan runs against the car. EDIABAS calls only at the desk with the cable **not in the car**,
  and only `ediabas_list_ecus` / `ediabas_describe` (PLAN.md §12).
- Raw recordings stay in `~/ediabas-recordings/car-20261123` (outside the repo). Only scrubbed,
  reviewed copies are committed. Nothing in `dev/fixtures/` is written by hand.
- Skill references hold only what a recording shows; each new entry names its date and evidence
  (recording sequence number or tool + result).
- Changes to the VM's `config.json` and to `reviewedJobs` are the owner's: each such step waits for their go-ahead.
- Work on a branch `phase6-results`; one PR. Tests after every task:
  `(cd mac-mcp && npm run typecheck:dev && npm run test:dev)` and `skills/install.sh --no-link`.

## Review Focus

1. **Identifiers in fixtures beyond what the scrubber knows** (the car's registration, the owner's name, serial numbers in
   `IDENT_FUNKTIONAL` sets with unusual field names). Expected: none in any committed file. Check in Task 2, Step 4.
2. **An eval run whose calls miss the recordings** (different `filter`, `includeInfoMemory`). Expected: the
   replay agent answers `no_recording` and the gap is written down, never filled by hand. Task 5.
3. **The skill claiming more than one session shows** (e.g. "ignition off reads 0"). Expected: every
   claim names 2026-11-23 and what was observed; ignition-off at the car is stated as not recorded. Tasks 2–3.
4. **A stale skill copy on claude.ai** after the repo changes. Expected: the rebuilt `.skill` is uploaded, and
   the README says so. Task 5.

---

### Task 1: Config back to normal, and the voltage source

**Files:**
- Modify (VM, not in repo): `C:\ProgramData\EdiabasBridge\config.json`
- Modify (Mac, git-ignored): `config.local.json`

- [ ] **Step 1: Ask the owner for the go-ahead** to (a) set `ediabasIdleReleaseSeconds` to 20 and restart
  Windows in the VM, (b) remove the three `CAS` approvals that the car answered "not supported", (c) later, after the evals (Task 5, Step 6), add the `KOMB60` voltage source.
  Also ask whether to remove `MPM_60:STATUS_AD_WERTE_LESEN@aea6a0af4d73`: `MPM_60` did not answer (`IFH-0009`) and is absent from `IDENT_FUNKTIONAL`.

- [ ] **Step 2: Edit the VM config over `ssh ediabas-vm`** (admin PowerShell; never used for EDIABAS or BMW tools).
  Set `"ediabasIdleReleaseSeconds": 20`; remove from `reviewedJobs` exactly:
  `CAS:STATUS_WAKEUP_HISTORY@fbc0eac3b28d`, `CAS:STATUS_KLEMMENVERHINDERER@25403a239cb1`, `CAS:STATUS_KL15_ABSCHALTUNG@7f401ea917cd`
  (and the `MPM_60` line if the owner said so). Keep the file's JSON shape; back it up first as `config.json.bak-20261123`.

- [ ] **Step 3: Restart Windows in the VM** (owner does it, or with their OK `Restart-Computer` over ssh).

- [ ] **Step 4: Verify.** Restart Claude (Code and Desktop, Cmd+Q). `vm_health` must show `Approved jobs: 2 pinned`
  (or 1 without `MPM_60`), the three `CAS` lines gone. `ssh ediabas-vm` `Get-Content` shows idle 20.
  `config.local.json` still lists only `NVE_65` (the evals replay a vehicle-status batch recorded with that one source). `claude mcp get bimmer-bridge` and Desktop's config both show `mac-mcp/dist/index.js`, no `EDIABAS_RECORD_DIR`.
  No commit (nothing in the repo changed).

### Task 2: Fixtures `nve-faults` and `battery`, with the ECU map and job cookbook

**Files:**
- Create: `dev/fixtures/nve-faults/*` (scrubbed), `dev/fixtures/nve-faults/scenario.json`
- Create: `dev/fixtures/battery/*` (scrubbed)
- Modify: `dev/fixtures/README.md` (scenario list and table)
- Modify: `skills/bimmer-bridge/references/ecu-map.md`
- Modify: `skills/bimmer-bridge/references/job-cookbook.md`

- [ ] **Step 1: Dry run the scrubber for both scenarios.**
  Run: `node dev/scrub-fixtures.ts ~/ediabas-recordings/car-20261123 nve-faults --dry-run` and the same for `battery`.
  Expected: a list of replacements (user/host in `/health`, any serial fields); no error.

- [ ] **Step 2: Scrub for real**, both scenario names.

- [ ] **Step 3: Prune each scenario to what it needs** (sequence numbers from the raw folder):
  - `nve-faults`: one `GET-health` (keep `0001`), `0014` (read_faults `NVE_65` with info memory), `0020` (`NVE_65` fault memory only), `0022` (vehicle status). Delete the rest.
    Add `scenario.json`: `{ "description": "Night vision at the car, 2026-11-23: NVE_65 fault and info memory empty; vehicle status with ignition on. List and describe answers come from the desk (needs-review).", "base": "needs-review" }`.
  - `battery`: one `GET-health`, `0003` `0005` `0007` (ecus `D_`, `60`, `ulf`), `0009` `0011` (describe `KGM_60`), `0016` (12-item battery batch), `0018` (read_faults 5 ECUs), `0022`, `0024` (`D_MOTOR` `IDENT`), `0027` (`D_MOTOR` `FS_LESEN`), `0029` `0031` (`E60` functional jobs). Delete the rest.

- [ ] **Step 4: Review every kept file by hand.** `grep -rilE -f <local file of the owner's identifiers: registration, name> dev/fixtures/nve-faults dev/fixtures/battery` must print nothing.
  Read each `IDENT_FUNKTIONAL` set for serial-like fields the scrubber did not name; if one is found, stop and ask
  (the fix is a scrubber rule plus a test, not a hand edit).

- [ ] **Step 5: Run the dev tests.**
  Run: `(cd mac-mcp && npm run test:dev)`. Expected: PASS, including `fixtures-clean.test.ts` over the new folders.

- [ ] **Step 6: Fill `ecu-map.md`.** Replace the "Status: no SGBD has yet answered" paragraph with: modules confirmed from the car on 2026-11-23
  by `E60` `IDENT_FUNKTIONAL` (recording `0029`). Add one entries-table row per module in that result (`ECU_GROBNAME`, `ECU_SGBD`, `ECU_GRUPPE`,
  address, confirmed `2026-11-23, E60 IDENT_FUNKTIONAL`), plus the resolved groups: `D_MOTOR` → `N62_TUE` (`0024`), `D_PGS` → `PGS_65_2`,
  `D_KBM` → `KBM_60` (`0018`). Record: `d_0012` gives `IFH-0009` (`0018`); `MPM_60` gives `IFH-0009` and is not in `IDENT_FUNKTIONAL` (`0016`, `0029`);
  SINE (0x50) answers `FS_LESEN_FUNKTIONAL` but not `IDENT_FUNKTIONAL`; `VVT`/`VVT2` answer `FS_LESEN_FUNKTIONAL` with `ERROR_ECU_SERVICE_NOT_SUPPORTED` (`0031`).
  Rewrite "Which engine file ... is not known" as: the engine file is `N62_TUE`; `N62_TUE2` and `ME9N62_2` are not this car's.

- [ ] **Step 7: Fill `job-cookbook.md`.** Move to "Entries (jobs run on the car)", in the file's entry format, with the agent's `durationMs`:
  `E60` `IDENT_FUNKTIONAL` (3,925 ms, 44 sets) and `FS_LESEN_FUNKTIONAL` (3,964 ms, 45 sets); `D_MOTOR` `IDENT` (253 ms) and `FS_LESEN` via `ediabas_read_faults`;
  `KGM_60` `STATUS_HISTORIENSPEICHER_LESEN_BLOCK_1/2/3` with their result names from `0016` (`STAT_VERURSACHER_n` and `STAT_WECKER_SG_ID_n` are module
  addresses: 0x27 = PGS by `IDENT_FUNKTIONAL`); `IHKA60` `STATUS_VERBRAUCHERREDUZIERUNG`; `KOMB60` `STATUS_KL30_H_ZAEHLER`; `CAS` `STATUS_WUP`, `STATUS_PA_WUP`;
  `NVE_65` `STATUS_BORDNETZSPANNUNG` (integer volts only), `FS_LESEN`, `IS_LESEN`.
  Under `UTILITY`/`TMODE`, replace "comes from Phase 6" with the observed values: ignition on at the car → `STAT_ZUENDUNG=1`, cable KL15 `E02E0000`;
  no car → 0, `00000000`; ignition off with the cable in the car **not recorded yet**; `STAT_UBATT`/KL30 identical with and without the car.
  Under `CAS`: the three jobs answer `ERROR_ECU_SUBFUNCTION_NOT_SUPPORTED__INVALID_FORMAT` on this CAS; approvals removed (if Task 1 did).
  Add a "For the next session" list: `ediabas_describe` `N62_TUE` `FS_LESEN_DETAIL` (try at the desk first; its argument is the fault code) for the `2DED` environment data;
  `KOMB60` `STATUS_KLEMMENSPANNUNG` resolution; `D_SINE` variant; ignition-off vehicle status with the cable in the car.

- [ ] **Step 8: Update `dev/fixtures/README.md`**: the intro lists `nve-faults` and `battery` as recorded at the car on 2026-11-23; `nve-faults` is an overlay on `needs-review`. Add: their `ediabas_vehicle_status` recordings were made with `voltageSources` = `NVE_65` `STATUS_BORDNETZSPANNUNG` only, so an eval run needs exactly that in `config.local.json` (another list sends another batch body, which the replay agent answers `no_recording`).

- [ ] **Step 9: Verify and commit.**
  Run: `(cd mac-mcp && npm run test:dev && npm run typecheck:dev) && skills/install.sh --no-link`. Expected: PASS; skill files build.
  ```bash
  git add dev/fixtures skills/bimmer-bridge/references/ecu-map.md skills/bimmer-bridge/references/job-cookbook.md
  git commit -m "Phase 6: fixtures nve-faults and battery from the car (2026-11-23); ECU map and job cookbook filled"
  ```

### Task 3: Skill routine and playbooks

**Files:**
- Modify: `skills/bimmer-bridge/SKILL.md` (Session routine steps 1, 3, 5; new notes after "The first call after a pause is slow")
- Modify: `skills/bimmer-bridge/references/playbooks/battery-drain.md`
- Modify: `skills/bimmer-bridge/references/playbooks/full-fault-sweep.md`
- Modify: `skills/bimmer-bridge/references/errors.md`

- [ ] **Step 1: `SKILL.md` step 3.** Drop "*Not yet run at the car.*" and "how long these take on a car is not yet measured";
  say: run first on an E60/E61; about 4 s each on the E61 (2026-11-23); keep `timeoutSeconds: 60` and one job per call.

- [ ] **Step 2: `SKILL.md` vehicle status.** In the tools table and step 5: the ignition reading and the cable's KL15 followed the ignition at the car;
  `STAT_UBATT` and KL30 come from the cable; only a voltage source shows the car's supply voltage.

- [ ] **Step 3: `SKILL.md` two new notes.**
  "**If the bridge tools disappear mid-session**": do not retry anything; ask the user to switch the ignition off while the link is down; when the tools
  return, `vm_health` "Last cable use" shows whether the last job reached the car (2026-11-23: a link drop over mobile data; the agent was unaffected).
  "**Long results**": pass `results` (semicolon list of result names, from `ediabas_describe`) to trim history jobs such as the `KGM_60` blocks;
  a batch's items share one 60 s limit (the 12-item battery batch took 19 s on 2026-11-23).

- [ ] **Step 4: `battery-drain.md`.** Replace "Status: no battery-related job has been run" with what ran on 2026-11-23. New order under "With the ignition on":
  1 `E60` `IDENT_FUNKTIONAL`; 2 `FS_LESEN_FUNKTIONAL`; 3 `KGM_60` history blocks 1–3 in one `ediabas_jobs` (block 1 names the module that kept the car awake,
  block 3 each waking module by address); 4 `ediabas_read_faults` on the modules the history and the functional read name; 5 fault detail (`FS_LESEN_DETAIL`) for a
  quiescent-current code such as `2DED`, once described. Remove the unverified engine candidates paragraph; name `N62_TUE` (via `D_MOTOR`) as this car's engine file.
  Mark `MPM_60` "not on this car's bus (2026-11-23)" and the three `CAS` jobs "not supported by this car's CAS". Keep: no bridge read measures the current; no cause claimed from readings alone.

- [ ] **Step 5: `full-fault-sweep.md`.** Replace "procedure only" with: run on the E61 on 2026-11-23 (both functional jobs, ~4 s each).

- [ ] **Step 6: `errors.md`.** Add an entry for a job status `ERROR_ECU_..._NOT_SUPPORTED...`: the module answered and refused the job (a negative response);
  not a bridge, cable or policy fault; do not retry; record it in the cookbook as "not supported on this module".

- [ ] **Step 7: Verify and commit.**
  Run: `skills/install.sh --no-link && (cd mac-mcp && npm run test:dev)` (includes `doc-links.test.ts`). Expected: PASS.
  ```bash
  git add skills/bimmer-bridge
  git commit -m "bimmer-bridge skill: routine and playbooks from the first session at the car"
  ```

### Task 4: Docs

**Files:**
- Modify: `docs/at-the-car.md`
- Modify: `docs/development.md` (recording section)
- Modify: `docs/history/changes.md`
- Modify: `README.md` (between `<!-- STATUS -->` markers)

- [ ] **Step 1: `at-the-car.md`.** Replace "Nothing added from 0.3.0 on has been run at the car yet" with a dated summary of what ran on 2026-11-23.
  In "Before going to the car": the recording registration must be made in **each app used at the car** — `claude mcp` for Claude Code, and
  `~/Library/Application Support/Claude/claude_desktop_config.json` (`mcpServers.bimmer-bridge`, with `"env": {"EDIABAS_RECORD_DIR": ...}`) for Claude Desktop,
  then Cmd+Q and reopen; restore both afterwards. Add: Claude needs a steady internet link (phone hotspot close to the Mac or Wi-Fi; mobile data dropped
  the Desktop link for 3 minutes on 2026-11-23); if the tools disappear, ignition off until they return. Add: the Mac's per-call log is
  `~/Library/Logs/ediabas-bridge/audit.log`. Under "Ignition on": the functional jobs took ~4 s each; replace "Its field names are not confirmed yet" with the observed values.

- [ ] **Step 2: `development.md`**: the same Claude Desktop registration for the recording server, next to the `claude mcp` commands.

- [ ] **Step 3: `changes.md`**: a section "Phase 6 at the car (2026-11-23)" with the acceptance result from the spec (pass / pass / read passed, pair absent / fail, about 18 min / recorded).

- [ ] **Step 4: `README.md` Status**: first session at the car done on 2026-11-23 with 0.6.2; link `changes.md`; open items: evals 01, 03, 06 (Task 5), the formatter plan.

- [ ] **Step 5: Verify and commit.**
  Run: `(cd mac-mcp && npm run test:dev)`. Expected: PASS (`doc-links.test.ts`).
  ```bash
  git add docs README.md
  git commit -m "Docs: Phase 6 results; Claude Desktop recording registration; connectivity at the car"
  ```

### Task 5: Evals 01, 03 and 06 against the new fixtures

**Files:**
- Modify: `skills/evals/01-read-night-vision-faults.md`, `03-clear-faults.md`, `06-battery-drain.md` (drop "until then this case cannot be run")
- Create: `docs/history/evals-2026-11-23.md` (results)
- Possibly create: desk recordings added to `dev/fixtures/needs-review/` or `battery/` (Step 3)

- [ ] **Step 1: Run each eval as its file says** (fresh Claude Code session, replay registration from `dev/fixtures/README.md`), with and without the skills.
  Scenario `nve-faults` for 01 and 03, `battery` for 06.

- [ ] **Step 2: For every `no_recording` answer, write down the exact request** (path, query, body) in the results file.

- [ ] **Step 3: Fill desk-safe gaps only.** A missed `ediabas_list_ecus` or `ediabas_describe` that does not need the module can be recorded at the desk
  (owner confirms the cable is **not in the car**; recording server as in `docs/development.md`), scrubbed into the scenario, and the eval re-run.
  A missed car read is listed under "for the next session" in the results file; nothing is written by hand.

- [ ] **Step 4: Record pass / fail per eval** in `docs/history/evals-2026-11-23.md`, with the reason, and the with/without-skill comparison.
  Also grade the live Claude Desktop run of 01 and 06 at the car (the session doc the owner pasted) against the same rubric; note that it named `NVE_65`
  without an `ediabas_list_ecus` result in that session (eval 01 "Must not" — record as found).

- [ ] **Step 5: Rebuild and hand over the skill files.** Run `skills/install.sh --no-link`; tell the owner to upload `skills/dist/bimmer-bridge.skill`
  (and `bmw-standard-tools.skill` if changed) to claude.ai so Claude Desktop uses the new references.

- [ ] **Step 6: Add the `KOMB60` voltage source on the Mac** (owner's go-ahead from Task 1), now that the evals have run. `config.local.json` becomes:
  ```json
  { "voltageSources": [
      { "ecu": "KOMB60", "job": "STATUS_KLEMMENSPANNUNG" },
      { "ecu": "NVE_65", "job": "STATUS_BORDNETZSPANNUNG" } ] }
  ```
  Restart Claude. Its resolution is confirmed at the next session (cookbook "For the next session"). Put it back to `NVE_65` only for any later eval run.

- [ ] **Step 7: Commit.**
  ```bash
  git add skills/evals docs/history/evals-2026-11-23.md dev/fixtures
  git commit -m "Evals 01, 03, 06 run against the Phase 6 fixtures"
  ```
