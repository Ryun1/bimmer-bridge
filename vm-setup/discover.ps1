# ediabas-bridge - Phase 0 discovery (read-only)
#
# Run in the VM from Windows PowerShell 5.1: paste the Discovery line that dev/serve.py prints
# (it checks this file's SHA-256 before running it with iex).
#
# This script only READS. It changes no setting, installs nothing, starts no BMW tool,
# makes no EDIABAS call and does not touch the diagnostic cable. It writes one report,
# ediabas-bridge-discovery.txt, to the Desktop and uploads it (plus api.h if found) to
# dev/serve.py on the Mac. It never calls 'exit', because under iex that would
# close the PowerShell window.

$EbdVersion = '1'
$EbdState = @{
    Lines      = New-Object System.Collections.ArrayList
    Limited    = New-Object System.Collections.ArrayList
    Found      = New-Object System.Collections.ArrayList
    Elevated   = $false
    Base       = $null
    EcuPath    = $null
    TracePath  = $null
    ApiH       = $null
}

function Out-EbdLine {
    param([string]$Text = '')
    [void]$EbdState.Lines.Add($Text)
}

function Out-EbdText {
    param($Value)
    if ($null -eq $Value) { return }
    $s = ($Value | Out-String -Width 400)
    foreach ($l in ($s -split "`r?`n")) { Out-EbdLine $l.TrimEnd() }
}

function Add-EbdLimited {
    param([string]$Text)
    [void]$EbdState.Limited.Add($Text)
    Out-EbdLine ('  [limited: not elevated] ' + $Text)
}

function Invoke-EbdSection {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ('  ' + $Name + ' ...')
    Out-EbdLine ''
    Out-EbdLine ('=' * 78)
    Out-EbdLine ('== ' + $Name)
    Out-EbdLine ('=' * 78)
    $ErrorActionPreference = 'Stop'
    try {
        & $Body
    } catch {
        Out-EbdLine ('SECTION FAILED: ' + $_.Exception.Message)
        Write-Host ('    section failed: ' + $_.Exception.Message) -ForegroundColor Yellow
    }
}

# Runs a native program and returns its output (stdout and stderr) as strings. Native stderr
# under ErrorActionPreference=Stop would throw in Windows PowerShell 5.1, so relax it here.
function Invoke-EbdNative {
    param([string]$Exe, [string[]]$ArgList)
    $ErrorActionPreference = 'Continue'
    $out = & $Exe @ArgList 2>&1
    foreach ($o in @($out)) { [string]$o }
}

function Get-EbdFileInfo {
    param([string]$Path)
    $f = Get-Item -LiteralPath $Path -Force
    $fv = $null
    $pv = $null
    try { $fv = $f.VersionInfo.FileVersion; $pv = $f.VersionInfo.ProductVersion } catch { }
    $mod = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
    return ('{0}  size={1}  modified={2}  fileversion={3}  productversion={4}' -f $f.FullName, $f.Length, $mod, $fv, $pv)
}

function Get-EbdPeMachine {
    param([string]$Path)
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $buf = New-Object byte[] 4096
    $n = 0
    try { $n = $fs.Read($buf, 0, 4096) } finally { $fs.Close() }
    if ($n -lt 64 -or $buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { return 'not a PE file (no MZ header)' }
    $off = [BitConverter]::ToInt32($buf, 0x3C)
    if ($off -lt 0 -or ($off + 6) -gt $n) { return 'PE header not within the first 4 KB' }
    if ($buf[$off] -ne 0x50 -or $buf[$off + 1] -ne 0x45 -or $buf[$off + 2] -ne 0 -or $buf[$off + 3] -ne 0) {
        return 'bad PE signature'
    }
    $m = [int][BitConverter]::ToUInt16($buf, $off + 4)
    $desc = 'unknown machine'
    if ($m -eq 0x014C) { $desc = 'x86 - 32-bit' }
    elseif ($m -eq 0x8664) { $desc = 'x64 - 64-bit' }
    elseif ($m -eq 0xAA64) { $desc = 'ARM64 - 64-bit' }
    elseif ($m -eq 0x01C4) { $desc = 'ARM Thumb-2 - 32-bit' }
    return ('machine=0x{0:X4} ({1})' -f $m, $desc)
}

function Test-EbdSecretKey {
    param([string]$Line)
    return ($Line -match '^\s*[^;=\[\]]*(password|passwd|pwd|secret|token|credential|private|key)[^=]*=')
}

function Out-EbdIniText {
    param([string]$Path)
    Out-EbdLine ''
    Out-EbdLine ('----- BEGIN ' + $Path + ' -----')
    $lines = [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::Default)
    foreach ($l in $lines) {
        if (Test-EbdSecretKey $l) {
            $k = ($l -split '=', 2)[0].Trim()
            Out-EbdLine ($k + ' = <redacted by discover.ps1>')
        } else {
            Out-EbdLine $l
        }
    }
    Out-EbdLine ('----- END ' + $Path + ' -----')
}

function Get-EbdIniValue {
    param([string[]]$Lines, [string]$Key)
    foreach ($l in $Lines) {
        $t = $l.Trim()
        if ($t.StartsWith(';') -or $t.StartsWith('#')) { continue }
        $parts = $t -split '=', 2
        if ($parts.Count -eq 2 -and $parts[0].Trim() -ieq $Key) {
            $v = $parts[1]
            $semi = $v.IndexOf(';')
            if ($semi -ge 0) { $v = $v.Substring(0, $semi) }
            return $v.Trim()
        }
    }
    return $null
}

function Out-EbdRecentFiles {
    param([string]$Folder, [int]$Count = 20)
    if (-not $Folder) { return }
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) {
        Out-EbdLine ('  ' + $Folder + ' : not found')
        return
    }
    $files = @(Get-ChildItem -LiteralPath $Folder -Recurse -Depth 3 -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First $Count)
    Out-EbdLine ('  ' + $Folder + ' : ' + $files.Count + ' most recent file(s)')
    foreach ($f in $files) {
        Out-EbdLine ('    {0}  {1,10}  {2}' -f $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), $f.Length, $f.FullName)
    }
}

Write-Host 'ediabas-bridge discovery (read-only) starting...'
$EbdStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# --------------------------------------------------------------------------- server URL
try {
    if ($base) {
        $EbdState.Base = ([string]$base).Trim().TrimEnd('/')
    } else {
        $gw = $null
        try {
            $gw = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 -ErrorAction Stop |
                Where-Object { $_.NextHop -and $_.NextHop -ne '0.0.0.0' } |
                Sort-Object RouteMetric | Select-Object -First 1 -ExpandProperty NextHop
        } catch { }
        if (-not $gw) {
            $cfg = Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = True' -ErrorAction SilentlyContinue |
                Where-Object { $_.DefaultIPGateway } | Select-Object -First 1
            if ($cfg) { $gw = @($cfg.DefaultIPGateway | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' })[0] }
        }
        if ($gw) { $EbdState.Base = 'http://' + $gw + ':8000' }
    }
} catch { }

Out-EbdLine 'ediabas-bridge discovery report'
Out-EbdLine ('discover.ps1 version: ' + $EbdVersion)
Out-EbdLine ('generated (local):    ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))
Out-EbdLine ('generated (UTC):      ' + (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))
Out-EbdLine ('server ($base):       ' + $EbdState.Base)

# --------------------------------------------------------------------------- sections

Invoke-EbdSection 'User, session and elevation' {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($id)
    $EbdState.Elevated = $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
    Out-EbdLine ('User name:         ' + $id.Name)
    Out-EbdLine ('USERNAME/DOMAIN:   ' + $env:USERNAME + ' / ' + $env:USERDOMAIN)
    Out-EbdLine ('Computer name:     ' + $env:COMPUTERNAME)
    Out-EbdLine ('Session id:        ' + (Get-Process -Id $PID).SessionId)
    Out-EbdLine ('Elevated now:      ' + $EbdState.Elevated)
    $adminLine = $null
    try {
        $groups = Invoke-EbdNative 'whoami.exe' @('/groups', '/fo', 'csv')
        $adminLine = @($groups | Where-Object { $_ -match 'S-1-5-32-544' })[0]
    } catch { }
    if ($adminLine) {
        Out-EbdLine ('Administrators:    member (whoami: ' + $adminLine + ')')
    } else {
        Out-EbdLine 'Administrators:    not a member (S-1-5-32-544 not in token groups)'
    }
    try {
        $uac = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Stop
        Out-EbdLine ('UAC EnableLUA:     ' + $uac.EnableLUA + '   ConsentPromptBehaviorAdmin: ' + $uac.ConsentPromptBehaviorAdmin)
    } catch { Out-EbdLine ('UAC settings:      unreadable: ' + $_.Exception.Message) }
}

Invoke-EbdSection 'PowerShell and .NET' {
    Out-EbdLine ('PowerShell version: ' + $PSVersionTable.PSVersion + '  edition: ' + $PSVersionTable.PSEdition + '  CLR: ' + $PSVersionTable.CLRVersion)
    Out-EbdLine ('64-bit process:     ' + [Environment]::Is64BitProcess + '   64-bit OS: ' + [Environment]::Is64BitOperatingSystem)
    Out-EbdLine ('PowerShell host:    ' + (Get-Process -Id $PID).Path)
    try {
        $ndp = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
        Out-EbdLine ('.NET Framework 4:   version ' + $ndp.Version + '  release ' + $ndp.Release)
    } catch { Out-EbdLine '.NET Framework 4:   not found in registry' }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        Out-EbdLine ('dotnet:             ' + $dotnet.Source)
        Out-EbdLine 'dotnet --list-runtimes:'
        Out-EbdText (Invoke-EbdNative $dotnet.Source @('--list-runtimes'))
    } else {
        Out-EbdLine 'dotnet:             not on PATH (no .NET Core/5+ host)'
    }
}

Invoke-EbdSection 'Operating system' {
    $os = Get-CimInstance Win32_OperatingSystem
    Out-EbdLine ('Caption:            ' + $os.Caption)
    Out-EbdLine ('Version / build:    ' + $os.Version + ' / ' + $os.BuildNumber)
    Out-EbdLine ('OSArchitecture:     ' + $os.OSArchitecture)
    try {
        $cv = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        Out-EbdLine ('DisplayVersion:     ' + $cv.DisplayVersion + '  UBR: ' + $cv.UBR + '  EditionID: ' + $cv.EditionID)
    } catch { }
    Out-EbdLine ('PROCESSOR_ARCHITECTURE (this process): ' + $env:PROCESSOR_ARCHITECTURE + '  PROCESSOR_ARCHITEW6432: ' + $env:PROCESSOR_ARCHITEW6432)
    try {
        $na = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -ErrorAction Stop).PROCESSOR_ARCHITECTURE
        Out-EbdLine ('Native PROCESSOR_ARCHITECTURE (registry): ' + $na)
    } catch { }
    try {
        Out-EbdLine ('RuntimeInformation.OSArchitecture: ' + [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)
    } catch { }
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    Out-EbdLine ('CPU:                ' + $cpu.Name + '  cores: ' + $cpu.NumberOfLogicalProcessors)
    $cs = Get-CimInstance Win32_ComputerSystem
    Out-EbdLine ('Model:              ' + $cs.Manufacturer + ' / ' + $cs.Model + '  RAM MB: ' + [int]($cs.TotalPhysicalMemory / 1MB))
    $envPath = @($env:PATH -split ';' | Where-Object { $_ -match 'ediabas|inpa|ncs|nfs|ec-apps' })
    Out-EbdLine ('PATH entries mentioning BMW tools: ' + ($envPath -join ' ; '))
    if ($env:EDIABAS) { Out-EbdLine ('EDIABAS environment variable: ' + $env:EDIABAS) }
}

Invoke-EbdSection 'Network adapters' {
    $profiles = @{}
    try {
        foreach ($p in @(Get-NetConnectionProfile -ErrorAction Stop)) { $profiles[[int]$p.InterfaceIndex] = $p }
    } catch { Out-EbdLine ('Get-NetConnectionProfile failed: ' + $_.Exception.Message) }
    foreach ($c in @(Get-NetIPConfiguration -ErrorAction Stop)) {
        $ips = @($c.IPv4Address | ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength }) -join ', '
        $gws = @($c.IPv4DefaultGateway | ForEach-Object { $_.NextHop }) -join ', '
        $cat = ''
        $prof = $profiles[[int]$c.InterfaceIndex]
        if ($prof) { $cat = [string]$prof.NetworkCategory + ' (' + $prof.Name + ')' }
        $desc = ''
        if ($c.NetAdapter) { $desc = $c.NetAdapter.InterfaceDescription + ', status ' + $c.NetAdapter.Status }
        Out-EbdLine ('Adapter: ' + $c.InterfaceAlias + '  [index ' + $c.InterfaceIndex + ']  ' + $desc)
        Out-EbdLine ('  IPv4:            ' + $ips)
        Out-EbdLine ('  Default gateway: ' + $gws)
        Out-EbdLine ('  Network category: ' + $cat)
    }
    Out-EbdLine ''
    Out-EbdLine 'IPv4 default routes:'
    Out-EbdText (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Select-Object InterfaceAlias, NextHop, RouteMetric, InterfaceMetric | Format-Table -AutoSize)
}

Invoke-EbdSection 'BMW file search' {
    $wanted = @{}
    foreach ($n in @('api32.dll', 'api64.dll', 'api.h', 'ediabas.ini', 'obd.ini', 'inpaload.exe',
                     'tool32.exe', 'ncsexper.exe', 'winkfpt.exe', 'winkfp.exe', 'istagui.exe',
                     'ebas32.exe', 'ediabas.dll')) {
        $wanted[$n] = $true
    }
    $patterns = @('*ncs*dummy*.exe', '*coding*tool*.exe', '*bmwcoding*.exe')
    $roots = @(
        @{ Path = 'C:\EDIABAS'; Depth = 8 },
        @{ Path = 'C:\INPA'; Depth = 8 },
        @{ Path = 'C:\EC-APPS'; Depth = 8 },
        @{ Path = 'C:\NCSEXPER'; Depth = 8 },
        @{ Path = 'C:\NFS'; Depth = 8 },
        @{ Path = 'C:\Rheingold'; Depth = 3 },
        @{ Path = 'C:\ProgramData\BMW'; Depth = 3 },
        @{ Path = 'C:\Program Files'; Depth = 4 },
        @{ Path = 'C:\Program Files (x86)'; Depth = 4 },
        @{ Path = 'C:\Program Files (Arm)'; Depth = 4 }
    )
    $seen = @{}
    foreach ($r in $roots) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        if (-not (Test-Path -LiteralPath $r.Path -PathType Container)) {
            Out-EbdLine ('Root ' + $r.Path + ': not present')
            continue
        }
        $errs = $null
        $hits = 0
        Get-ChildItem -LiteralPath $r.Path -Recurse -Depth $r.Depth -File -ErrorAction SilentlyContinue -ErrorVariable errs |
            ForEach-Object {
                $n = $_.Name.ToLowerInvariant()
                $match = $wanted.ContainsKey($n)
                if (-not $match) {
                    foreach ($p in $patterns) { if ($n -like $p) { $match = $true; break } }
                }
                if ($match -and -not $seen.ContainsKey($_.FullName)) {
                    $seen[$_.FullName] = $true
                    [void]$EbdState.Found.Add($_.FullName)
                    $hits++
                }
            }
        $denied = @($errs).Count
        Out-EbdLine ('Root {0}: depth {1}, {2} match(es), {3} unreadable item(s), {4:N1}s' -f $r.Path, $r.Depth, $hits, $denied, $sw.Elapsed.TotalSeconds)
        if ($denied -gt 0 -and -not $EbdState.Elevated) {
            Add-EbdLimited ($r.Path + ': ' + $denied + ' folder(s) could not be read')
        }
    }
    foreach ($extra in @('C:\Windows\EDIABAS.INI', 'C:\Windows\obd.ini', 'C:\Windows\System32\api32.dll', 'C:\Windows\SysWOW64\api32.dll')) {
        if ((Test-Path -LiteralPath $extra -PathType Leaf) -and -not $seen.ContainsKey($extra)) {
            $seen[$extra] = $true
            [void]$EbdState.Found.Add($extra)
        }
    }
    Out-EbdLine ''
    Out-EbdLine 'Files found:'
    foreach ($f in ($EbdState.Found | Sort-Object)) {
        try { Out-EbdLine ('  ' + (Get-EbdFileInfo $f)) } catch { Out-EbdLine ('  ' + $f + '  (unreadable: ' + $_.Exception.Message + ')') }
    }
    $wantedReport = @('api32.dll', 'api.h', 'ediabas.ini', 'obd.ini', 'inpaload.exe', 'tool32.exe',
                      'ncsexper.exe', 'winkfpt.exe', 'istagui.exe', 'ebas32.exe')
    Out-EbdLine ''
    Out-EbdLine 'Not found:'
    foreach ($w in $wantedReport) {
        $any = @($EbdState.Found | Where-Object { [System.IO.Path]::GetFileName($_) -ieq $w })
        if ($any.Count -eq 0) { Out-EbdLine ('  ' + $w) }
    }
    $nd = @($EbdState.Found | Where-Object { [System.IO.Path]::GetFileName($_) -like '*ncs*dummy*.exe' })
    if ($nd.Count -eq 0) { Out-EbdLine '  NCS Dummy (*ncs*dummy*.exe)' }
    $ct = @($EbdState.Found | Where-Object { $n = [System.IO.Path]::GetFileName($_); ($n -like '*coding*tool*.exe') -or ($n -like '*bmwcoding*.exe') })
    if ($ct.Count -eq 0) { Out-EbdLine '  BMW Coding Tool (*coding*tool*.exe, *bmwcoding*.exe)' }

    $apiH = @($EbdState.Found | Where-Object { [System.IO.Path]::GetFileName($_) -ieq 'api.h' })
    if ($apiH.Count -gt 0) { $EbdState.ApiH = $apiH[0] }

    Out-EbdLine ''
    Out-EbdLine 'ISTA:'
    $istaExe = @($EbdState.Found | Where-Object { [System.IO.Path]::GetFileName($_) -ieq 'istagui.exe' })
    Out-EbdLine ('  ISTAGUI.exe found:        ' + ($istaExe.Count -gt 0))
    Out-EbdLine ('  C:\Rheingold exists:      ' + (Test-Path -LiteralPath 'C:\Rheingold'))
    Out-EbdLine ('  C:\ProgramData\BMW exists: ' + (Test-Path -LiteralPath 'C:\ProgramData\BMW'))
}

Invoke-EbdSection 'api32.dll details' {
    $dlls = @($EbdState.Found | Where-Object { [System.IO.Path]::GetFileName($_) -ieq 'api32.dll' })
    if ($dlls.Count -eq 0) { Out-EbdLine 'api32.dll not found.'; return }
    foreach ($d in $dlls) {
        Out-EbdLine $d
        try {
            $fi = Get-Item -LiteralPath $d -Force
            Out-EbdLine ('  size:         ' + $fi.Length)
            Out-EbdLine ('  file version: ' + $fi.VersionInfo.FileVersion + '   product version: ' + $fi.VersionInfo.ProductVersion)
            Out-EbdLine ('  description:  ' + $fi.VersionInfo.FileDescription + '   company: ' + $fi.VersionInfo.CompanyName)
            $pe = Get-EbdPeMachine $d
            Out-EbdLine ('  PE:           ' + $pe + '   32-bit: ' + ($pe -like '*32-bit*'))
        } catch { Out-EbdLine ('  error: ' + $_.Exception.Message) }
    }
}

Invoke-EbdSection 'EDIABAS.INI and obd.ini' {
    $inis = @($EbdState.Found | Where-Object { $n = [System.IO.Path]::GetFileName($_); ($n -ieq 'ediabas.ini') -or ($n -ieq 'obd.ini') })
    if ($inis.Count -eq 0) { Out-EbdLine 'Neither EDIABAS.INI nor obd.ini was found.'; return }
    $keys = @('Interface', 'EcuPath', 'TracePath', 'ApiTrace', 'IfhTrace', 'Simulation', 'SimulationPath', 'LoadWin32')
    foreach ($ini in $inis) {
        if ([System.IO.Path]::GetFileName($ini) -ieq 'ediabas.ini') {
            Out-EbdLine ('Key settings in ' + $ini + ':')
            try {
                $lines = [System.IO.File]::ReadAllLines($ini, [System.Text.Encoding]::Default)
                foreach ($k in $keys) {
                    $v = Get-EbdIniValue $lines $k
                    Out-EbdLine ('  {0,-15} = {1}' -f $k, $v)
                    if ($k -eq 'EcuPath' -and $v -and -not $EbdState.EcuPath) { $EbdState.EcuPath = $v }
                    if ($k -eq 'TracePath' -and $v -and -not $EbdState.TracePath) { $EbdState.TracePath = $v }
                }
            } catch { Out-EbdLine ('  unreadable: ' + $_.Exception.Message) }
        }
    }
    foreach ($ini in $inis) {
        try { Out-EbdIniText $ini } catch { Out-EbdLine ('Could not read ' + $ini + ': ' + $_.Exception.Message) }
    }
}

Invoke-EbdSection 'SGBD files (Ecu folder)' {
    $ecu = $EbdState.EcuPath
    if (-not $ecu) { $ecu = 'C:\EDIABAS\Ecu'; Out-EbdLine 'EcuPath not set in EDIABAS.INI; using the default.' }
    Out-EbdLine ('Ecu folder: ' + $ecu)
    if (-not (Test-Path -LiteralPath $ecu -PathType Container)) { Out-EbdLine '  folder not found'; return }
    foreach ($n in @('TMODE.PRG', 'UTILITY.PRG', 'NVE_65.PRG', 'N62_TUE.PRG', 'ME9N62_2.PRG', 'D_MOTOR.GRP')) {
        $p = Join-Path $ecu $n
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $fi = Get-Item -LiteralPath $p -Force
            Out-EbdLine ('  {0,-14} present  size={1}  modified={2}' -f $n, $fi.Length, $fi.LastWriteTime.ToString('yyyy-MM-dd'))
        } else {
            Out-EbdLine ('  {0,-14} MISSING' -f $n)
        }
    }
    $all = @(Get-ChildItem -LiteralPath $ecu -File -ErrorAction SilentlyContinue)
    $prg = @($all | Where-Object { $_.Extension -ieq '.prg' }).Count
    $grp = @($all | Where-Object { $_.Extension -ieq '.grp' }).Count
    Out-EbdLine ('  .PRG files: ' + $prg + '   .GRP files: ' + $grp + '   all files: ' + $all.Count)
}

Invoke-EbdSection 'Desktop shortcuts, run-as-admin flags and zip files' {
    $desktops = @()
    try { $desktops += [Environment]::GetFolderPath('Desktop') } catch { }
    try { $desktops += [Environment]::GetFolderPath('CommonDesktopDirectory') } catch { }
    $desktops = @($desktops | Where-Object { $_ } | Select-Object -Unique)
    $shell = $null
    try { $shell = New-Object -ComObject WScript.Shell } catch { Out-EbdLine ('WScript.Shell unavailable: ' + $_.Exception.Message) }
    foreach ($dt in $desktops) {
        Out-EbdLine ('Desktop: ' + $dt)
        if (-not (Test-Path -LiteralPath $dt -PathType Container)) { Out-EbdLine '  not found'; continue }
        foreach ($lnk in @(Get-ChildItem -LiteralPath $dt -Filter '*.lnk' -File -Force -ErrorAction SilentlyContinue)) {
            try {
                $target = ''
                $lnkArgs = ''
                $wd = ''
                if ($shell) {
                    $sc = $shell.CreateShortcut($lnk.FullName)
                    $target = $sc.TargetPath
                    $lnkArgs = $sc.Arguments
                    $wd = $sc.WorkingDirectory
                }
                $runAs = 'unknown'
                $fs = [System.IO.File]::Open($lnk.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $hdr = New-Object byte[] 32
                $got = 0
                try { $got = $fs.Read($hdr, 0, 32) } finally { $fs.Close() }
                if ($got -ge 0x16 -and $hdr[0] -eq 0x4C) { $runAs = [string](($hdr[0x15] -band 0x20) -ne 0) }
                Out-EbdLine ('  ' + $lnk.Name)
                Out-EbdLine ('    target:    ' + $target)
                Out-EbdLine ('    arguments: ' + $lnkArgs)
                Out-EbdLine ('    start in:  ' + $wd)
                Out-EbdLine ('    run as administrator flag: ' + $runAs)
            } catch { Out-EbdLine ('  ' + $lnk.Name + ': error ' + $_.Exception.Message) }
        }
        foreach ($z in @(Get-ChildItem -LiteralPath $dt -Filter '*.zip' -File -Force -ErrorAction SilentlyContinue)) {
            Out-EbdLine ('  zip: ' + $z.Name + '  size=' + $z.Length)
        }
    }
    Out-EbdLine ''
    Out-EbdLine 'AppCompatFlags\Layers entries (RUNASADMIN marked with *):'
    foreach ($key in @('HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
                       'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers')) {
        if (-not (Test-Path -LiteralPath $key)) { Out-EbdLine ('  ' + $key + ': not present'); continue }
        $k = Get-Item -LiteralPath $key
        $names = @($k.GetValueNames())
        Out-EbdLine ('  ' + $key + ': ' + $names.Count + ' value(s)')
        foreach ($n in $names) {
            $v = [string]$k.GetValue($n)
            $mark = ' '
            if ($v -match 'RUNASADMIN') { $mark = '*' }
            Out-EbdLine ('   ' + $mark + ' ' + $n + ' = ' + $v)
        }
    }
}

Invoke-EbdSection 'COM ports and USB serial devices' {
    Out-EbdLine ('SerialPort.GetPortNames: ' + (([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object) -join ', '))
    try {
        $sc = Get-Item -LiteralPath 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM' -ErrorAction Stop
        Out-EbdLine 'HKLM\HARDWARE\DEVICEMAP\SERIALCOMM:'
        foreach ($n in @($sc.GetValueNames())) { Out-EbdLine ('  ' + $n + ' = ' + $sc.GetValue($n)) }
    } catch { Out-EbdLine 'HKLM\HARDWARE\DEVICEMAP\SERIALCOMM: not present (no serial ports right now)' }
    Out-EbdLine ''
    Out-EbdLine 'Ports class and USB/FTDI devices (Win32_PnPEntity):'
    $dev = @(Get-CimInstance Win32_PnPEntity | Where-Object {
        ($_.PNPClass -eq 'Ports') -or ($_.PNPDeviceID -like 'FTDIBUS\*') -or ($_.PNPDeviceID -like 'USB\*') -or ($_.Name -match 'COM\d+|serial|FTDI|USB.*UART')
    })
    foreach ($d in $dev) {
        Out-EbdLine ('  ' + $d.Name)
        Out-EbdLine ('    class=' + $d.PNPClass + '  status=' + $d.Status + '  error=' + $d.ConfigManagerErrorCode + '  manufacturer=' + $d.Manufacturer)
        Out-EbdLine ('    id=' + $d.PNPDeviceID)
    }
    if ($dev.Count -eq 0) { Out-EbdLine '  none' }
}

Invoke-EbdSection 'Running processes' {
    $diagPattern = 'inpa|tool32|ncsexper|ncs.?dummy|winkfp|ebas|ediabas|ista|rheingold|coding|ifh|obd|kmm|nfs'
    $procs = @(Get-Process)
    $diag = @($procs | Where-Object { $_.ProcessName -match $diagPattern })
    Out-EbdLine 'Diagnostic-looking processes:'
    if ($diag.Count -eq 0) { Out-EbdLine '  none' }
    $noPath = 0
    foreach ($p in $diag) {
        $path = $null
        try { $path = $p.Path } catch { }
        if (-not $path) { $noPath++ }
        Out-EbdLine ('  {0}  pid={1}  session={2}  path={3}' -f $p.ProcessName, $p.Id, $p.SessionId, $path)
    }
    Out-EbdLine ''
    Out-EbdLine 'Processes with api32.dll loaded:'
    $withApi = 0
    $unreadable = 0
    foreach ($p in $procs) {
        $mods = $null
        try { $mods = $p.Modules } catch { $unreadable++; continue }
        if ($mods | Where-Object { $_.ModuleName -ieq 'api32.dll' }) {
            Out-EbdLine ('  {0}  pid={1}' -f $p.ProcessName, $p.Id)
            $withApi++
        }
    }
    if ($withApi -eq 0) { Out-EbdLine '  none seen' }
    Out-EbdLine ('  (modules unreadable for ' + $unreadable + ' process(es); 32-bit process modules are only partly visible from 64-bit PowerShell)')
    if (-not $EbdState.Elevated) {
        Add-EbdLimited ('process paths/modules of other users and elevated processes are hidden (' + $unreadable + ' process(es) unreadable, ' + $noPath + ' diagnostic process path(s) missing)')
    }
    Out-EbdLine ''
    Out-EbdLine 'All process names:'
    $names = @($procs | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique)
    Out-EbdLine ('  ' + ($names -join ', '))
}

Invoke-EbdSection 'Recent files in trace and work folders' {
    $folders = New-Object System.Collections.ArrayList
    if ($EbdState.TracePath) { [void]$folders.Add($EbdState.TracePath) }
    foreach ($f in @('C:\EDIABAS\TRACE', 'C:\NCSEXPER\WORK', 'C:\EC-APPS\NFS\WORK', 'C:\NFS\WORK', 'C:\EC-APPS\NFS\LOG')) {
        [void]$folders.Add($f)
    }
    foreach ($w in @($EbdState.Found | Where-Object { $n = [System.IO.Path]::GetFileName($_); ($n -ieq 'winkfpt.exe') -or ($n -ieq 'winkfp.exe') })) {
        $binDir = Split-Path -Parent $w
        $appDir = Split-Path -Parent $binDir
        foreach ($sub in @('WORK', 'LOG', 'TRACE')) { [void]$folders.Add((Join-Path $appDir $sub)) }
    }
    $done = @{}
    foreach ($f in $folders) {
        $key = $f.TrimEnd('\').ToLowerInvariant()
        if ($done.ContainsKey($key)) { continue }
        $done[$key] = $true
        try { Out-EbdRecentFiles $f } catch { Out-EbdLine ('  ' + $f + ': error ' + $_.Exception.Message) }
    }
}

Invoke-EbdSection 'Guest agent services (QEMU / SPICE)' {
    $svc = @(Get-Service -ErrorAction SilentlyContinue | Where-Object {
        ($_.Name -match 'qemu|spice|vdagent|vdservice|virtio|utm') -or ($_.DisplayName -match 'qemu|spice|virtio|utm')
    })
    if ($svc.Count -eq 0) { Out-EbdLine 'No QEMU/SPICE/VirtIO guest services installed.' }
    foreach ($s in $svc) {
        $start = ''
        try { $start = [string]$s.StartType } catch { }
        Out-EbdLine ('  {0}  ({1})  status={2}  start={3}' -f $s.Name, $s.DisplayName, $s.Status, $start)
    }
}

Invoke-EbdSection 'Sleep, screen saver and screen lock' {
    Out-EbdLine 'Active power scheme:'
    Out-EbdText (Invoke-EbdNative 'powercfg.exe' @('/getactivescheme'))
    Out-EbdLine 'Sleep after (STANDBYIDLE):'
    Out-EbdText (Invoke-EbdNative 'powercfg.exe' @('/query', 'SCHEME_CURRENT', 'SUB_SLEEP', 'STANDBYIDLE'))
    Out-EbdLine 'Hibernate after (HIBERNATEIDLE):'
    Out-EbdText (Invoke-EbdNative 'powercfg.exe' @('/query', 'SCHEME_CURRENT', 'SUB_SLEEP', 'HIBERNATEIDLE'))
    Out-EbdLine 'Turn off display after (VIDEOIDLE):'
    Out-EbdText (Invoke-EbdNative 'powercfg.exe' @('/query', 'SCHEME_CURRENT', 'SUB_VIDEO', 'VIDEOIDLE'))
    Out-EbdLine 'Require password on wake (CONSOLELOCK):'
    Out-EbdText (Invoke-EbdNative 'powercfg.exe' @('/query', 'SCHEME_CURRENT', 'SUB_NONE', 'CONSOLELOCK'))
    Out-EbdLine ''
    $ssKeys = @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure', 'SCRNSAVE.EXE', 'DelayLockInterval')
    foreach ($key in @('HKCU:\Control Panel\Desktop', 'HKCU:\Software\Policies\Microsoft\Windows\Control Panel\Desktop')) {
        if (-not (Test-Path -LiteralPath $key)) { Out-EbdLine ($key + ': not present'); continue }
        $k = Get-Item -LiteralPath $key
        $vals = @()
        foreach ($n in $ssKeys) {
            $v = $k.GetValue($n)
            if ($null -ne $v) { $vals += ($n + '=' + $v) }
        }
        Out-EbdLine ($key + ': ' + ($vals -join '  '))
    }
    try {
        $pol = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Stop
        Out-EbdLine ('Machine inactivity limit (InactivityTimeoutSecs): ' + $pol.InactivityTimeoutSecs)
    } catch { }
    $ssConfigured = $false
    try {
        $d = Get-Item -LiteralPath 'HKCU:\Control Panel\Desktop'
        if (([string]$d.GetValue('ScreenSaveActive') -eq '1') -and ([int]$d.GetValue('ScreenSaveTimeOut') -gt 0)) { $ssConfigured = $true }
    } catch { }
    Out-EbdLine ('Screen saver timeout configured: ' + $ssConfigured)
}

Invoke-EbdSection 'Elevation summary' {
    Out-EbdLine ('Script ran elevated: ' + $EbdState.Elevated)
    if ($EbdState.Elevated) {
        Out-EbdLine 'No section was limited by elevation.'
    } elseif ($EbdState.Limited.Count -eq 0) {
        Out-EbdLine 'Not elevated, but no section reported missing data because of it.'
    } else {
        Out-EbdLine 'Sections limited by lack of elevation:'
        foreach ($l in $EbdState.Limited) { Out-EbdLine ('  - ' + $l) }
    }
    Out-EbdLine ('Runtime: {0:N1}s' -f $EbdStopwatch.Elapsed.TotalSeconds)
}

# --------------------------------------------------------------------------- save and upload

$EbdText = ($EbdState.Lines -join "`r`n") + "`r`n"
$EbdUtf8 = New-Object System.Text.UTF8Encoding($false)
$EbdBytes = $EbdUtf8.GetBytes($EbdText)
$EbdReportPath = $null
try {
    $EbdDesktop = [Environment]::GetFolderPath('Desktop')
    $EbdReportPath = Join-Path $EbdDesktop 'ediabas-bridge-discovery.txt'
    [System.IO.File]::WriteAllBytes($EbdReportPath, $EbdBytes)
    Write-Host ('Report saved: ' + $EbdReportPath)
} catch {
    Write-Host ('Could not save the report to the Desktop: ' + $_.Exception.Message) -ForegroundColor Red
    $EbdReportPath = $null
}

function Send-EbdUpload {
    param([string]$Name, [byte[]]$Bytes)
    if (-not $EbdState.Base) {
        Write-Host ('Upload ' + $Name + ': FAILED - no server URL ($base not set and no default gateway found)') -ForegroundColor Red
        return $false
    }
    $ProgressPreference = 'SilentlyContinue'
    try {
        $uri = $EbdState.Base + '/upload/' + $Name
        $r = Invoke-WebRequest -Uri $uri -Method Put -Body $Bytes -ContentType 'application/octet-stream' -UseBasicParsing -TimeoutSec 60
        Write-Host ('Upload ' + $Name + ': OK (HTTP ' + [int]$r.StatusCode + ') ' + $r.Content) -ForegroundColor Green
        return $true
    } catch {
        Write-Host ('Upload ' + $Name + ': FAILED - ' + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
}

Write-Host ''
$EbdOk = Send-EbdUpload 'discovery.txt' $EbdBytes
if ($EbdState.ApiH) {
    try {
        $EbdApiBytes = [System.IO.File]::ReadAllBytes($EbdState.ApiH)
        [void](Send-EbdUpload 'api.h' $EbdApiBytes)
    } catch {
        Write-Host ('Upload api.h: FAILED - could not read ' + $EbdState.ApiH + ': ' + $_.Exception.Message) -ForegroundColor Red
    }
} else {
    Write-Host 'api.h: not found, nothing to upload.'
}
if (-not $EbdOk) {
    if ($EbdReportPath) {
        Write-Host ('The report was not uploaded. It is on the Desktop: ' + $EbdReportPath) -ForegroundColor Yellow
    } else {
        Write-Host 'The report could be neither uploaded nor saved.' -ForegroundColor Red
    }
}
Write-Host ('ediabas-bridge discovery finished in {0:N1}s.' -f $EbdStopwatch.Elapsed.TotalSeconds)
