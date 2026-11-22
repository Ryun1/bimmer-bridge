# ediabas-bridge - install or upgrade the VM agent.
#
# Run in an elevated Windows PowerShell 5.1 window inside the VM: paste the Install line that
# dev/serve.py prints. That line downloads this file, checks its SHA-256 against the value in
# the line, and only then runs it (with iex), with these variables set:
#   $sha      SHA-256 of EdiabasAgent.zip (required)
# Optional variables set before it on the same line:
#   $base     server URL (default http://<IPv4 default gateway>:8000)
#   $Elevated $true to run the agent with highest privileges (default: limited)
#   $SendToken $true to send the existing token to the Mac (serve.py --accept-token sets it);
#             a first install always sends it. The token is never printed.
#
# Idempotent: re-running upgrades in place. config.json and token.txt are kept if present.
# This file must stay ASCII-only and Windows PowerShell 5.1 compatible (it runs via iex).

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    Set-StrictMode -Version Latest

    $Root = 'C:\ProgramData\EdiabasBridge'
    $LogDir = Join-Path $Root 'logs'
    $ExePath = Join-Path $Root 'EdiabasAgent.exe'
    $ConfigPath = Join-Path $Root 'config.json'
    $TokenPath = Join-Path $Root 'token.txt'
    $TaskName = 'ediabas-bridge agent'
    $RuleName = 'ediabas-bridge agent'
    $DefaultPort = 8765

    function Get-OptionalVar([string]$Name) {
        return Get-Variable -Name $Name -ValueOnly -ErrorAction SilentlyContinue
    }

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
        # The ACL calls below use .NET Framework APIs that PowerShell 7 does not have.
        if ($PSVersionTable.PSEdition -ne 'Desktop') {
            throw 'This script must run in "Windows PowerShell" (5.1), not PowerShell 7. Open "Windows PowerShell" as administrator and paste the command again. Nothing was changed.'
        }
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

    function Get-Sha256Hex([string]$Path) {
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    }

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

    function New-Grant($Sid, [string]$Rights) {
        return New-Object psobject -Property @{ Sid = $Sid; Rights = $Rights }
    }

    # Replaces the DACL with exactly the given grants and turns inheritance from the parent off.
    function Set-ExactAcl([string]$Path, [bool]$IsDirectory, [object[]]$Grants) {
        if ($IsDirectory) {
            $acl = New-Object System.Security.AccessControl.DirectorySecurity
            $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        } else {
            $acl = New-Object System.Security.AccessControl.FileSecurity
            $inherit = [System.Security.AccessControl.InheritanceFlags]::None
        }
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($g in $Grants) {
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                ([System.Security.Principal.IdentityReference]($g.Sid)),
                ([System.Security.AccessControl.FileSystemRights]($g.Rights)),
                $inherit,
                [System.Security.AccessControl.PropagationFlags]::None,
                [System.Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        if ($IsDirectory) {
            [System.IO.Directory]::SetAccessControl($Path, $acl)
        } else {
            [System.IO.File]::SetAccessControl($Path, $acl)
        }
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

    function New-Token {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
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

    $tmp = $null
    try {
        # Options first, so they are cleared even when a check below stops the script.
        $runElevated = ((Get-OneShotVar 'Elevated') -eq $true)
        $sendToken = ((Get-OneShotVar 'SendToken') -eq $true)

        Assert-Elevated
        Assert-WindowsPowerShell

        # ---- inputs ----
        $sha = Get-OptionalVar 'sha'
        if (-not $sha -or ([string]$sha).Trim() -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Set $sha to the SHA-256 of EdiabasAgent.zip (64 hex characters): paste the whole line serve.py prints. Nothing was changed.'
        }
        $sha = ([string]$sha).Trim()

        $route = Get-DefaultRoute
        $gateway = [string]$route.NextHop
        $base = Get-OptionalVar 'base'
        if (-not $base) {
            $base = 'http://' + $gateway + ':8000'
        }
        $base = ([string]$base).TrimEnd('/')

        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $userName = $identity.Name
        $userSid = $identity.User

        Write-Step ('Installing ediabas-bridge agent for ' + $userName + ' from ' + $base)

        # ---- 1. download and verify, in a folder only Administrators can write ----
        $tmp = New-AdminOnlyDirectory
        $zip = Join-Path $tmp 'EdiabasAgent.zip'
        Write-Step ('Downloading ' + $base + '/EdiabasAgent.zip')
        Invoke-WebRequest -Uri ($base + '/EdiabasAgent.zip') -OutFile $zip -UseBasicParsing
        $actual = Get-Sha256Hex $zip
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
        $exeHash = Get-Sha256Hex $newExe

        # ---- 2. stop a running agent ----
        Write-Step 'Stopping the scheduled task and any running agent'
        Stop-Agent

        # ---- 3. folders and ACLs ----
        Write-Step ('Creating ' + $Root + ' and setting permissions')
        New-Item -ItemType Directory -Path $Root -Force | Out-Null
        New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
        $sidSystem = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
        $sidAdmins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
        Set-ExactAcl $Root $true @(
            (New-Grant $sidSystem 'FullControl'),
            (New-Grant $sidAdmins 'FullControl'),
            (New-Grant $userSid 'ReadAndExecute'))
        Set-ExactAcl $LogDir $true @(
            (New-Grant $sidSystem 'FullControl'),
            (New-Grant $sidAdmins 'FullControl'),
            (New-Grant $userSid 'Modify'))

        # ---- 4. the exe ----
        Write-Step ('Installing EdiabasAgent.exe ' + $version)
        if (-not (Copy-VerifiedExe $newExe $ExePath $exeHash)) {
            throw ('Could not replace ' + $ExePath + ' (file in use?).')
        }

        # ---- 5. config.json (only if absent) ----
        if (Test-Path -LiteralPath $ConfigPath) {
            Write-Step 'config.json already exists; leaving it untouched'
        } else {
            Write-Step 'Writing config.json from detected values'
            $ediabasRoot = $null
            foreach ($candidate in @('C:\EDIABAS', 'C:\EC-APPS\EDIABAS')) {
                if (Test-Path -LiteralPath (Join-Path $candidate 'BIN\api32.dll')) { $ediabasRoot = $candidate; break }
            }
            if ($null -eq $ediabasRoot) {
                foreach ($candidate in @('C:\EDIABAS', 'C:\EC-APPS\EDIABAS')) {
                    if (Test-Path -LiteralPath $candidate) { $ediabasRoot = $candidate; break }
                }
            }
            if ($null -eq $ediabasRoot) {
                $ediabasRoot = 'C:\EDIABAS'
                Write-Warn 'EDIABAS not found in C:\EDIABAS or C:\EC-APPS\EDIABAS; using C:\EDIABAS. Edit config.json if it lives elsewhere.'
            }
            $logRoots = @(@((Join-Path $ediabasRoot 'TRACE'), 'C:\NCSEXPER\WORK') | Where-Object { Test-Path -LiteralPath $_ })
            $config = [ordered]@{
                bindAddress          = 'auto'
                port                 = $DefaultPort
                tokenFile            = $TokenPath
                logDirectory         = $LogDir
                ediabasDll           = (Join-Path $ediabasRoot 'BIN\api32.dll')
                ecuPath              = (Join-Path $ediabasRoot 'ECU')
                logRoots             = [object[]]$logRoots
                conflictProcesses    = [object[]]@('INPALOAD', 'TOOL32', 'NCSEXPER', 'WINKFPT', 'BMW Coding Tool')
                voltageSources       = [object[]]@([ordered]@{ ecu = 'NVE_65'; job = 'STATUS_BORDNETZSPANNUNG' })
                reviewedJobs         = [object[]]@()
                jobTimeoutSeconds    = 30
                maxJobTimeoutSeconds = 60
            }
            $json = $config | ConvertTo-Json -Depth 5
            [System.IO.File]::WriteAllText($ConfigPath, $json, (New-Object System.Text.UTF8Encoding($false)))
            Write-Note ('ediabasDll = ' + $config.ediabasDll)
            Write-Note ('ecuPath    = ' + $config.ecuPath)
            Write-Note ('logRoots   = ' + ($logRoots -join ', '))
        }
        $existing = Read-ExistingConfig
        $port = $existing.Port

        # ---- 6. token (only if absent) ----
        $firstInstall = -not (Test-Path -LiteralPath $TokenPath)
        $token = $null
        if ($firstInstall) {
            Write-Step 'Generating a new token'
            $token = New-Token
            [System.IO.File]::WriteAllText($TokenPath, $token, (New-Object System.Text.ASCIIEncoding))
        } else {
            Write-Step 'token.txt already exists; keeping it'
            $token = ([System.IO.File]::ReadAllText($TokenPath)).Trim()
        }
        Set-ExactAcl $TokenPath $false @(
            (New-Grant $sidSystem 'FullControl'),
            (New-Grant $sidAdmins 'FullControl'),
            (New-Grant $userSid 'Read'))

        # ---- 7. firewall ----
        Write-Step ('Firewall: inbound TCP ' + $port + ' from ' + $gateway + ' only')
        Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Action Allow -Protocol TCP `
            -LocalPort $port -RemoteAddress $gateway -Program $ExePath -Profile Any | Out-Null

        # ---- 8. scheduled task ----
        if ($runElevated) { $runLevel = 'Highest' } else { $runLevel = 'Limited' }
        Write-Step ('Scheduled task "' + $TaskName + '" at log-on of ' + $userName + ' and every 5 minutes, run level ' + $runLevel)
        $action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $Root
        # Two triggers: at log-on, and every 5 minutes indefinitely (no RepetitionDuration) so the
        # agent comes back after a crash. IgnoreNew plus the agent's mutex make the repeat harmless
        # while it runs.
        $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
        $repeatTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
        $triggers = @($logonTrigger, $repeatTrigger)
        $principal = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel $runLevel
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartInterval (New-TimeSpan -Minutes 1) -RestartCount 999 `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $triggers -Principal $principal `
            -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName

        # ---- 9. sleep ----
        Write-Step 'Disabling sleep on AC power'
        & powercfg.exe /change standby-timeout-ac 0
        if ($LASTEXITCODE -ne 0) {
            Write-Warn ('powercfg returned ' + $LASTEXITCODE)
        }
        Write-Note 'The screen lock was NOT changed. A locked screen blocks screenshots (later phases).'

        # ---- 10. is it up? ----
        $bindAddress = $existing.BindAddress
        if ($bindAddress -ieq 'auto') {
            $bindAddress = Get-InterfaceIPv4 $route.InterfaceIndex
        }
        if (-not $bindAddress) {
            Write-Warn 'Could not work out the bind address; skipping the health check.'
        } else {
            $url = 'http://' + $bindAddress + ':' + $port + '/health'
            Write-Step ('Waiting up to 20 s for the agent at ' + $url)
            $result = $null
            $deadline = (Get-Date).AddSeconds(20)
            while ((Get-Date) -lt $deadline) {
                $result = Invoke-Http 'GET' $url ('Bearer ' + $token) $null 3000
                if ($result.Status -ne 0) { break }
                Start-Sleep -Seconds 1
            }
            if ($null -eq $result -or $result.Status -eq 0) {
                $why = ''
                if ($null -ne $result) { $why = ' (' + $result.Body + ')' }
                Write-Warn ('The agent did not answer within 20 s' + $why + '. Check ' + (Join-Path $LogDir 'agent.log'))
            } elseif ($result.Status -eq 200) {
                Write-Note 'Agent is up. /health:'
                Write-Host $result.Body
            } elseif ($result.Status -eq 403) {
                Write-Note 'Agent is up. (It answered 403 forbidden because this request came from the VM itself,'
                Write-Note 'not from the Mac; that proves the listener works.)'
            } else {
                Write-Warn ('The agent answered HTTP ' + $result.Status + ': ' + $result.Body)
            }
        }

        # ---- 11. token hand-off (first install, or $SendToken) ----
        # serve.py accepts PUT /token only when started with --accept-token, and then puts
        # $SendToken=$true on the Install line. The token is never printed.
        if ($firstInstall -or $sendToken) {
            Write-Step 'Sending the token to the Mac'
            $put = Invoke-Http 'PUT' ($base + '/token') $null $token 10000
            if ($put.Status -ge 200 -and $put.Status -lt 300) {
                Write-Note "token stored in the Mac's Keychain"
            } else {
                Write-Warn ('Could not send the token to the Mac (HTTP ' + $put.Status + '). On the Mac, start: python3 dev/serve.py --accept-token, then paste the Install line it prints again.')
            }
        } else {
            Write-Note 'The token was not sent to the Mac. To send it, start serve.py on the Mac with --accept-token and paste the Install line it prints.'
        }
        $token = $null

        Write-Step ('Done. ediabas-bridge agent ' + $version + ' listens on port ' + $port + '.')
    } catch {
        Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    } finally {
        if ($null -ne $tmp -and (Test-Path -LiteralPath $tmp)) {
            Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
