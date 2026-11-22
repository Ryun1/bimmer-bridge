# ediabas-bridge - replace the VM agent's exe with a new build.
#
# Run in an elevated Windows PowerShell 5.1 window inside the VM: paste the Update line that
# dev/serve.py prints. That line downloads this file, checks its SHA-256 against the value in
# the line, and only then runs it (with iex), with $sha (SHA-256 of EdiabasAgent.zip) set.
# Optional: $base (default http://<IPv4 default gateway>:8000).
#
# Verifies the hash, stops the task and the agent, replaces EdiabasAgent.exe, starts the task
# and prints the new version. Touches nothing else (config, token, ACLs, firewall, task settings).
# This file must stay ASCII-only and Windows PowerShell 5.1 compatible (it runs via iex).

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    Set-StrictMode -Version Latest

    $Root = 'C:\ProgramData\EdiabasBridge'
    $ExePath = Join-Path $Root 'EdiabasAgent.exe'
    $TaskName = 'ediabas-bridge agent'

    function Get-OptionalVar([string]$Name) {
        return Get-Variable -Name $Name -ValueOnly -ErrorAction SilentlyContinue
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

    function Get-DefaultGateway {
        $routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.NextHop -ne '0.0.0.0' } |
            Sort-Object -Property RouteMetric)
        if ($routes.Count -eq 0) {
            throw 'No IPv4 default gateway found, and $base was not set.'
        }
        return [string]$routes[0].NextHop
    }

    # shared:begin
    # This block is the same in install.ps1 and update.ps1 (dev/test_serve.py checks it).
    # A new, empty folder that only SYSTEM and Administrators can write to, created with that
    # ACL in one step (no window in which another process could put a file in it). The zip is
    # downloaded, checked and expanded here, never in the user's %TEMP%, so a non-elevated
    # process cannot swap EdiabasAgent.exe between the hash check and the copy.
    function New-AdminOnlyDirectory {
        $path = Join-Path $env:ProgramData ('EdiabasBridge-staging-' + [guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $path) {
            throw ('Staging folder ' + $path + ' already exists. Nothing was changed.')
        }
        $acl = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                (New-Object System.Security.Principal.SecurityIdentifier($sid)),
                [System.Security.AccessControl.FileSystemRights]::FullControl,
                $inherit,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        [void][System.IO.Directory]::CreateDirectory($path, $acl)
        $check = Get-Acl -LiteralPath $path
        $others = @($check.Access | Where-Object {
            $s = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
            $s -ne 'S-1-5-18' -and $s -ne 'S-1-5-32-544'
        })
        if (-not $check.AreAccessRulesProtected -or $others.Count -ne 0) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            throw ('Could not restrict ' + $path + ' to Administrators. Nothing was changed.')
        }
        return $path
    }

    # Staging folders left behind when a window was closed mid-run (the finally that removes
    # them did not run). Only names New-AdminOnlyDirectory makes; a link is left alone, and
    # Directory.Delete does not follow links inside the folder.
    function Remove-StaleStaging {
        foreach ($dir in @(Get-ChildItem -LiteralPath $env:ProgramData -Directory -Force -ErrorAction SilentlyContinue)) {
            if ($dir.Name -notmatch '^EdiabasBridge-staging-[0-9a-f]{32}$') { continue }
            if (($dir.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Warn ($dir.FullName + ' is a link, not a staging folder; left alone.')
                continue
            }
            try {
                [System.IO.Directory]::Delete($dir.FullName, $true)
                Write-Host ('    Removed the left-over ' + $dir.FullName)
            } catch {
                Write-Warn ('Could not remove ' + $dir.FullName + ': ' + $_.Exception.Message)
            }
        }
    }

    # Copies the verified exe and checks that what landed is the same file.
    function Copy-VerifiedExe([string]$From, [string]$To, [string]$Hash) {
        $copied = $false
        for ($i = 0; $i -lt 10 -and -not $copied; $i++) {
            try {
                Copy-Item -LiteralPath $From -Destination $To -Force
                $copied = $true
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $copied) {
            return $false
        }
        $landed = (Get-FileHash -Algorithm SHA256 -LiteralPath $To).Hash
        if ($landed -ine $Hash) {
            Remove-Item -LiteralPath $To -Force -ErrorAction SilentlyContinue
            throw ('The copied ' + $To + ' (SHA-256 ' + $landed + ') is not the verified exe (' + $Hash + '). It was deleted; run the line again.')
        }
        return $true
    }

    function Stop-Agent {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        }
        Get-Process -Name 'EdiabasAgent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 20; $i++) {
            if ($null -eq (Get-Process -Name 'EdiabasAgent' -ErrorAction SilentlyContinue)) {
                return
            }
            Start-Sleep -Milliseconds 500
        }
        throw 'EdiabasAgent.exe is still running after 10 seconds; stop it in Task Manager and run this again.'
    }
    # shared:end

    $tmp = $null
    try {
        Assert-Elevated
        Assert-WindowsPowerShell
        Remove-StaleStaging

        $sha = Get-OptionalVar 'sha'
        if (-not $sha -or ([string]$sha).Trim() -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Set $sha to the SHA-256 of EdiabasAgent.zip (64 hex characters): paste the whole line serve.py prints. Nothing was changed.'
        }
        $sha = ([string]$sha).Trim()

        if (-not (Test-Path -LiteralPath $ExePath)) {
            throw ('The agent is not installed (' + $ExePath + ' is missing). Run install.ps1 instead. Nothing was changed.')
        }

        $base = Get-OptionalVar 'base'
        if (-not $base) {
            $base = 'http://' + (Get-DefaultGateway) + ':8000'
        }
        $base = ([string]$base).TrimEnd('/')

        # ---- download and verify, in a folder only Administrators can write ----
        $tmp = New-AdminOnlyDirectory
        $zip = Join-Path $tmp 'EdiabasAgent.zip'
        Write-Step ('Downloading ' + $base + '/EdiabasAgent.zip')
        Invoke-WebRequest -Uri ($base + '/EdiabasAgent.zip') -OutFile $zip -UseBasicParsing
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
        if ($actual -ine $sha) {
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            throw ('SHA-256 mismatch: expected ' + $sha + ', got ' + $actual + '. The download was deleted; nothing was changed.')
        }
        Write-Host ('    SHA-256 OK: ' + $actual)

        $unz = Join-Path $tmp 'unz'
        Expand-Archive -LiteralPath $zip -DestinationPath $unz -Force
        $newExe = Join-Path $unz 'EdiabasAgent.exe'
        $versionFile = Join-Path $unz 'version.txt'
        if (-not (Test-Path -LiteralPath $newExe) -or -not (Test-Path -LiteralPath $versionFile)) {
            throw 'The zip does not contain EdiabasAgent.exe and version.txt. Nothing was changed.'
        }
        $version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        $exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $newExe).Hash

        # ---- stop, replace, start ----
        Write-Step 'Stopping the scheduled task and any running agent'
        Stop-Agent

        Write-Step ('Replacing ' + $ExePath)
        if (-not (Copy-VerifiedExe $newExe $ExePath $exeHash)) {
            throw ('Could not replace ' + $ExePath + ' (file in use?). The old exe is still in place; start the task again with: Start-ScheduledTask -TaskName "' + $TaskName + '"')
        }

        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -eq $task) {
            Write-Warn ('Scheduled task "' + $TaskName + '" not found; the agent was not started. Run install.ps1 to recreate it.')
        } else {
            Write-Step 'Starting the scheduled task'
            Start-ScheduledTask -TaskName $TaskName
        }

        Write-Step ('Updated to ediabas-bridge agent ' + $version)
    } catch {
        Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    } finally {
        if ($null -ne $tmp -and (Test-Path -LiteralPath $tmp)) {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
