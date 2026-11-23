# Replay fixtures

Recorded agent exchanges that `dev/replay-agent.ts` serves in place of the VM agent, so the MCP
server and the skills can be exercised with no VM and no car. **Every file here comes from a
real session, scrubbed and reviewed.** Nothing is written by hand: no invented EDIABAS
responses, no edited values. If a scenario needs an answer nobody has recorded yet, the
scenario waits for a session that records it.

Five scenarios exist. Recorded at the desk: `idle` and `inpa-open` (an overlay on `idle`,
which also serves the screenshot of the INPA loader menu) on 2026-11-21, and `needs-review`
(`NVE_65` `STATUS_PAIRING`, refused with `job_needs_review`; cable in the VM, not in the car)
on 2026-11-22. Recorded at the car on 2026-11-23: `nve-faults` (an overlay on `needs-review`,
which supplies the list and describe answers) and `battery`. Their `ediabas_vehicle_status`
recordings were made with `voltageSources` = `NVE_65` `STATUS_BORDNETZSPANNUNG` only, so an
eval run needs exactly that in `config.local.json`: another list sends another batch body,
which the replay agent answers `no_recording`.

## Layout

One folder per scenario, named with letters, digits, `-` and `_`:

```
dev/fixtures/<scenario>/
  <time>-<seq>-<METHOD>-<path>[-<ECU>-<JOB>].json   one recorded exchange (dev/recorder.ts format)
  <same base>.jpg | .png                            a screenshot, referenced by `imageFile`
  scenario.json                                     optional: description, base, routes
```

`scenario.json` (all fields optional):

```json
{
  "description": "INPA open: /health lists INPALOAD; every EDIABAS call is refused.",
  "base": "nve-faults",
  "routes": [ { "method": "*", "pathPrefix": "/ediabas/", "recording": "<a recording in this folder>.json" } ]
}
```

- `base` makes the scenario an **overlay**: anything it does not answer itself is answered by
  the base scenario (and its base, and so on), so the same recordings are not copied.
- `routes` serve one recorded response for every request matching `method` (`GET`, `POST` or
  `*`) and `path` (exact) or `pathPrefix`. The response must be a real recording in the same
  folder, for example one real `cable_in_use` answer recorded with INPA open, served for every
  `/ediabas/*` call.

How a request is answered, layer by layer from the scenario named on the command line down
through its bases:

1. a recording in that folder with the same method, path, query (any order) and JSON body
   (keys in any order; arrays in order). If several match, the one recorded last;
2. else the first of that folder's `routes` that matches;
3. else the next layer down.

Every scenario (or its base) needs a `GET /health` recording: the MCP server checks `/health`
before its first call, even with `EDIABAS_AGENT_URL` set.

Nothing matched: `404` with code `no_recording` and a message naming the request. The replay
agent never makes up an answer. Names are matched as sent (`NVE_65` and `nve_65` differ, as the
MCP server sends what Claude passed).

## Scenarios the evals use (`skills/evals/`)

| Scenario | Record it with |
|---|---|
| `idle` | the agent up, no BMW tool open: `vm_health`, `vm_list_windows` |
| `nve-faults` | ignition on, no BMW tool open: `vm_health`, `ediabas_list_ecus`, `ediabas_describe` on the night-vision SGBD, `ediabas_vehicle_status`, `ediabas_read_faults` |
| `inpa-open` | overlay on `idle` or `nve-faults`: `vm_health` and one `ediabas_*` call with INPA open (the agent answers `cable_in_use`), plus a route sending that answer for `/ediabas/` (eval 02); `vm_list_windows` and `vm_screenshot` of the INPA window (eval 07) |
| `needs-review` | at the desk: `ediabas_describe` of an SGBD with a `STATUS_` job the agent marks `needs_review`, and `ediabas_job` on it (the agent answers `job_needs_review` before the job is sent) |
| `battery` | the engine group and a few status jobs: `ediabas_list_ecus`, `ediabas_describe`, `ediabas_vehicle_status`, `ediabas_read_faults`, `ediabas_jobs` |
| `unreachable` | no folder: start no replay agent, point `EDIABAS_AGENT_URL` at a closed port |

`no-car` is used by no eval: the vehicle-status batch from the desk rehearsal of 2026-11-22 (cable
in the VM, not in the car), the evidence for `job-cookbook.md`'s note that `STAT_UBATT` and the
cable's KL30 do not come from the car.

## Recording a scenario

1. Pick a raw folder **outside the repo** (it holds real vehicle data and is never committed),
   for example `~/ediabas-recordings/nve-faults`. One folder may serve several scenarios: at
   the car, record the whole session into one folder (changing it means restarting Claude),
   then run step 3 once per scenario name and prune each copy in step 4.
2. Run the recording server `dev/record-server.ts` instead of the production MCP server, with
   `EDIABAS_RECORD_DIR` set to it (the command is in
   [docs/development.md](../../docs/development.md#recording-and-scrubbing)), then use the
   tools as the scenario says. Every agent
   exchange except the `/hello` probe lands in the folder as one JSON file; screenshots as
   a `.jpg`/`.png` beside it. The token is never recorded, and neither is the `/hello` proof:
   the replay agent computes it from its own token.
3. Scrub into the fixtures folder (Node 24+, which runs the `.ts` file directly):

   ```sh
   node dev/scrub-fixtures.ts ~/ediabas-recordings/nve-faults nve-faults --dry-run   # see what it would replace
   node dev/scrub-fixtures.ts ~/ediabas-recordings/nve-faults nve-faults
   ```

   It replaces anything shaped like a 17-character VIN; a BMW VIN (`WB` and 15 more) in any
   case and whatever text touches it; in hex binary results, any 17-character run of the VIN
   alphabet with a letter and a digit; and the same inside percent-encoded strings (the window
   title header), checked decoded. Also the values of fields whose names say chassis number, VIN or serial number (`FG_NR`,
   `FAHRGESTELL*`, `VIN`, `SERIENNUMMER`, `SERIAL*`, `CHASSIS*`, ...), the user and host name
   in `/health`, and the folder name after `\Users\` in Windows paths. Placeholders keep the
   shape (`XXXXX00000XXXX000`). It prints every replacement (file, field, placeholder; never
   the original), refuses to overwrite an existing fixture, and writes nothing if any file
   is broken.
4. **Review** before committing: read the printed list and `git diff` every file. The rules
   catch the usual identifiers, not every one: look for anything else that identifies the car
   or a person (registration, a name, a VIN in a log text or a field with an unusual name).
   **Screenshots are copied unchanged**: open each one and drop it if it shows a VIN or other
   identifier. Delete exchanges the scenario does not need (duplicate probes, failed tries).
5. Add `scenario.json` if the scenario is an overlay or needs a route. Start the replay agent
   on it (below) and run the eval once. Commit the fixtures in the same commit as any skill
   reference file filled from the same session.

## Running the replay agent

```sh
EDIABAS_AGENT_TOKEN=<any token> node dev/replay-agent.ts --scenario nve-faults   # 127.0.0.1:8765
```

Then start the MCP server with `EDIABAS_AGENT_URL=http://127.0.0.1:8765` and the same token.
`--port` picks another port, `--fixtures <dir>` another fixtures folder, `--token` passes the
token as a flag instead (visible in `ps`). It binds to 127.0.0.1 only and serves GET and POST.

Any token works. With `EDIABAS_AGENT_URL` set, the MCP server sends `EDIABAS_AGENT_TOKEN` and
never reads the Keychain, so the real VM agent's token never reaches the replay agent (or any
other address set by hand). Without `EDIABAS_AGENT_TOKEN` it refuses with `no_token`. Example
registration for an eval session:

```sh
claude mcp add bimmer-bridge-replay \
  -e EDIABAS_AGENT_URL=http://127.0.0.1:8765 -e EDIABAS_AGENT_TOKEN=replay-dev-token \
  -- <node> <repo>/mac-mcp/dist/index.js
EDIABAS_AGENT_TOKEN=replay-dev-token node dev/replay-agent.ts --scenario nve-faults
```

## Tests

```sh
cd mac-mcp && npm run test:dev && npm run typecheck:dev
```

One test runs every committed recording under `dev/fixtures/` through the scrubber and fails if
it would replace anything.

They build synthetic recordings in temporary folders only, and include a session of the real
MCP server (from `mac-mcp/src`) answering tool calls from the replay agent.
