# vm-setup: scripts run in the VM

PowerShell scripts that the user runs in the Windows VM by pasting one line that `dev/serve.py`
prints on the Mac. Each line downloads its script, checks its SHA-256 against the value printed
on the Mac, and only then runs it. How the lines and the server work:
[docs/deploy.md](../docs/deploy.md).

| Script | Does | Window |
|---|---|---|
| `install.ps1` | installs or upgrades the agent in `C:\ProgramData\EdiabasBridge`: exe, `config.json` and `token.txt` (both kept if present), firewall rule and scheduled task `ediabas-bridge agent`, sleep on AC off; sends the token to the Mac on a first install or with `$SendToken=$true` (never prints it) | Windows PowerShell 5.1, as administrator |
| `update.ps1` | replaces the exe only and restarts the scheduled task | Windows PowerShell 5.1, as administrator |
| `uninstall.ps1` | removes the task, the firewall rule and the program files; keeps `config.json`, `token.txt` and `logs\` by default (`$Purge=$true; ` at the start of the line removes them too; `$KeepData` is ignored) | Windows PowerShell 5.1, as administrator |
| `discover.ps1` | read-only report of the VM (used once, for Phase 0); uploads it into `discovery/` on the Mac | Windows PowerShell 5.1 |

```sh
./release.sh                            # on the Mac: build the zip the lines refer to
python3 dev/serve.py --accept-token     # first install, or to send the token again; plain for update/uninstall
```

Then paste the matching line in the VM. If the Mac does not answer, the line prints
`download failed: the Mac did not serve <script> (is serve.py running?)`. Install, Update and
Uninstall first remove any `EdiabasBridge-staging-*` folder an earlier, cut-short run left
behind. Step by step, with what success looks like:
[Setup from scratch](../README.md#3-install-the-agent-in-the-vm), [Updating](../README.md#updating),
[Uninstalling](../README.md#uninstalling).

Rules for editing the scripts: they must stay ASCII-only and Windows PowerShell 5.1 compatible,
because they run with `iex`; and `serve.py` must be restarted after any change, since the
hashes it prints are those of the files when it started.
