# dev: tools for deploying and for testing without the car

| File | What |
|---|---|
| `serve.py` | the Mac-side deploy server: serves `vm-setup/*.ps1` and `release/EdiabasAgent.zip` to the VM's address only, takes the agent token into the Keychain with `--accept-token`, prints the hash-checked lines to paste. Contract: [docs/deploy.md](../docs/deploy.md) |
| `test_serve.py` | its tests: `python3 -m unittest dev/test_serve.py` |
| `replay-agent.ts` | a stand-in VM agent that answers only from recorded fixtures, on 127.0.0.1 |
| `scrub-fixtures.ts` | copies a raw recording (`EDIABAS_RECORD_DIR`) into `fixtures/<scenario>/` with identifiers removed |
| `fixtures/` | recorded scenarios (`idle`, `inpa-open`): [fixtures/README.md](fixtures/README.md) |
| `test/` | vitest tests for the replay agent and scrubber, run from `mac-mcp` |

```sh
python3 dev/serve.py [--accept-token] [--client <VM IPv4>] [--bind <Mac IPv4>] [--port 8000]
EDIABAS_AGENT_TOKEN=<any> node dev/replay-agent.ts --scenario idle [--port 8765] [--fixtures <dir>]
node dev/scrub-fixtures.ts <raw-folder> <scenario> [--fixtures <dir>] [--dry-run]
(cd mac-mcp && npm run typecheck:dev && npm run test:dev)
```

`serve.py` needs only Python 3's standard library. It refuses to start without the VM running
(no UTM bridge address) or without a DHCP lease for the VM's MAC (`EDIABAS_VM_MAC`, or pass
`--client`). The TypeScript tools have no dependencies and need Node 24+ (they run through type
stripping). More: [docs/development.md](../docs/development.md).
