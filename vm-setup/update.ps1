# ediabas-bridge - replace the VM agent's exe with a new build.
#
# Run in an elevated Windows PowerShell 5.1 window inside the VM: paste the Update line that
# dev/serve.py prints. That line downloads this file, checks its SHA-256 against the value in
# the line, and only then runs it (with iex), with $sha (SHA-256 of EdiabasAgent.zip) set.
# Optional: $base (default http://<IPv4 default gateway>:8000).
#
# Verifies the hash, disables the scheduled task and stops the agent, copies the current exe
# to EdiabasAgent.exe.bak, puts the new exe in place, adds any default conflict process that
# config.json lacks, makes the firewall rule again for the current gateway, enables and starts
# the task, and waits up to 60 s for /health. If the copy fails or the new agent does not
# answer, it puts the backup back and starts it. The task is enabled again on every way out.
# The token, the ACLs, the task settings and every other config.json setting are left as they are.
# This file must stay ASCII-only and Windows PowerShell 5.1 compatible (it runs via iex).

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    Set-StrictMode -Version Latest

    $Root = 'C:\ProgramData\EdiabasBridge'
    $LogDir = Join-Path $Root 'logs'
    $ExePath = Join-Path $Root 'EdiabasAgent.exe'
    $BackupPath = Join-Path $Root 'EdiabasAgent.exe.bak'
    $ConfigPath = Join-Path $Root 'config.json'
    $TokenPath = Join-Path $Root 'token.txt'
    $TaskName = 'ediabas-bridge agent'
    $RuleName = 'ediabas-bridge agent'
    $DefaultPort = 8765

    function Get-OptionalVar([string]$Name) {
        return Get-Variable -Name $Name -ValueOnly -ErrorAction SilentlyContinue
    }

    function Write-Step([string]$Text) {
        Write-Host ('==> ' + $Text) -ForegroundColor Cyan
    }

    function Write-Note([string]$Text) {
        Write-Host ('    ' + $Text)
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

    # Copies an exe and checks that what landed is the file with SHA-256 $Hash. Returns $false
    # when the copy fails or the hash differs; the target is then left as it is, and the caller
    # puts the backup back (update) or stops (install).
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
            Write-Warn ('The copied ' + $To + ' (SHA-256 ' + $landed + ') is not the expected file (' + $Hash + ').')
            return $false
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

    function Get-DefaultRoute {
        $routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Where-Object { $_.NextHop -ne '0.0.0.0' } |
            Sort-Object -Property RouteMetric)
        if ($routes.Count -eq 0) {
            throw 'No IPv4 default gateway found. Is the VM network (UTM Shared Network) connected?'
        }
        if ($routes.Count -gt 1) {
            Write-Warn ('Several IPv4 default routes; using ' + $routes[0].NextHop + ' on interface ' + $routes[0].InterfaceAlias)
        }
        return $routes[0]
    }

    function Get-InterfaceIPv4([int]$InterfaceIndex) {
        $addr = @(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike '169.254.*' } |
            Select-Object -First 1)
        if ($addr.Count -eq 0) {
            return $null
        }
        return $addr[0].IPAddress
    }

    # The address the agent listens on: bindAddress from config.json, or for 'auto' the IPv4
    # address of the default route's interface ($null when there is none).
    function Resolve-BindAddress([string]$Configured, [int]$InterfaceIndex) {
        if ($Configured -ieq 'auto') {
            return Get-InterfaceIPv4 $InterfaceIndex
        }
        return $Configured
    }

    function Read-ExistingConfig {
        $result = New-Object psobject -Property @{ Port = $DefaultPort; BindAddress = 'auto' }
        try {
            $cfg = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
            $p = $cfg.PSObject.Properties['port']
            if ($null -ne $p -and $null -ne $p.Value) { $result.Port = [int]($p.Value) }
            $b = $cfg.PSObject.Properties['bindAddress']
            if ($null -ne $b -and $null -ne $b.Value) { $result.BindAddress = [string]($b.Value) }
        } catch {
            Write-Warn ('Could not read port/bindAddress from the existing config.json (' + $_.Exception.Message + '); assuming port ' + $DefaultPort + ' and auto.')
        }
        return $result
    }

    # The BMW tools that hold the cable while they run. A new config.json lists them as
    # conflictProcesses; Update adds any of them that an existing config.json lacks.
    function Get-DefaultConflictProcesses {
        return @('INPALOAD', 'TOOL32', 'NCSEXPER', 'WINKFPT', 'BMW Coding Tool', 'WINKFP', 'ISTAGUI')
    }

    # Inbound TCP to the agent, from the current IPv4 default gateway (the Mac) only. Made
    # again on every install and update, so a gateway that changed since is picked up.
    function Set-FirewallRule([int]$Port) {
        $gateway = [string](Get-DefaultRoute).NextHop
        Write-Step ('Firewall: inbound TCP ' + $Port + ' from ' + $gateway + ' only')
        Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol TCP `
            -LocalPort $Port -RemoteAddress $gateway -Program $ExePath -Profile Any | Out-Null
    }

    function Find-InnerWebException($Exception) {
        $ex = $Exception
        while ($null -ne $ex) {
            if ($ex -is [System.Net.WebException]) {
                return $ex
            }
            $ex = $ex.InnerException
        }
        return $null
    }

    # Returns an object with Status (int, 0 = no HTTP response) and Body.
    function Invoke-Http([string]$Method, [string]$Url, [string]$Authorization, $Body, [int]$TimeoutMs) {
        $req = [System.Net.HttpWebRequest]::Create($Url)
        $req.Method = $Method
        $req.Proxy = $null
        $req.Timeout = $TimeoutMs
        $req.ReadWriteTimeout = $TimeoutMs
        $req.KeepAlive = $false
        if ($Authorization) {
            $req.Headers.Add('Authorization', $Authorization)
        }
        if (-not [string]::IsNullOrEmpty([string]$Body)) {
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($Body)
            $req.ContentType = 'text/plain; charset=utf-8'
            $req.ContentLength = $bytes.Length
            $stream = $req.GetRequestStream()
            try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Close() }
        }
        $resp = $null
        try {
            $resp = $req.GetResponse()
        } catch {
            $web = Find-InnerWebException $_.Exception
            if ($null -ne $web -and $null -ne $web.Response) {
                $resp = $web.Response
            } else {
                $msg = $_.Exception.Message
                if ($null -ne $web) { $msg = $web.Message }
                return New-Object psobject -Property @{ Status = 0; Body = $msg }
            }
        }
        try {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $text = $reader.ReadToEnd()
            $reader.Close()
            return New-Object psobject -Property @{ Status = [int]$resp.StatusCode; Body = $text }
        } finally {
            $resp.Close()
        }
    }

    # Asks the agent's /health once a second until it answers over HTTP (any status) or
    # $Seconds pass. Returns the last Invoke-Http result, or $null if none was made. From
    # inside the VM the agent answers 403 (it serves the gateway only): the listener works.
    function Wait-AgentAnswer([string]$Url, [string]$Token, [int]$Seconds) {
        $auth = $null
        if ($Token) { $auth = 'Bearer ' + $Token }
        $result = $null
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            $result = Invoke-Http 'GET' $Url $auth $null 3000
            if ($result.Status -ne 0) { break }
            Start-Sleep -Seconds 1
        }
        return $result
    }
    # shared:end

    # The version the exe reports (the agent's Version, e.g. 0.4.0).
    function Get-ExeVersion([string]$Path) {
        $v = [string](Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
        if ([string]::IsNullOrEmpty($v)) {
            return 'an unknown version'
        }
        return $v.Trim()
    }

    # Puts EdiabasAgent.exe.bak back in place. $true when the exe is the backed-up file again.
    function Restore-Backup([string]$OldHash) {
        Write-Step ('Restoring the previous exe from ' + $BackupPath)
        return (Copy-VerifiedExe $BackupPath $ExePath $OldHash)
    }

    function Get-RestoreByHand {
        return ('Stop the task (Stop-ScheduledTask -TaskName "' + $TaskName + '"), copy ' + $BackupPath +
            ' over ' + $ExePath + ', then run: Enable-ScheduledTask -TaskName "' + $TaskName +
            '"; Start-ScheduledTask -TaskName "' + $TaskName + '"')
    }

    # Adds the default conflict processes that config.json's conflictProcesses lacks (compared
    # without case), after the owner's own entries. Everything else in the file (reviewedJobs
    # and the other keys) is kept. A file that cannot be read or parsed is left untouched.
    function Merge-ConflictDefaults {
        if (-not (Test-Path -LiteralPath $ConfigPath)) {
            Write-Warn ($ConfigPath + ' not found; conflict process defaults not merged. Run install.ps1 to write it.')
            return
        }
        try {
            $cfg = [System.IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
        } catch {
            Write-Warn ('Could not read ' + $ConfigPath + ' (' + $_.Exception.Message + '); left untouched, conflict process defaults not merged.')
            return
        }
        if ($null -eq $cfg -or -not ($cfg -is [System.Management.Automation.PSCustomObject])) {
            Write-Warn ($ConfigPath + ' is not a JSON object; left untouched, conflict process defaults not merged.')
            return
        }
        $prop = $cfg.PSObject.Properties['conflictProcesses']
        $current = @()
        if ($null -ne $prop -and $null -ne $prop.Value) {
            $current = @($prop.Value)
        }
        $added = @()
        foreach ($name in @(Get-DefaultConflictProcesses)) {
            $found = $false
            foreach ($have in $current) {
                if ([string]$have -ieq $name) { $found = $true; break }
            }
            if (-not $found) { $added += $name }
        }
        if ($added.Count -eq 0) {
            Write-Note 'config.json already lists every default conflict process'
            return
        }
        $merged = [object[]]($current + $added)
        if ($null -eq $prop) {
            $cfg | Add-Member -NotePropertyName 'conflictProcesses' -NotePropertyValue $merged
        } else {
            $prop.Value = $merged
        }
        $json = $cfg | ConvertTo-Json -Depth 10
        try {
            $null = $json | ConvertFrom-Json
        } catch {
            Write-Warn ('The merged config.json would not parse (' + $_.Exception.Message + '); left untouched.')
            return
        }
        # Written beside it, then swapped in (File.Replace keeps config.json's ACL): a failed
        # write never leaves a half-written config.json.
        $newPath = $ConfigPath + '.new'
        try {
            [System.IO.File]::WriteAllText($newPath, $json, (New-Object System.Text.UTF8Encoding($false)))
            [System.IO.File]::Replace($newPath, $ConfigPath, $null)
        } catch {
            Remove-Item -LiteralPath $newPath -Force -ErrorAction SilentlyContinue
            Write-Warn ('Could not write ' + $ConfigPath + ' (' + $_.Exception.Message + '); left as it was, conflict process defaults not merged.')
            return
        }
        Write-Note ('Added to conflictProcesses in config.json: ' + ($added -join ', '))
    }

    $tmp = $null
    $taskDisabled = $false
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
        if ($null -eq (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
            throw ('Scheduled task "' + $TaskName + '" not found. Run install.ps1 instead; it recreates it. Nothing was changed.')
        }

        $route = Get-DefaultRoute
        $base = Get-OptionalVar 'base'
        if (-not $base) {
            $base = 'http://' + [string]$route.NextHop + ':8000'
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
        Write-Note ('SHA-256 OK: ' + $actual)

        $unz = Join-Path $tmp 'unz'
        Expand-Archive -LiteralPath $zip -DestinationPath $unz -Force
        $newExe = Join-Path $unz 'EdiabasAgent.exe'
        $versionFile = Join-Path $unz 'version.txt'
        if (-not (Test-Path -LiteralPath $newExe) -or -not (Test-Path -LiteralPath $versionFile)) {
            throw 'The zip does not contain EdiabasAgent.exe and version.txt. Nothing was changed.'
        }
        $version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        $exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $newExe).Hash
        $oldVersion = Get-ExeVersion $ExePath
        Write-Step ('Updating ediabas-bridge agent ' + $oldVersion + ' to ' + $version)

        # ---- 1-2. task disabled (so its 5-minute trigger cannot start the exe mid-copy), agent stopped ----
        Write-Step 'Disabling the scheduled task and stopping the agent'
        Disable-ScheduledTask -TaskName $TaskName | Out-Null
        $taskDisabled = $true
        Stop-Agent

        # ---- 3. backup ----
        $oldHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath).Hash
        Write-Step ('Backing up the current exe to ' + $BackupPath)
        if (-not (Copy-VerifiedExe $ExePath $BackupPath $oldHash)) {
            throw ('Could not back up ' + $ExePath + ' to ' + $BackupPath + '. The exe was not replaced.')
        }

        # ---- 4. the new exe ----
        Write-Step ('Replacing ' + $ExePath)
        if (-not (Copy-VerifiedExe $newExe $ExePath $exeHash)) {
            if (Restore-Backup $oldHash) {
                throw ('Could not replace ' + $ExePath + ' (file in use, or the copy differs). The previous exe (' + $oldVersion + ') was put back.')
            }
            throw ('Could not replace ' + $ExePath + ', and could not put the backup back. ' + (Get-RestoreByHand))
        }

        # ---- 5. conflict process defaults ----
        Write-Step 'Checking conflictProcesses in config.json'
        Merge-ConflictDefaults

        # ---- 6. firewall ----
        $existing = Read-ExistingConfig
        $port = $existing.Port
        Set-FirewallRule $port

        # ---- 7. start ----
        Write-Step 'Enabling and starting the scheduled task'
        Enable-ScheduledTask -TaskName $TaskName | Out-Null
        $taskDisabled = $false
        Start-ScheduledTask -TaskName $TaskName

        # ---- 8. is the new agent up? If not, back to the old one ----
        $bindAddress = Resolve-BindAddress $existing.BindAddress $route.InterfaceIndex
        if (-not $bindAddress) {
            Write-Warn 'Could not work out the bind address; skipped the health check. Check vm_health from the Mac.'
        } else {
            $token = $null
            try {
                $token = ([System.IO.File]::ReadAllText($TokenPath)).Trim()
            } catch {
                Write-Warn ('Could not read ' + $TokenPath + '; asking /health without the token.')
            }
            $url = 'http://' + $bindAddress + ':' + $port + '/health'
            Write-Step ('Waiting up to 60 s for the new agent at ' + $url)
            $result = Wait-AgentAnswer $url $token 60
            $token = $null
            if ($null -ne $result -and $result.Status -eq 200) {
                Write-Note 'Agent is up. /health:'
                Write-Host $result.Body
            } elseif ($null -ne $result -and $result.Status -eq 403) {
                Write-Note 'Agent is up. (It answered 403 forbidden because this request came from the VM itself,'
                Write-Note 'not from the Mac; that proves the listener works.)'
            } else {
                $why = ''
                if ($null -ne $result -and $result.Status -eq 0) { $why = ' (' + $result.Body + ')' }
                if ($null -ne $result -and $result.Status -ne 0) { $why = ' (HTTP ' + $result.Status + ': ' + $result.Body + ')' }
                Write-Warn ('The new agent did not come up within 60 s' + $why + '. Rolling back.')
                Disable-ScheduledTask -TaskName $TaskName | Out-Null
                $taskDisabled = $true
                Stop-Agent
                if (-not (Restore-Backup $oldHash)) {
                    throw ('Agent ' + $version + ' did not start, and the backup could not be put back. ' + (Get-RestoreByHand))
                }
                Enable-ScheduledTask -TaskName $TaskName | Out-Null
                $taskDisabled = $false
                Start-ScheduledTask -TaskName $TaskName
                Write-Host ('Rolled back to ' + $oldVersion) -ForegroundColor Yellow
                throw ('Agent ' + $version + ' did not start; ' + $oldVersion + ' was put back and started. Check ' + (Join-Path $LogDir 'agent.log'))
            }
        }

        Write-Step ('Updated to ediabas-bridge agent ' + $version)
    } catch {
        Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    } finally {
        # Every way out after the task was disabled: enable it again and start whatever exe is
        # in place (after a failed copy that is the previous one, put back from the backup).
        if ($taskDisabled) {
            try {
                Enable-ScheduledTask -TaskName $TaskName | Out-Null
                Start-ScheduledTask -TaskName $TaskName
                Write-Note ('Scheduled task "' + $TaskName + '" enabled and started again.')
            } catch {
                Write-Warn ('Could not enable and start the scheduled task: ' + $_.Exception.Message + '. Run: Enable-ScheduledTask -TaskName "' + $TaskName + '"; Start-ScheduledTask -TaskName "' + $TaskName + '"')
            }
        }
        if ($null -ne $tmp -and (Test-Path -LiteralPath $tmp)) {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
