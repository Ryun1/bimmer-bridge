# vm-setup: scripts run in the VM

PowerShell scripts that Ryan runs in the Windows VM by pasting one line that `dev/serve.py`
prints on the Mac. Each line downloads its script, checks its SHA-256 against the value printed
on the Mac, and only then runs it. How the lines and the server work:
[docs/deploy.md](../docs/deploy.md).

| Script | Does | Window |
|---|---|---|
| `install.ps1` | installs or upgrades the agent in `C:\ProgramData\EdiabasBridge`: exe, `config.json` and `token.txt` (both kept if present), firewall rule and scheduled task `ediabas-bridge agent`, sleep on AC off; sends the token to the Mac | Windows PowerShell 5.1, as administrator |
| `update.ps1` | replaces the exe only and restarts the scheduled task | Windows PowerShell 5.1, as administrator |
| `uninstall.ps1` | removes the task, the firewall rule and the folder (`$KeepData=$true; ` keeps config, token and logs) | Windows PowerShell 5.1, as administrator |
| `discover.ps1` | read-only report of the VM (used once, for Phase 0); uploads it into `discovery/` on the Mac | Windows PowerShell 5.1 |

```sh
./release.sh                            # on the Mac: build the zip the lines refer to
python3 dev/serve.py --accept-token     # first install (takes the token); plain for update/uninstall
```

Then paste the matching line in the VM. Step by step, with what success looks like:
[Setup from scratch](../README.md#3-install-the-agent-in-the-vm), [Updating](../README.md#updating),
[Uninstalling](../README.md#uninstalling).

Rules for editing the scripts: they must stay ASCII-only and Windows PowerShell 5.1 compatible,
because they run with `iex`; and `serve.py` must be restarted after any change, since the
hashes it prints are those of the files when it started.
