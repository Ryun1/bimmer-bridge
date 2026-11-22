# ediabas-bridge - remove the VM agent.
#
# Run in an elevated Windows PowerShell 5.1 window inside the VM: paste the Uninstall line that
# dev/serve.py prints (it checks this file's SHA-256 before running it).
#
# Stops and removes the scheduled task and the firewall rule and removes the program files from
# C:\ProgramData\EdiabasBridge, keeping config.json (with the hand-approved reviewedJobs),
# token.txt and logs\ for a later reinstall. To remove those too, put $Purge=$true; at the start
# of that line. ($KeepData is no longer needed: keeping them is the default.)
# Restores nothing else: the sleep setting changed by install.ps1 is left as it is.
# This file must stay ASCII-only and Windows PowerShell 5.1 compatible (it runs via iex).

& {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest

    $Root = 'C:\ProgramData\EdiabasBridge'
    $TaskName = 'ediabas-bridge agent'
    $RuleName = 'ediabas-bridge agent'
    $Keep = @('config.json', 'token.txt', 'logs')

    # An option from the pasted line: read once, then removed from the window's global scope,
    # so a later paste in the same window does not inherit it.
    function Get-OneShotVar([string]$Name) {
        $value = Get-Variable -Name $Name -ValueOnly -ErrorAction SilentlyContinue
        Remove-Variable -Name $Name -Scope Global -ErrorAction SilentlyContinue
        return $value
    }

    function Write-Step([string]$Text) {
        Write-Host ('==> ' + $Text) -ForegroundColor Cyan
    }

    function Write-Warn([string]$Text) {
        Write-Host ('WARNING: ' + $Text) -ForegroundColor Yellow
    }

    function Assert-Elevated {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = New-Object Security.Principal.WindowsPrincipal($id)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'This script must run in an elevated PowerShell. Right-click Windows PowerShell, choose "Run as administrator", and paste the command again.'
        }
    }

    function Assert-WindowsPowerShell {
        # Built and tested for Windows PowerShell 5.1 only (install.ps1 needs .NET Framework ACL APIs).
        if ($PSVersionTable.PSEdition -ne 'Desktop') {
            throw 'This script must run in "Windows PowerShell" (5.1), not PowerShell 7. Open "Windows PowerShell" as administrator and paste the command again. Nothing was changed.'
        }
    }

    try {
        # Options first, so they are cleared even when a check below stops the script.
        $purge = ((Get-OneShotVar 'Purge') -eq $true)
        $keepDataSet = ($null -ne (Get-OneShotVar 'KeepData'))
        Assert-Elevated
        Assert-WindowsPowerShell
        if ($keepDataSet) {
            Write-Host '    $KeepData is ignored: config.json, token.txt and logs\ are kept unless $Purge=$true is set.'
        }

        # ---- task ----
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Write-Step ('Stopping and removing scheduled task "' + $TaskName + '"')
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        } else {
            Write-Step ('Scheduled task "' + $TaskName + '" not found')
        }

        # ---- process ----
        Get-Process -Name 'EdiabasAgent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20; $i++) {
            if ($null -eq (Get-Process -Name 'EdiabasAgent' -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($null -ne (Get-Process -Name 'EdiabasAgent' -ErrorAction SilentlyContinue)) {
            Write-Warn 'EdiabasAgent.exe is still running; its file may not be removable.'
        }

        # ---- firewall ----
        $rules = @(Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)
        if ($rules.Count -gt 0) {
            Write-Step ('Removing firewall rule "' + $RuleName + '"')
            $rules | Remove-NetFirewallRule
        } else {
            Write-Step ('Firewall rule "' + $RuleName + '" not found')
        }

        # ---- files ----
        if (Test-Path -LiteralPath $Root) {
            if ($purge) {
                Write-Step ('Removing ' + $Root + ' with config.json, token.txt and logs\ ($Purge)')
                Remove-Item -LiteralPath $Root -Recurse -Force
            } else {
                Write-Step ('Removing ' + $Root + ' except config.json, token.txt and logs\')
                Get-ChildItem -LiteralPath $Root -Force | Where-Object { $Keep -notcontains $_.Name } |
                    Remove-Item -Recurse -Force
                Write-Host '    Kept for a later reinstall. To remove them too: the Uninstall line with $Purge=$true; at its start.'
            }
        } else {
            Write-Step ($Root + ' not found')
        }

        Write-Host '    The sleep setting (standby-timeout-ac 0) set by install.ps1 was left unchanged.'
        Write-Host '    To restore it, for example: powercfg /change standby-timeout-ac 30'
        Write-Step 'Done.'
    } catch {
        Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    }
}
