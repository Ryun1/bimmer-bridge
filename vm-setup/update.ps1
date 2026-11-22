# ediabas-bridge - replace the VM agent's exe with a new build.
#
# Run in an elevated Windows PowerShell 5.1 window inside the VM: paste the Update line that
# dev/serve.py prints. That line downloads this file, checks its SHA-256 against the value in
# the line, and only then runs it (with iex), with $sha (SHA-256 of EdiabasAgent.zip) set.
# Optional: $base (default http://<IPv4 default gateway>:8000).
#
# Verifies the hash, disables the scheduled task and stops the agent, copies the current exe
# to EdiabasAgent.exe.bak, puts the new exe in place, adds any default conflict process that
# config.json was never offered (conflictDefaultsOffered), makes the firewall rule again for
# the current gateway, enables and starts the task, and waits up to 60 s for /health. If the
# copy fails or the new agent does not answer, it puts the backup back (and config.json.bak,
# if this run merged config.json), starts it and checks /health once more, saying so when the
# restored agent does not answer either.
# The task is enabled again on every way out.
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
    $ConfigBackupPath = Join-Path $Root 'config.json.bak'
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
    # conflictProcesses and conflictDefaultsOffered; Update adds to an existing config.json
    # only the ones it was never offered, so a default the owner removed stays removed.
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

    # True when /health answered as a working agent does: 200, or 403 forbidden (from inside
    # the VM the agent serves the gateway only, so a 403 proves the listener works).
    function Test-AgentAnswered($result) {
        if ($null -eq $result) { return $false }
        if ($result.Status -eq 200) { return $true }
        return ($result.Status -eq 403 -and [string]$result.Body -like '*forbidden*')
    }

    # The defaults install.ps1 wrote before conflictDefaultsOffered existed (0.4.0 and earlier):
    # a config.json without that key counts as having been offered these.
    function Get-LegacyOfferedConflictDefaults {
        return @('INPALOAD', 'TOOL32', 'NCSEXPER', 'WINKFPT', 'BMW Coding Tool')
    }

    # $true when $Names holds $Name, compared without case.
    function Test-NameIn([string]$Name, $Names) {
        foreach ($n in $Names) {
            if ([string]$n -ieq $Name) { return $true }
        }
        return $false
    }

    # Adds to config.json's conflictProcesses the default conflict processes this file was never
    # offered (conflictDefaultsOffered, compared without case) and does not already list, after
    # the owner's own entries, and records every default in conflictDefaultsOffered, so a default
    # the owner removed is not added back. A file without that key counts as offered the 0.4.0
    # defaults; a file without a conflictProcesses array gets every default. Everything else in
    # the file (reviewedJobs and the other keys) is kept. A file that cannot be read or parsed,
    # or whose conflictDefaultsOffered is not an array, is left untouched.
    # Returns $true only when it rewrote config.json (the previous file is then config.json.bak).
    function Merge-ConflictDefaults {
        if (-not (Test-Path -LiteralPath $ConfigPath)) {
            Write-Warn ($ConfigPath + ' not found; conflict process defaults not merged. Run install.ps1 to write it.')
            return $false
        }
        try {
            $cfg = [System.IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
        } catch {
            Write-Warn ('Could not read ' + $ConfigPath + ' (' + $_.Exception.Message + '); left untouched, conflict process defaults not merged.')
            return $false
        }
        if ($null -eq $cfg -or -not ($cfg -is [System.Management.Automation.PSCustomObject])) {
            Write-Warn ($ConfigPath + ' is not a JSON object; left untouched, conflict process defaults not merged.')
            return $false
        }
        # What the written file must still hold.
        $origKeys = @($cfg.PSObject.Properties | ForEach-Object { $_.Name })
        $origReviewed = 0
        $rj = $cfg.PSObject.Properties['reviewedJobs']
        if ($null -ne $rj -and $null -ne $rj.Value) { $origReviewed = @($rj.Value).Count }

        # The defaults already offered to this file. Not an array: something other than Install
        # or Update wrote it, so the file is left as it is rather than guessed at.
        $offeredProp = $cfg.PSObject.Properties['conflictDefaultsOffered']
        $offeredHad = @()
        $offered = @(Get-LegacyOfferedConflictDefaults)
        if ($null -ne $offeredProp) {
            if (-not ($offeredProp.Value -is [System.Array])) {
                Write-Warn ('conflictDefaultsOffered in ' + $ConfigPath + ' is not a JSON array; left untouched, conflict process defaults not merged. Make it a list of names, or remove the key.')
                return $false
            }
            $offeredHad = @($offeredProp.Value)
            $offered = $offeredHad
        }

        $prop = $cfg.PSObject.Properties['conflictProcesses']
        $current = @()
        if ($null -ne $prop -and $null -ne $prop.Value) {
            $current = @($prop.Value)
        }
        # No array to remove a name from (missing, null or a single value): the owner removed
        # nothing, so every default it lacks is added, as before conflictDefaultsOffered.
        if ($null -eq $prop -or -not ($prop.Value -is [System.Array])) {
            $offered = @()
        }
        $added = @()
        $offeredMissing = @()
        foreach ($name in @(Get-DefaultConflictProcesses)) {
            if (-not (Test-NameIn $name $offeredHad)) { $offeredMissing += $name }
            if ((Test-NameIn $name $offered) -or (Test-NameIn $name $current)) { continue }
            $added += $name
        }
        if ($added.Count -eq 0 -and $offeredMissing.Count -eq 0) {
            Write-Note 'config.json has been offered every default conflict process before (conflictDefaultsOffered); not changed. A default removed from conflictProcesses stays removed.'
            return $false
        }
        # Plain .NET object[]s (List.ToArray, set through a method call, not a cmdlet
        # parameter), so ConvertTo-Json writes JSON arrays and not {"value":...,"Count":...},
        # also for one entry.
        $list = New-Object 'System.Collections.Generic.List[object]'
        foreach ($item in $current) { $list.Add($item) }
        foreach ($item in $added) { $list.Add($item) }
        $merged = $list.ToArray()
        if ($null -eq $prop) {
            $cfg.PSObject.Properties.Add([System.Management.Automation.PSNoteProperty]::new('conflictProcesses', $merged))
        } else {
            $prop.Value = $merged
        }
        $offeredList = New-Object 'System.Collections.Generic.List[object]'
        foreach ($item in $offeredHad) { $offeredList.Add($item) }
        foreach ($item in $offeredMissing) { $offeredList.Add($item) }
        $offeredNew = $offeredList.ToArray()
        if ($null -eq $offeredProp) {
            $cfg.PSObject.Properties.Add([System.Management.Automation.PSNoteProperty]::new('conflictDefaultsOffered', $offeredNew))
        } else {
            $offeredProp.Value = $offeredNew
        }
        $json = $cfg | ConvertTo-Json -Depth 10

        # The new text must parse back to the same file plus the added names and the
        # conflictDefaultsOffered written above.
        $problem = $null
        try {
            $re = $json | ConvertFrom-Json
        } catch {
            $re = $null
            $problem = 'it does not parse (' + $_.Exception.Message + ')'
        }
        if ($null -eq $problem -and -not ($re -is [System.Management.Automation.PSCustomObject])) {
            $problem = 'it is not a JSON object'
        }
        if ($null -eq $problem) {
            $reList = $re.PSObject.Properties['conflictProcesses']
            if ($null -eq $reList -or -not ($reList.Value -is [System.Array])) {
                $problem = 'conflictProcesses is not an array'
            } else {
                $reItems = @($reList.Value)
                if ($reItems.Count -ne $merged.Count) {
                    $problem = 'conflictProcesses has ' + $reItems.Count + ' entries, not ' + $merged.Count
                } else {
                    for ($i = 0; $i -lt $merged.Count; $i++) {
                        if ([string]$reItems[$i] -ine [string]$merged[$i]) {
                            $problem = 'conflictProcesses entry ' + $i + ' changed'
                            break
                        }
                    }
                }
            }
        }
        if ($null -eq $problem) {
            $reOffered = $re.PSObject.Properties['conflictDefaultsOffered']
            if ($null -eq $reOffered -or -not ($reOffered.Value -is [System.Array])) {
                $problem = 'conflictDefaultsOffered is not an array'
            } else {
                $reOfferedItems = @($reOffered.Value)
                if ($reOfferedItems.Count -ne $offeredNew.Count) {
                    $problem = 'conflictDefaultsOffered has ' + $reOfferedItems.Count + ' entries, not ' + $offeredNew.Count
                } else {
                    for ($i = 0; $i -lt $offeredNew.Count; $i++) {
                        if ([string]$reOfferedItems[$i] -cne [string]$offeredNew[$i]) {
                            $problem = 'conflictDefaultsOffered entry ' + $i + ' changed'
                            break
                        }
                    }
                }
            }
        }
        if ($null -eq $problem) {
            foreach ($key in $origKeys) {
                if ($null -eq $re.PSObject.Properties[$key]) {
                    $problem = 'the key ' + $key + ' is missing'
                    break
                }
            }
        }
        if ($null -eq $problem) {
            # Only conflictProcesses (when the file had none) and conflictDefaultsOffered are new.
            foreach ($reKey in @($re.PSObject.Properties | ForEach-Object { $_.Name })) {
                if (-not ($origKeys -contains $reKey) -and $reKey -ne 'conflictProcesses' -and $reKey -ne 'conflictDefaultsOffered') {
                    $problem = 'the key ' + $reKey + ' is new'
                    break
                }
            }
        }
        if ($null -eq $problem) {
            $newReviewed = 0
            $reRj = $re.PSObject.Properties['reviewedJobs']
            if ($null -ne $reRj -and $null -ne $reRj.Value) { $newReviewed = @($reRj.Value).Count }
            if ($newReviewed -ne $origReviewed) {
                $problem = 'reviewedJobs has ' + $newReviewed + ' entries, not ' + $origReviewed
            }
        }
        if ($null -ne $problem) {
            Write-Warn ('The merged config.json failed its check (' + $problem + '); config.json left untouched, conflict process defaults not merged.')
            return $false
        }

        # Written beside it, then swapped in; File.Replace keeps config.json's ACL and moves the
        # previous file to config.json.bak. A failed write never leaves a half-written config.json.
        $newPath = $ConfigPath + '.new'
        try {
            [System.IO.File]::WriteAllText($newPath, $json, (New-Object System.Text.UTF8Encoding($false)))
            [System.IO.File]::Replace($newPath, $ConfigPath, $ConfigBackupPath)
        } catch {
            Remove-Item -LiteralPath $newPath -Force -ErrorAction SilentlyContinue
            Write-Warn ('Could not write ' + $ConfigPath + ' (' + $_.Exception.Message + '); left as it was, conflict process defaults not merged.')
            return $false
        }
        if ($added.Count -gt 0) {
            Write-Note ('Added to conflictProcesses in config.json: ' + ($added -join ', ') + ' (previous file kept as ' + $ConfigBackupPath + ')')
        } else {
            Write-Note ('Nothing added to conflictProcesses in config.json (previous file kept as ' + $ConfigBackupPath + ')')
        }
        if ($offeredMissing.Count -gt 0) {
            Write-Note ('Recorded in conflictDefaultsOffered in config.json: ' + ($offeredMissing -join ', ') + '. Update does not add these again; a default removed from conflictProcesses stays removed.')
        }
        return $true
    }

    $tmp = $null
    $taskDisabled = $false
    $wasDisabled = $false
    $configMerged = $false
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
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -eq $task) {
            throw ('Scheduled task "' + $TaskName + '" not found. Run install.ps1 instead; it recreates it. Nothing was changed.')
        }
        $wasDisabled = ([string]$task.State -eq 'Disabled')

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
        try {
            $configMerged = Merge-ConflictDefaults
        } catch {
            # Never a reason to stop: the update carries on to the health check (and rollback).
            $configMerged = $false
            Write-Warn ('Merging the conflict process defaults failed unexpectedly (' + $_.Exception.Message + '); config.json left untouched, defaults not merged.')
        }

        # ---- 6. firewall ----
        $existing = Read-ExistingConfig
        $port = $existing.Port
        try {
            Set-FirewallRule $port
        } catch {
            throw ('The new exe (' + $version + ') is in place, but the firewall rule "' + $RuleName + '" could not be made (' + $_.Exception.Message + '), so it is missing or still pinned to the old gateway: the Mac cannot reach the agent. Run the Install line again.')
        }

        # ---- 7. start ----
        Write-Step 'Enabling and starting the scheduled task'
        Enable-ScheduledTask -TaskName $TaskName | Out-Null
        $taskDisabled = $false
        Start-ScheduledTask -TaskName $TaskName
        if ($wasDisabled) {
            Write-Warn ('The scheduled task was disabled before this update; it has been enabled and started.')
        }

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
            if ($null -ne $result -and $result.Status -eq 200) {
                Write-Note 'Agent is up. /health:'
                Write-Host $result.Body
            } elseif ($null -ne $result -and $result.Status -eq 403 -and [string]$result.Body -like '*forbidden*') {
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
                $configNote = ''
                if ($configMerged) {
                    try {
                        [System.IO.File]::Copy($ConfigBackupPath, $ConfigPath, $true)
                        $configNote = ' and config.json from ' + $ConfigBackupPath
                    } catch {
                        Write-Warn ('Could not put ' + $ConfigBackupPath + ' back as config.json (' + $_.Exception.Message + '); the merged config.json stays (only conflictProcesses and conflictDefaultsOffered were changed in it).')
                    }
                }
                Enable-ScheduledTask -TaskName $TaskName | Out-Null
                $taskDisabled = $false
                Start-ScheduledTask -TaskName $TaskName
                Write-Host ('Rolled back to ' + $oldVersion + $configNote) -ForegroundColor Yellow

                # One more check: if the restored agent does not answer either, the fault is not
                # the new exe (config.json's bindAddress or port, the network), so say so.
                Write-Step ('Waiting up to 60 s for the restored agent (' + $oldVersion + ') at ' + $url)
                $restored = Wait-AgentAnswer $url $token 60
                $token = $null
                if (Test-AgentAnswered $restored) {
                    Write-Note ('The restored agent (' + $oldVersion + ') answers /health.')
                    throw ('Agent ' + $version + ' did not start; ' + $oldVersion + ' was put back, started and answers. Check ' + (Join-Path $LogDir 'agent.log'))
                }
                Write-Warn ('The restored agent (' + $oldVersion + ') does not answer at ' + $url + ' either, so the new exe is probably not the cause: check bindAddress and port in ' + $ConfigPath + ' and the VM network.')
                throw ('Agent ' + $version + ' did not start, and the restored ' + $oldVersion + ' does not answer either. Check ' + $ConfigPath + ' and ' + (Join-Path $LogDir 'agent.log'))
            }
            $token = $null
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
                if ($wasDisabled) {
                    Write-Warn ('The scheduled task was disabled before this update; it has been enabled and started.')
                }
            } catch {
                Write-Warn ('Could not enable and start the scheduled task: ' + $_.Exception.Message + '. Run: Enable-ScheduledTask -TaskName "' + $TaskName + '"; Start-ScheduledTask -TaskName "' + $TaskName + '"')
            }
        }
        if ($null -ne $tmp -and (Test-Path -LiteralPath $tmp)) {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
