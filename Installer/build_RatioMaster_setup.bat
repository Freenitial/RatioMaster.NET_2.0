<# :
    @echo off & Title RatioMaster.NET Packing
    set "RM_BAT_ARGS=%*"

    for /f "tokens=2 delims=[]" %%v in ('ver') do for /f "tokens=2,3 delims=. " %%m in ("%%v") do (set "WINMAJOR=%%m" & set "WINMINOR=%%n")
    if not defined WINMAJOR set "WINMAJOR=0"
    if not defined WINMINOR set "WINMINOR=0"
    if %WINMAJOR% GTR 6 goto :winVersionOk
    if %WINMAJOR% EQU 6 if %WINMINOR% GEQ 1 goto :winVersionOk
    echo. & echo  [ERROR] This tool requires Windows 7 or later. & echo.
    pause
    exit /b 1
    :winVersionOk

    REM ── Elevation gate (foreground UAC, ONLY if needed) ──────────────────────────
    REM Relaunch elevated only when something needs admin: an output folder isn't writable, OR the
    REM .NET 11 SDK / MSVC x64 (win-x64 AOT) / MSVC ARM64 (win-arm64 AOT) / .NET Android workload
    REM (apk/aab) are missing — phases 0+4 install them. The Android JDK + SDK are self-acquired by
    REM phase 4 to user paths (no admin). (No installer to build — every artifact is portable.)
    powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
        "$ErrorActionPreference='SilentlyContinue';" ^
        "$bat='%~f0'; $root=Split-Path -Parent (Split-Path -Parent $bat);" ^
        "if((New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)){exit 0};" ^
        "$reasons=@();" ^
        "$aa=[string]$env:RM_BAT_ARGS; $sAot=$aa -match '(?i)--aot\b'; $sWx=$aa -match '(?i)--winx64\b'; $sWa=$aa -match '(?i)--winarm64\b'; $sLx=$aa -match '(?i)--linux\b'; $sAk=$aa -match '(?i)--apk\b'; $sAb=$aa -match '(?i)--aab\b'; $any=$sAot -or $sWx -or $sWa -or $sLx -or $sAk -or $sAb; $needWin=(-not $any) -or $sAot -or $sWx -or $sWa; $needArm=(-not $any) -or $sWa; $needAndroid=(-not $any) -or $sAk -or $sAb;" ^
        "foreach($d in @((Join-Path $root 'RatioMaster.App\publish'),(Split-Path -Parent $bat),(Join-Path (Split-Path -Parent $bat) 'Output'),(Join-Path (Split-Path -Parent $bat) 'Data'))){try{New-Item -ItemType Directory -Force -Path $d | Out-Null; $t=Join-Path $d ('.w_'+[guid]::NewGuid().ToString('N')); [IO.File]::WriteAllText($t,'x'); Remove-Item $t -Force}catch{$reasons+=('output folder not writable: '+$d)}};" ^
        "$net11=$false; try{$net11=((dotnet --list-sdks 2>&1 | Out-String) -match '(?im)^\s*11\.0\.')}catch{}; if(-not $net11){$reasons+='.NET 11 SDK not installed'};" ^
        "$vcx64=[bool](Get-ChildItem @((Join-Path $env:ProgramFiles 'Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe')) -EA SilentlyContinue); $winsdk=[bool](Get-ChildItem @((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Lib\*\um\x64\kernel32.lib'),(Join-Path $env:ProgramFiles 'Windows Kits\10\Lib\*\um\x64\kernel32.lib')) -EA SilentlyContinue); if($needWin -and -not ($vcx64 -and $winsdk)){$reasons+='MSVC x64 C++ build tools / Windows SDK not installed (REQUIRED for the win-x64 Native-AOT single file)'};" ^
        "$vcarm=[bool](Get-ChildItem @((Join-Path $env:ProgramFiles 'Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\arm64\link.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\arm64\link.exe')) -EA SilentlyContinue); if($needArm -and -not $vcarm){$reasons+='MSVC ARM64 build tools not installed (for the win-arm64 AOT)'};" ^
        "$wl=$false; try{$wl=((dotnet workload list 2>&1 | Out-String) -match '(?im)^\s*android\b')}catch{}; if($needAndroid -and -not $wl){$reasons+='.NET Android workload not installed (for the apk/aab)'};" ^
        "if($reasons.Count -eq 0){exit 0};" ^
        "$why='[setup] Administrator rights needed -- relaunching elevated. Reason(s): '+($reasons -join '; ');" ^
        "Write-Host $why;" ^
        "try{ Start-Process -FilePath $bat -ArgumentList ('%RM_BAT_ARGS% --rm-elevated').Trim() -Verb RunAs; exit 3 }catch{ Write-Host '[setup] Elevation declined -- continuing without admin (best-effort).'; exit 0 }"
    if "%ERRORLEVEL%"=="3" exit /b

    powershell -NoLogo -NoProfile -executionpolicy bypass -STA -Command ^
        "$M=[Runtime.InteropServices.Marshal];" ^
        "$d=[AppDomain]::CurrentDomain.DefineDynamicAssembly(" ^
        "(New-Object Reflection.AssemblyName('W')),'Run').DefineDynamicModule('W');" ^
        "$t=$d.DefineType('A','Public,Class');" ^
        "$z=$t.DefinePInvokeMethod('CreateWindowExW','user32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([IntPtr])," ^
        "@([Int32],[String],[String],[Int32],[Int32],[Int32],[Int32],[Int32]," ^
        "[IntPtr],[IntPtr],[IntPtr],[IntPtr]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$z=$t.DefinePInvokeMethod('ShowWindow','user32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([Bool])," ^
        "@([IntPtr],[Int32]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$z=$t.DefinePInvokeMethod('GetSystemMetrics','user32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([Int32])," ^
        "@([Int32]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$z=$t.DefinePInvokeMethod('SendMessageW','user32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([IntPtr])," ^
        "@([IntPtr],[UInt32],[IntPtr],[IntPtr]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$z=$t.DefinePInvokeMethod('GetStockObject','gdi32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([IntPtr])," ^
        "@([Int32]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$z=$t.DefinePInvokeMethod('InitCommonControlsEx','comctl32.dll'," ^
        "'Public,Static,PinvokeImpl','Standard',([Bool])," ^
        "@([IntPtr]),'Winapi','Unicode');" ^
        "$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128);" ^
        "$A=$t.CreateType();" ^
        "$sw=$A::GetSystemMetrics(0);$sh=$A::GetSystemMetrics(1);" ^
        "$hw=$A::CreateWindowExW(9,'#32770','RatioMaster.NET Packing',0x10C00000," ^
        "[int](($sw-440)/2),[int](($sh-130)/2),440,130," ^
        "[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero);" ^
        "$null=$A::ShowWindow($hw,5);" ^
        "$pc=$M::AllocHGlobal(8);$M::WriteInt32($pc,0,8);$M::WriteInt32($pc,4,0x20);" ^
        "$null=$A::InitCommonControlsEx($pc);$M::FreeHGlobal($pc);" ^
        "$ft=$A::GetStockObject(17);" ^
        "$hl=$A::CreateWindowExW(0,'Static','Initializing...',0x50000000," ^
        "20,15,390,20,$hw,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero);" ^
        "$null=$A::SendMessageW($hl,0x30,$ft,[IntPtr]::Zero);" ^
        "$hb=$A::CreateWindowExW(0,'msctls_progress32','',0x50000000," ^
        "20,42,390,24,$hw,[IntPtr]::Zero,[IntPtr]::Zero,[IntPtr]::Zero);" ^
        "$batFile='%~f0';& ([ScriptBlock]::Create([IO.File]::ReadAllText('%~f0')))"
    exit /b
#>

# ══════════════════════ RatioMaster.NET portable-build packer ══════════════════════
# Produces PORTABLE artifacts (no installer): win-x64 single .exe, win-arm64 .zip, Linux .AppImage.

$t=$d.DefineType('E','Public,Class')
foreach($x in @(
    ,@('SetWindowTextW','user32.dll',([Bool]),@([IntPtr],[String]))
    ,@('DestroyWindow','user32.dll',([Bool]),@([IntPtr]))
    ,@('PeekMessageW','user32.dll',([Bool]),@([IntPtr],[IntPtr],[UInt32],[UInt32],[UInt32]))
    ,@('TranslateMessage','user32.dll',([Bool]),@([IntPtr]))
    ,@('DispatchMessageW','user32.dll',([IntPtr]),@([IntPtr]))
)){$z=$t.DefinePInvokeMethod($x[0],$x[1],'Public,Static,PinvokeImpl','Standard',$x[2],$x[3],'Winapi','Unicode');$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128)}
$E=$t.CreateType()

$script:BuildMutex = $null
try {
    $created = $false
    $script:BuildMutex = New-Object System.Threading.Mutex($true, 'Local\RatioMaster_build_setup', [ref]$created)
    if (-not $created) {
        $owned = $false
        try { $owned = $script:BuildMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { try { $null = $E::DestroyWindow($hw) } catch {}; Write-Host '[setup] Another RatioMaster build is already running - exiting.'; exit 1 }
    }
} catch { $script:BuildMutex = $null }
$mg=$M::AllocHGlobal(48)

$buildLog = $null
try {
    $buildLog = Join-Path (Split-Path -Parent $batFile) 'build-ratiomaster.log'
    if ($env:RM_BAT_ARGS -match '(?i)--rm-elevated') { [IO.File]::AppendAllText($buildLog, "---- relaunched elevated; continuing ----`r`n") }
    else { [IO.File]::WriteAllText($buildLog, "==== RatioMaster.NET build $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) ====`r`n") }
} catch { $buildLog = $null }
function Log([string]$m) { if ($null -eq $m) { return }; Write-Host $m; if ($buildLog) { try { [IO.File]::AppendAllText($buildLog, $m + "`r`n") } catch {} } }
trap { try { Log ("[ERROR] " + $_.Exception.Message) } catch {}; try { if ($_.InvocationInfo) { Log ("        at " + $_.InvocationInfo.PositionMessage) } } catch {} }

$script:LastPopupMsg = $null; $script:LastPct = 0
function Invoke-LoadingPump{try{while($E::PeekMessageW($mg,[IntPtr]::Zero,0,0,1)){$null=$E::TranslateMessage($mg);$null=$E::DispatchMessageW($mg)}}catch{}}
function Update-LoadingPopup([int]$pct,[string]$s){if($pct -lt $script:LastPct){$pct=$script:LastPct}else{$script:LastPct=$pct};$null=$A::SendMessageW($hb,0x402,[IntPtr]$pct,[IntPtr]::Zero);if($s){$null=$E::SetWindowTextW($hl,$s); if($s -ne $script:LastPopupMsg){Log ("[{0,3}%] {1}" -f $pct,$s); $script:LastPopupMsg=$s}};try{Invoke-LoadingPump}catch{}}
function Close-LoadingPopup{try{$null=$E::DestroyWindow($hw)}catch{};try{Invoke-LoadingPump}catch{};try{$M::FreeHGlobal($mg)}catch{}}
Update-LoadingPopup 5 "Loading assemblies..."

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Fail([string]$msg) {
    try { Update-LoadingPopup 100 "Failed" } catch {}
    try { Close-LoadingPopup } catch {}
    [System.Windows.Forms.MessageBox]::Show($msg, 'RatioMaster.NET Packing - failed', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
$script:AnsiEnc = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage)
function Read-TextRobust([string]$path) {
    try { $b = [IO.File]::ReadAllBytes($path) } catch { return '' }
    if ($null -eq $b -or $b.Length -eq 0) { return '' }
    if ($b.Length -eq 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return '' }
    if ($b.Length -gt 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $b = $b[3..($b.Length - 1)] }
    try { return (New-Object System.Text.UTF8Encoding($false, $true)).GetString($b) } catch { return $script:AnsiEnc.GetString($b) }
}
function Invoke-Stage([string]$exe,[object[]]$argList,[int]$from,[int]$to,[string]$msg,[string]$log,[int]$timeoutSec = 0) {
    Update-LoadingPopup $from $msg
    $q = ($argList | ForEach-Object { if ([string]$_ -match '\s') { '"' + $_ + '"' } else { [string]$_ } }) -join ' '
    $inner = '"' + $exe + '" ' + $q + ' > "' + $log + '" 2>&1'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec; $psi.Arguments = '/c "' + $inner + '"'; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $p = $from; $i = 0
    $deadline = if ($timeoutSec -gt 0) { [DateTime]::UtcNow.AddSeconds($timeoutSec) } else { [DateTime]::MaxValue }
    $timedOut = $false
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 300; $i++
        if (($i % 7) -eq 0 -and $p -lt ($to - 1)) { $p++ }
        Update-LoadingPopup $p $msg
        if ([DateTime]::UtcNow -gt $deadline) { $timedOut = $true; try { & taskkill /PID $proc.Id /T /F *> $null } catch {}; try { if (-not $proc.HasExited) { $proc.Kill() } } catch {}; break }
    }
    $proc.WaitForExit()
    try { $stageOut = Read-TextRobust $log; if ($stageOut) { Log $stageOut.TrimEnd() } } catch {}
    Update-LoadingPopup $to $msg
    if ($timedOut) { Log ("[timeout] stage exceeded ${timeoutSec}s and was terminated: $msg"); return 1460 }
    return $proc.ExitCode
}
function Tail([string]$log,[int]$n = 25) { $t = Read-TextRobust $log; if (-not $t) { return '' }; (($t -split "`r?`n") | Select-Object -Last $n) -join "`r`n" }

# ── layout: <repo>\Installer\{build_RatioMaster_setup.bat, Data\, Output\} ; repo root = one up ──
$root    = Split-Path -Parent (Split-Path -Parent $batFile)
$batDir  = Split-Path -Parent $batFile
$dataDir = Join-Path $batDir 'Data'
$outDir  = Join-Path $batDir 'Output'
$appDir  = Join-Path $root 'RatioMaster.App'
$proj    = Join-Path $appDir 'RatioMaster.App.csproj'
$pub      = Join-Path $appDir 'publish\win-x64'
$pubArm   = Join-Path $appDir 'publish\win-arm64'
$linux    = Join-Path $appDir 'publish\linux-x64'
$linuxArm = Join-Path $appDir 'publish\linux-arm64'
$appRun   = Join-Path $dataDir 'AppRun'
$desktop  = Join-Path $dataDir 'RatioMaster.desktop'
$iconPng  = Join-Path $root 'icon.png'
$shFile   = Join-Path $dataDir 'rm_linux_aot.sh'
$wrapFile = Join-Path $dataDir 'aarch64-aot-ld'
$stamp    = Join-Path $dataDir 'last_update_check.txt'

$argStr    = [string]$env:RM_BAT_ARGS
$selAot    = [bool]($argStr -match '(?i)--aot\b')        # bare win-x64 AOT publish, no packaging
$selWinX64 = [bool]($argStr -match '(?i)--winx64\b')     # win-x64 single .exe
$selWinArm = [bool]($argStr -match '(?i)--winarm64\b')   # win-arm64 .zip
$selLinux  = [bool]($argStr -match '(?i)--linux\b')      # linux .AppImage x64 + arm64
$selApk    = [bool]($argStr -match '(?i)--apk\b')        # Android .apk (sideload)
$selAab    = [bool]($argStr -match '(?i)--aab\b')        # Android .aab (Play App Bundle)
$anySel = $selAot -or $selWinX64 -or $selWinArm -or $selLinux -or $selApk -or $selAab
$doWinX64Aot   = (-not $anySel) -or $selAot -or $selWinX64
$doWinX64Pack  = (-not $anySel) -or $selWinX64
$doWinArm64    = (-not $anySel) -or $selWinArm
$doLinux       = (-not $anySel) -or $selLinux
$doApk         = (-not $anySel) -or $selApk
$doAab         = (-not $anySel) -or $selAab
if ($anySel) { Log ("[setup] selective build: " + (@(if($doWinX64Aot){'win-x64'};if($doWinArm64){'win-arm64'};if($doLinux){'linux'};if($doApk){'apk'};if($doAab){'aab'}) -join ', ')) }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$built  = [ordered]@{}
$tools  = [ordered]@{}
New-Item -ItemType Directory -Force -Path $dataDir, $outDir | Out-Null

# ── helpers ──
function Have-Cmd([string]$n) { [bool](Get-Command $n -ErrorAction SilentlyContinue) }
function Is-Admin { try { (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator) } catch { $false } }
function Refresh-Path {
    try {
        $mm = [Environment]::GetEnvironmentVariable('Path','Machine'); $u = [Environment]::GetEnvironmentVariable('Path','User')
        $joined = ((@($mm,$u) | Where-Object { $_ }) -join ';')
        $env:PATH = if ($script:PathPrefix) { "$($script:PathPrefix);$joined" } else { $joined }
        Get-Command -Name dotnet,winget -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}
function Stale24h { if (-not [IO.File]::Exists($stamp)) { return $true }; try { return (((Get-Date) - [datetime]::Parse(([IO.File]::ReadAllText($stamp)).Trim())).TotalHours -ge 24) } catch { return $true } }
function To-WslPath([string]$w) { $p = $w -replace '\\','/'; if ($p -match '^([A-Za-z]):(.*)$') { '/mnt/' + $Matches[1].ToLower() + $Matches[2] } else { $p } }
function Winget-Present([string]$id) { try { & winget list --id $id -e --disable-interactivity --accept-source-agreements *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } }
function Ensure-Winget([string]$id,[int]$from,[int]$to,[string]$label) {
    if (-not (Have-Cmd 'winget')) { $tools[$label] = 'SKIPPED - winget not available; install manually'; return $false }
    $present = Winget-Present $id
    $log = Join-Path $env:TEMP ("rm_wg_" + ($id -replace '[^\w]','_') + ".log")
    $wgVerb = if ($present) { 'upgrade' } else { 'install' }
    Invoke-Stage 'winget' @($wgVerb,'--id',$id,'-e','--silent','--accept-package-agreements','--accept-source-agreements','--disable-interactivity') $from $to "$label..." $log 1800 | Out-Null
    Refresh-Path
    $ok = Winget-Present $id
    $tools[$label] = if ($ok) { if ($present) { 'up to date' } else { 'installed' } } else { "FAILED (see $log)" }
    return $ok
}
function Net11Ver { try { return (((& dotnet --list-sdks) 2>&1) | ForEach-Object { if ($_ -match '^(11\.0\.\d\S*)') { $Matches[1] } } | Select-Object -First 1) } catch { return $null } }
function Wg-Exists([string]$id) { if (-not (Have-Cmd 'winget')) { return $false }; try { & winget show --id $id -e --disable-interactivity --accept-source-agreements *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } }
function Ensure-Dotnet11 {
    Refresh-Path
    if ((-not (Net11Ver)) -or $daily) {
        Update-LoadingPopup 4 "Checking .NET 11 SDK..."
        $id = if (Wg-Exists 'Microsoft.DotNet.SDK.11') { 'Microsoft.DotNet.SDK.11' } else { 'Microsoft.DotNet.SDK.Preview' }
        Ensure-Winget $id 3 6 ".NET 11 SDK" | Out-Null; Refresh-Path
    } else { $tools['.NET 11 SDK'] = "present ($(Net11Ver))" }
    $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not (Net11Ver) -and -not $dn) { Fail("No .NET SDK found and the automatic install failed.`r`nInstall the .NET 11 SDK, then re-run.") }
    return $dn
}
function Wsl-Ready { try { & cmd /c "wsl -e true >nul 2>&1"; return ($LASTEXITCODE -eq 0) } catch { return $false } }
function HasAndroidWorkload { try { return [bool](((& $dotnet workload list) 2>&1 | Out-String) -match '(?im)^\s*android\b') } catch { return $false } }
function Ensure-AndroidWorkload {
    if (HasAndroidWorkload) { $tools['Android workload'] = 'present'; return }
    # The .NET 11 PREVIEW SDK defaults to workload-SET mode, pinned to a set that can reference an android
    # pack build never published to nuget.org; manifest update-mode resolves the latest android manifest.
    try { & $dotnet workload config --update-mode manifests 2>&1 | Out-Null } catch {}
    $rc = Invoke-Stage $dotnet @('workload','install','android') 8 12 "Installing .NET Android workload (~1 GB)..." (Join-Path $env:TEMP 'rm_wlinstall.log') 2400
    $tools['Android workload'] = if ($rc -eq 0 -and (HasAndroidWorkload)) { 'installed' } else { "FAILED - run as admin: dotnet workload install android" }
}
function OutName([string]$suffix) { Join-Path $outDir ("RatioMaster.NET_{0}_v{1}" -f $suffix,$ver) }
function Find-VCArm64Link { foreach ($b in @("${env:ProgramFiles}\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) { if ([IO.Directory]::Exists($b)) { $l = Get-ChildItem (Join-Path $b '*\*\VC\Tools\MSVC\*\bin\Hostx64\arm64\link.exe') -ErrorAction SilentlyContinue | Select-Object -First 1; if ($l) { return $l.FullName } } }; return $null }
function Ensure-VCArm64 {
    if (Find-VCArm64Link) { return $true }
    $base  = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'; $vsw = Join-Path $base 'vswhere.exe'; $setup = Join-Path $base 'setup.exe'
    if (-not [IO.File]::Exists($vsw) -or -not [IO.File]::Exists($setup)) { return $false }
    $inst = (& $vsw -all -prerelease -products * -property installationPath | Select-Object -First 1); if (-not $inst) { return $false }
    Invoke-Stage $setup @('modify','--installPath',$inst,'--add','Microsoft.VisualStudio.Component.VC.Tools.ARM64','--quiet','--norestart','--force','--wait') 63 66 "Installing MSVC ARM64 build tools (~2 GB)..." (Join-Path $env:TEMP 'rm_vcarm64.log') | Out-Null
    return [bool](Find-VCArm64Link)
}
function Find-VCx64Link {
    $vsw = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if ([IO.File]::Exists($vsw)) { $inst = & $vsw -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1; if ($inst) { $l = Get-ChildItem (Join-Path $inst 'VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe') -ErrorAction SilentlyContinue | Select-Object -First 1; if ($l) { return $l.FullName } } }
    foreach ($b in @("${env:ProgramFiles}\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) { if ([IO.Directory]::Exists($b)) { $l = Get-ChildItem (Join-Path $b '*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\link.exe') -ErrorAction SilentlyContinue | Select-Object -First 1; if ($l) { return $l.FullName } } }
    return $null
}
function Find-WinSdk { foreach ($r in @("${env:ProgramFiles(x86)}\Windows Kits\10\Lib", "$env:ProgramFiles\Windows Kits\10\Lib")) { if ([IO.Directory]::Exists($r)) { $k = Get-ChildItem (Join-Path $r '*\um\x64\kernel32.lib') -ErrorAction SilentlyContinue | Select-Object -First 1; if ($k) { return $k.FullName } } }; return $null }
function Ensure-VSCppX64 {
    if ((Find-VCx64Link) -and (Find-WinSdk)) { $tools['MSVC x64 + Windows SDK'] = 'present'; return $true }
    Update-LoadingPopup 8 "Installing C++ build tools + Windows SDK (Native-AOT)..."
    $base = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'; $vsw = Join-Path $base 'vswhere.exe'; $vsSetup = Join-Path $base 'setup.exe'
    if ([IO.File]::Exists($vsw) -and [IO.File]::Exists($vsSetup)) {
        $inst = & $vsw -all -prerelease -products * -property installationPath 2>$null | Select-Object -First 1
        if ($inst) { Invoke-Stage $vsSetup @('modify','--installPath',$inst,'--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended','--quiet','--norestart','--force','--wait') 8 12 "Adding C++ build tools + Windows SDK to VS (~4 GB)..." (Join-Path $env:TEMP 'rm_vcx64.log') 3600 | Out-Null }
    }
    if (((-not (Find-VCx64Link)) -or (-not (Find-WinSdk))) -and (Have-Cmd 'winget')) {
        Invoke-Stage 'winget' @('install','--id','Microsoft.VisualStudio.2022.BuildTools','-e','--silent','--accept-package-agreements','--accept-source-agreements','--override','--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended') 8 12 "Installing VS Build Tools (C++ + Windows SDK, ~4 GB)..." (Join-Path $env:TEMP 'rm_bt_install.log') 3600 | Out-Null
        Refresh-Path
    }
    if ((-not (Find-VCx64Link)) -or (-not (Find-WinSdk))) {
        $bs = Join-Path $env:TEMP 'vs_BuildTools.exe'
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
        Update-LoadingPopup 8 "Downloading VS Build Tools bootstrapper..."
        $got = $false
        try { (New-Object System.Net.WebClient).DownloadFile('https://aka.ms/vs/17/release/vs_BuildTools.exe', $bs); $got = [IO.File]::Exists($bs) -and ((Get-Item $bs).Length -gt 100000) } catch { Log "[toolchain] VS Build Tools bootstrapper download failed: $($_.Exception.Message)" }
        if ($got) { Invoke-Stage $bs @('--quiet','--wait','--norestart','--nocache','--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended') 9 12 "Installing VS Build Tools (C++ + Windows SDK, ~4 GB)..." (Join-Path $env:TEMP 'rm_bt_install.log') 3600 | Out-Null; Refresh-Path; try { Remove-Item $bs -Force -ErrorAction SilentlyContinue } catch {} }
    }
    $ok = [bool]((Find-VCx64Link) -and (Find-WinSdk))
    $tools['MSVC x64 + Windows SDK'] = if ($ok) { 'installed' } elseif (Find-VCx64Link) { 'MSVC present but Windows SDK NOT found - win-x64 AOT link may fail' } else { 'MISSING - install VS Build Tools "Desktop development with C++"; win-x64 AOT will fail' }
    return $ok
}
# A running RatioMaster locks its OWN published .exe, so a build started while it is open dies deep inside
# MSBuild after 10 one-second retries with a cryptic MSB3027/MSB3021 wall. Catch it up front instead.
#
# We deliberately do NOT try to close it for the user, by either route:
#   * CloseMainWindow() is useless here — MainWindow.OnClosing cancels the close on Windows and Hide()s to
#     the tray, so the process never exits. On a VISIBLE window it would actively make things worse by
#     hiding the app right before we tell the user to go and close it.
#   * taskkill /F would work, but skips the shutdown handler that saves the session, losing the current
#     upload/download counters — the exact class of silent data loss we removed from the engine.
# So: detect, stop, and say precisely what to do.
function Ensure-AppNotRunning {
    $live = @()
    try {
        $live = @(Get-Process -Name 'RatioMaster.NET' -ErrorAction SilentlyContinue | Where-Object {
            $exe = $null; try { $exe = $_.Path } catch {}
            # No readable path -> can't rule it out, so treat it as ours (a locked file is the failure we're
            # preventing). A copy installed elsewhere can't lock OUR publish dir, so it is ignored.
            (-not $exe) -or $exe.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase)
        })
    } catch { return }
    if (-not $live) { return }

    $ids = ($live | ForEach-Object { $_.Id }) -join ', '
    Log "[app] build blocked: RatioMaster.NET is running (PID $ids) and locks the published exe."
    Fail("RatioMaster.NET is running (PID $ids) and is locking its own published .exe, so the build cannot overwrite it.`r`n`r`nQuit it, then re-run this builder.`r`n`r`nLook in the SYSTEM TRAY (notification area, next to the clock): closing the window only MINIMISES the app there, so it keeps running invisibly. Right-click the RatioMaster tray icon and choose Exit.`r`n`r`n(The builder will not kill it for you: a forced kill skips the session save and would lose the current upload/download counters.)")
}

# Native-AOT publish a Windows RID via -p:DesktopRid (win-x64 = static single file; win-arm64 = multi-file).
function Publish-WinAot([string]$rid,[string]$out,[int]$from,[int]$to) {
    $log = Join-Path $env:TEMP "ratiomaster_publish_$rid.log"
    $rc = Invoke-Stage $dotnet @('publish', $proj, '-c','Release',"-p:DesktopRid=$rid",'-p:PublishAot=true','-o',$out) $from $to "Building Windows app ($rid, Native-AOT)..." $log
    if ($rc -ne 0) { return $false }
    Get-ChildItem $out -Filter *.session -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    return [bool][IO.File]::Exists((Join-Path $out 'RatioMaster.NET.exe'))
}
function Zip-Dir([string]$dir,[string]$zip) {
    try { Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue; [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip); return [IO.File]::Exists($zip) } catch { Log "[zip] $($_.Exception.Message)"; return $false }
}
function Package-Linux([string]$dir,[string]$rid) {   # .tar.gz fallback when AppImage can't be built
    $pkg = (OutName $rid) + '.tar.gz'
    try {
        Remove-Item -LiteralPath $pkg -Force -ErrorAction SilentlyContinue
        $tarExe = (Get-Command tar.exe -ErrorAction SilentlyContinue).Source
        if ($tarExe) { $rc = Invoke-Stage $tarExe @('-czf',$pkg,'-C',$dir,'.') 98 99 "Packaging $rid (.tar.gz)..." (Join-Path $env:TEMP "rm_tar_$rid.log"); if ($rc -eq 0 -and [IO.File]::Exists($pkg)) { $built["Linux ($rid, .tar.gz)"] = $pkg } else { $built["Linux ($rid)"] = "tar failed - raw build at $dir" } }
        else { $zip = (OutName $rid) + '.zip'; Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip -Force; $built["Linux ($rid, .zip)"] = $zip }
    } catch { $built["Linux ($rid)"] = "packaging failed - raw build at $dir" }
}

# ── validate ──
Update-LoadingPopup 6 "Validating sources..."
if (-not [IO.File]::Exists($proj)) { Fail("Project not found: $proj") }

# ── PHASE 0 : tools ──
$daily = Stale24h
$dotnet = Ensure-Dotnet11
Update-LoadingPopup 7 "Checking build tools..."
if ($doWinX64Aot -or $doWinArm64) { Ensure-VSCppX64 | Out-Null }
if ($doApk -or $doAab) { Ensure-AndroidWorkload }
if ($daily) {
    if (Wsl-Ready) { $tools['WSL'] = 'ready' }
    elseif (Is-Admin) { Invoke-Stage 'wsl' @('--install','--no-launch') 19 21 "Installing WSL..." (Join-Path $env:TEMP 'rm_wsl_install.log') 1800 | Out-Null; Refresh-Path; $tools['WSL'] = 'install attempted - REBOOT then re-run for Linux AppImage'; $RebootNeeded = $true }
    else { $tools['WSL'] = 'NOT installed - run as Administrator once + reboot for the Linux AppImage' }
    try { [IO.File]::WriteAllText($stamp, (Get-Date).ToString('o')) } catch {}
} else { $tools['Daily tool check'] = 'skipped (< 24h ago)' }
Update-LoadingPopup 22 "Building..."

# Only the desktop publishes write into publish\ (and thus collide with a running instance); an
# apk/aab-only run leaves it alone, so don't disturb the user's session for those.
if ($doWinX64Aot -or $doWinArm64 -or $doLinux) { Ensure-AppNotRunning }

# ── 1) Windows app : Native-AOT (MSVC's own link.exe first on PATH so a rogue GnuWin32 `link` loses) ──
$vcBin = $null; $vcl = Find-VCx64Link; if ($vcl) { $vcBin = Split-Path $vcl -Parent }
$script:PathPrefix = if ($vcBin) { "$vcBin;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" } else { "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer" }
$env:PATH = "$($script:PathPrefix);$env:PATH"
if ($doWinX64Aot) {
    if (-not (Publish-WinAot 'win-x64' $pub 22 40)) {
        $wl=Join-Path $env:TEMP 'ratiomaster_publish_win-x64.log'
        $hint = if (-not ((Find-VCx64Link) -and (Find-WinSdk))) { "`r`n`r`nLIKELY CAUSE: MSVC x64 C++ build tools / Windows SDK missing. Install 'Visual Studio Build Tools' with 'Desktop development with C++', then re-run (auto-installed as Administrator)." } else { '' }
        Fail("Windows x64 AOT publish failed.`r`nLog: $wl$hint`r`n`r`n$(Tail $wl)")
    }
}

# ── 2) version — from the exe ProductVersion, else the csproj <Version>. ──
Update-LoadingPopup 41 "Reading version..."
$ver = $null
$x64Exe = Join-Path $pub 'RatioMaster.NET.exe'
if ($doWinX64Aot -and [IO.File]::Exists($x64Exe)) { $fv = (Get-Item $x64Exe).VersionInfo.ProductVersion; $vm = [regex]::Match([string]$fv, '^\d+\.\d+\.\d+'); if ($vm.Success) { $ver = $vm.Value } }
if (-not $ver) { try { $ct = Read-TextRobust $proj } catch { $ct = '' }; $vm = [regex]::Match([string]$ct, '<Version>\s*(\d+\.\d+\.\d+)'); if ($vm.Success) { $ver = $vm.Groups[1].Value } }
if (-not $ver) { $ver = '0.0.0' }
# Android versionCode = a monotonic int from major.minor.patch (Play needs an ever-increasing integer),
# so the .apk/.aab report the SAME version as the Windows/Linux builds.
$verCode = 1; try { $vp = $ver -split '\.'; $verCode = ([int]$vp[0]) * 10000 + ([int]$vp[1]) * 100 + [int]$vp[2] } catch {}
if ($verCode -lt 1) { $verCode = 1 }

# ── 3) Package Windows : win-x64 = the single .exe (copied) ; win-arm64 = a .zip of the folder. ──
if ($doWinX64Pack) {
    $exeOut = (OutName 'win-x64') + '.exe'
    try { Copy-Item $x64Exe $exeOut -Force; $built['Windows (win-x64, single .exe)'] = $exeOut } catch { $built['Windows (win-x64)'] = "copy failed: $($_.Exception.Message)" }
}
if ($doWinArm64) {
    Update-LoadingPopup 60 "Building win-arm64..."
    if (Ensure-VCArm64) {
        if (Publish-WinAot 'win-arm64' $pubArm 62 70) {
            $zip = (OutName 'win-arm64') + '.zip'
            if (Zip-Dir $pubArm $zip) { $built['Windows (win-arm64, .zip)'] = $zip } else { $built['Windows (win-arm64)'] = "zip failed - raw build at $pubArm" }
        } else { $built['Windows (win-arm64)'] = "AOT publish FAILED (see $env:TEMP\ratiomaster_publish_win-arm64.log)" }
    } else { $built['Windows (win-arm64)'] = 'SKIPPED - MSVC ARM64 build tools missing (run the .bat as Administrator to install them)' }
}

# ── 4) Android : RELEASE .apk (sideload) and/or .aab (Play App Bundle). CoreCLR = .NET's default Android
#       runtime. Needs the android workload (phase 0) + the Android SDK/JDK (self-acquired to user paths,
#       no admin). aapt2/d8 reject SPACES + non-ASCII in the project path or %TEMP%, so when the path is
#       hostile the build runs through a short ASCII junction with %TEMP% redirected. Best-effort. ──
if ($doApk -or $doAab) {
    Update-LoadingPopup 70 "Checking Android toolchain..."
    if (HasAndroidWorkload) {
        $logDeps = Join-Path $env:TEMP 'ratiomaster_android_deps.log'
        $logApk  = Join-Path $env:TEMP 'ratiomaster_apk.log'
        $logAab  = Join-Path $env:TEMP 'ratiomaster_aab.log'
        $androidSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
        $androidJdk = Join-Path $env:LOCALAPPDATA 'Android\jdk'
        $androidArgs = @("-p:AndroidSdkDirectory=$androidSdk", "-p:JavaSdkDirectory=$androidJdk", '-p:AcceptAndroidSdkLicenses=True', '-p:IncludeAndroid=true', "-p:ApplicationDisplayVersion=$ver", "-p:ApplicationVersion=$verCode")

        # aapt2 (APT2265) + the D8 dexer choke on spaces / non-ASCII in the PROJECT path or %TEMP%.
        $hostile = ($root -match '[^\x21-\x7E]') -or ($env:TEMP -match '[^\x21-\x7E]')
        $apkProj = $proj; $junction = $null; $savedTemp = $env:TEMP; $savedTmp = $env:TMP
        if ($hostile) {
            $junction = Join-Path $env:SystemDrive 'rm-apk'
            try { if (Test-Path $junction) { & cmd /c "rmdir `"$junction`"" 2>&1 | Out-Null }
                  & cmd /c "mklink /J `"$junction`" `"$root`"" 2>&1 | Out-Null } catch {}
            $jp = Join-Path $junction 'RatioMaster.App\RatioMaster.App.csproj'
            if ([IO.File]::Exists($jp)) { $apkProj = $jp }
            $asciiTmp = Join-Path $env:SystemDrive 'rm-tmp'
            try { New-Item -ItemType Directory -Force -Path $asciiTmp | Out-Null; $env:TEMP = $asciiTmp; $env:TMP = $asciiTmp } catch {}
        }
        $manPath = Join-Path $appDir 'Properties\AndroidManifest.xml'
        $manOrig = $null; $rc = $null; $rcAab = $null
        try {
            # Acquire/refresh the Android SDK + the JDK .NET needs (user paths, no admin).
            if ((-not [IO.Directory]::Exists((Join-Path $androidSdk 'platform-tools'))) -or (-not [IO.File]::Exists((Join-Path $androidJdk 'bin\keytool.exe'))) -or $daily) {
                Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-t:InstallAndroidDependencies') + $androidArgs) 70 72 "Installing Android SDK + JDK..." $logDeps | Out-Null
            }
            # targetSdk = the highest STABLE installed platform (auto-detected: source.properties
            # PreviewSdkInt = 0 ⇒ released). Fall back to highest installed, then to 35 (Play floor).
            # Rewritten into the manifest for THIS build only; restored in the finally.
            $tgtApi = 0; $tgtKind = 'highest stable installed'
            try {
                $platDir = Join-Path $androidSdk 'platforms'; $bestStable = 0; $bestAny = 0
                if ([IO.Directory]::Exists($platDir)) {
                    foreach ($pd in (Get-ChildItem $platDir -Directory -EA SilentlyContinue)) {
                        $sp = Join-Path $pd.FullName 'source.properties'; if (-not [IO.File]::Exists($sp)) { continue }
                        $txt = [IO.File]::ReadAllText($sp)
                        if ($txt -notmatch '(?im)^[ \t]*AndroidVersion\.ApiLevel[ \t]*=[ \t]*(\d+)') { continue }
                        $api = [int]$Matches[1]
                        $preview = if ($txt -match '(?im)^[ \t]*AndroidVersion\.PreviewSdkInt[ \t]*=[ \t]*(\d+)') { [int]$Matches[1] } else { 0 }
                        if ($api -gt $bestAny) { $bestAny = $api }
                        if ($preview -eq 0 -and $api -gt $bestStable) { $bestStable = $api }
                    }
                }
                if ($bestStable -gt 0) { $tgtApi = $bestStable }
                elseif ($bestAny -gt 0) { $tgtApi = $bestAny; $tgtKind = 'highest installed (no stable platform)' }
            } catch {}
            if ($tgtApi -lt 1) { $tgtApi = 35; $tgtKind = 'Play floor (no platform detected)' }
            try {
                $o = [IO.File]::ReadAllText($manPath)
                $n = [regex]::Replace($o, 'android:targetSdkVersion="\d+"', "android:targetSdkVersion=`"$tgtApi`"")
                if ($n -ne $o) { try { [IO.File]::WriteAllText(($manPath + '.rmbak'), $o) } catch {}; $manOrig = $o; [IO.File]::WriteAllText($manPath, $n) }
                $built['Android targetSdk'] = "$tgtApi ($tgtKind)"
            } catch {}
            $env:JAVA_HOME = $androidJdk
            # SIGNING: a RELEASE keystore from env when all four vars are set AND the file exists; otherwise a
            # debug keystore (sideload only). For a Play release, set RM_ANDROID_KEYSTORE / _PASS / _ALIAS / _KEY_PASS.
            $relKs=$env:RM_ANDROID_KEYSTORE; $relSp=$env:RM_ANDROID_KEYSTORE_PASS; $relAl=$env:RM_ANDROID_KEY_ALIAS; $relKp=$env:RM_ANDROID_KEY_PASS
            if ($relKs -and [IO.File]::Exists($relKs) -and $relSp -and $relAl -and $relKp) {
                $sign = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$relKs", "-p:AndroidSigningKeyAlias=$relAl", "-p:AndroidSigningKeyPass=$relKp", "-p:AndroidSigningStorePass=$relSp"); $signKind = 'release'
            } else {
                $keytool = Join-Path $androidJdk 'bin\keytool.exe'; $ks = Join-Path $env:USERPROFILE '.android\debug.keystore'
                if ((-not [IO.File]::Exists($ks)) -and [IO.File]::Exists($keytool)) {
                    try { New-Item -ItemType Directory -Force -Path (Split-Path $ks) | Out-Null
                          & $keytool -genkeypair -v -keystore $ks -storepass android -keypass android -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US" 2>&1 | Out-Null } catch {}
                }
                $sign = if ([IO.File]::Exists($ks)) { @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$ks", '-p:AndroidSigningKeyAlias=androiddebugkey', '-p:AndroidSigningKeyPass=android', '-p:AndroidSigningStorePass=android') } else { @() }
                $signKind = 'debug'
            }
            # CoreCLR is .NET's DEFAULT Android runtime; RunAOTCompilation is Mono-only (XA1044) → not passed.
            if ($doApk) { $rc    = Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-p:AndroidPackageFormat=apk') + $androidArgs + $sign) 72 82 "Building Android APK ($signKind-signed) - slow..." $logApk }
            if ($doAab) { $rcAab = Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-p:AndroidPackageFormat=aab') + $androidArgs + $sign) 82 84 "Building Android App Bundle (.aab)..." $logAab }
        }
        finally {
            if ($manOrig) { try { [IO.File]::WriteAllText($manPath, $manOrig) } catch {}; try { [IO.File]::Delete($manPath + '.rmbak') } catch {} }
            $env:TEMP = $savedTemp; $env:TMP = $savedTmp
            if ($junction) { & cmd /c "rmdir `"$junction`"" 2>&1 | Out-Null }
        }
        $abin = Join-Path $appDir 'bin\Release\net11.0-android'
        if ($doApk) {
            if ($rc -eq 0) {
                $apk = Get-ChildItem $abin -Recurse -Filter '*-Signed.apk' -EA SilentlyContinue | Select-Object -First 1
                if (-not $apk) { $apk = Get-ChildItem $abin -Recurse -Filter '*.apk' -EA SilentlyContinue | Select-Object -First 1 }
                if ($apk) { $apkDest = (OutName 'android') + '.apk'; Copy-Item $apk.FullName $apkDest -Force; $built['Android APK'] = "$apkDest [$signKind]" }
                else      { $built['Android APK'] = "built, but no .apk found (see $logApk)" }
            } else { $built['Android APK'] = "FAILED (see $logApk)" }
        }
        if ($doAab) {
            if ($rcAab -eq 0) {
                $aab = Get-ChildItem $abin -Recurse -Filter '*-Signed.aab' -EA SilentlyContinue | Select-Object -First 1
                if (-not $aab) { $aab = Get-ChildItem $abin -Recurse -Filter '*.aab' -EA SilentlyContinue | Select-Object -First 1 }
                if ($aab) { $aabDest = (OutName 'android') + '.aab'; Copy-Item $aab.FullName $aabDest -Force; $built['Android AAB'] = "$aabDest [$signKind]" }
                else      { $built['Android AAB'] = "built, but no .aab found (see $logAab)" }
            } else { $built['Android AAB'] = "FAILED (see $logAab)" }
        }
    } else {
        if ($doApk) { $built['Android APK'] = 'SKIPPED - .NET Android workload missing (phase 0 installs it)' }
        if ($doAab) { $built['Android AAB'] = 'SKIPPED - .NET Android workload missing (phase 0 installs it)' }
    }
}

# ── 5) Linux : Native-AOT via WSL, packaged as an AppImage (single portable file) per arch. ──
if ($doLinux) {
    $logLin = Join-Path $env:TEMP 'ratiomaster_linux.log'
    $linTargets = @(@{ rid='linux-x64'; dir=$linux; arch='x86_64' }, @{ rid='linux-arm64'; dir=$linuxArm; arch='aarch64' })
    foreach ($tt in $linTargets) { try { if ([IO.Directory]::Exists($tt.dir)) { Remove-Item -LiteralPath $tt.dir -Recurse -Force -ErrorAction SilentlyContinue } } catch {} }
    Get-ChildItem $outDir -Filter '*.AppImage' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match [regex]::Escape("_v$ver`_") } | Remove-Item -Force -ErrorAction SilentlyContinue
    if (Wsl-Ready) {
        Update-LoadingPopup 84 "Linux AOT + AppImage via WSL (x64 + arm64)..."
        try {
            $wrap = "#!/usr/bin/env bash`n" + "args=()`n" + 'for a in "$@"; do case "$a" in --target=*|--gcc-toolchain=*) ;; *) args+=("$a");; esac; done' + "`n" + 'exec aarch64-linux-gnu-gcc "${args[@]}"' + "`n"
            [IO.File]::WriteAllText($wrapFile, $wrap, (New-Object System.Text.UTF8Encoding($false)))
            $ow = To-WslPath $outDir
            $body = "#!/usr/bin/env bash`n" + "set -e`n" + "export DEBIAN_FRONTEND=noninteractive`n" +
                "( command -v clang >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -qi libicu && command -v aarch64-linux-gnu-gcc >/dev/null 2>&1 && command -v file >/dev/null 2>&1 ) || { apt-get update -y && apt-get install -y clang zlib1g-dev libicu-dev curl ca-certificates gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu file; }`n" +
                "install -m 0755 `"$(To-WslPath $wrapFile)`" /usr/local/bin/aarch64-aot-ld`n" +
                "if command -v dotnet >/dev/null 2>&1; then DOTNET=dotnet; elif [ -x /root/.dotnet/dotnet ]; then DOTNET=/root/.dotnet/dotnet; else curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/di.sh; bash /tmp/di.sh --channel 11.0 --quality preview --install-dir /root/.dotnet; DOTNET=/root/.dotnet/dotnet; fi`n" +
                "`"`$DOTNET`" publish `"$(To-WslPath $proj)`" -c Release -p:DesktopRid=linux-x64 -p:PublishAot=true -p:DebugType=none -o `"$(To-WslPath $linux)`" || true`n" +
                "`"`$DOTNET`" publish `"$(To-WslPath $proj)`" -c Release -p:DesktopRid=linux-arm64 -p:PublishAot=true -p:DebugType=none -p:CppCompilerAndLinker=/usr/local/bin/aarch64-aot-ld -p:ObjCopyName=aarch64-linux-gnu-objcopy -o `"$(To-WslPath $linuxArm)`" || true`n" +
                "C=/root/.cache/rm-appimage; mkdir -p `"`$C`"`n" +
                "[ -x `"`$C/appimagetool`" ] || { curl -sSL -o `"`$C/appimagetool`" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage && chmod +x `"`$C/appimagetool`"; }`n" +
                "[ -f `"`$C/runtime-x86_64`" ] || curl -sSL -o `"`$C/runtime-x86_64`" https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64`n" +
                "[ -f `"`$C/runtime-aarch64`" ] || curl -sSL -o `"`$C/runtime-aarch64`" https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-aarch64`n" +
                "mkapp() { d=`"`$1`"; a=`"`$2`"; o=`"`$3`"; [ -f `"`$d/RatioMaster.NET`" ] || return 0; ad=`"`$d/AppDir`"; rm -rf `"`$ad`"; mkdir -p `"`$ad/usr/bin`"; cp -f `"`$d/RatioMaster.NET`" `"`$ad/usr/bin/`"; cp -f `"`$d`"/*.so `"`$ad/usr/bin/`" 2>/dev/null || true; cp -f `"$(To-WslPath $appRun)`" `"`$ad/AppRun`"; sed -i 's/\r`$//' `"`$ad/AppRun`"; chmod +x `"`$ad/AppRun`"; cp -f `"$(To-WslPath $desktop)`" `"`$ad/RatioMaster.desktop`"; sed -i 's/\r`$//' `"`$ad/RatioMaster.desktop`"; cp -f `"$(To-WslPath $iconPng)`" `"`$ad/ratiomaster.png`" 2>/dev/null || true; ( APPIMAGE_EXTRACT_AND_RUN=1 ARCH=`"`$a`" `"`$C/appimagetool`" --runtime-file `"`$C/runtime-`$a`" `"`$ad`" `"`$o`" ) || return 1; }`n" +
                "mkapp `"$(To-WslPath $linux)`" x86_64 `"$ow/RatioMaster.NET_v${ver}_x86_64.AppImage`" || echo '[appimage] x86_64 failed'`n" +
                "mkapp `"$(To-WslPath $linuxArm)`" aarch64 `"$ow/RatioMaster.NET_v${ver}_aarch64.AppImage`" || echo '[appimage] aarch64 failed'`n"
            [IO.File]::WriteAllText($shFile, $body, (New-Object System.Text.UTF8Encoding($false)))
            Invoke-Stage 'wsl' @('-u','root','-e','bash', (To-WslPath $shFile)) 86 97 "Linux AOT + AppImage (WSL)..." $logLin | Out-Null
        } catch {}
        finally { Remove-Item -LiteralPath $shFile,$wrapFile -Force -ErrorAction SilentlyContinue }
    }
    foreach ($tt in $linTargets) {
        $ai = Join-Path $outDir ("RatioMaster.NET_v{0}_{1}.AppImage" -f $ver,$tt.arch)
        if ([IO.File]::Exists($ai)) { $built["Linux ($($tt.rid), .AppImage)"] = $ai; continue }
        # No AppImage (WSL absent or appimagetool failed) → self-contained JIT + .tar.gz fallback.
        if (-not [IO.File]::Exists((Join-Path $tt.dir 'RatioMaster.NET'))) {
            $logJit = Join-Path $env:TEMP "ratiomaster_linux_$($tt.rid)_jit.log"
            Invoke-Stage $dotnet @('publish', $proj, "-p:DesktopRid=$($tt.rid)", '-c','Release','--self-contained','true','-p:PublishAot=false','-p:DebugType=none','-o',$tt.dir) 96 97 "Linux $($tt.rid) self-contained JIT..." $logJit | Out-Null
        }
        if ([IO.File]::Exists((Join-Path $tt.dir 'RatioMaster.NET'))) { Package-Linux $tt.dir $tt.rid } else { $built["Linux ($($tt.rid))"] = "FAILED (see $logLin)" }
    }
}

# ── summary ──
Update-LoadingPopup 100 "Done"
Close-LoadingPopup
$artLines  = ($built.GetEnumerator() | ForEach-Object { "  - $($_.Key):`r`n      $($_.Value)" }) -join "`r`n"
$toolLines = if ($tools.Count) { "`r`nTools:`r`n" + (($tools.GetEnumerator() | ForEach-Object { "  - $($_.Key): $($_.Value)" }) -join "`r`n") } else { "" }
$androidNote = if ($doApk -or $doAab) { "`r`n`r`nAndroid: the .apk sideloads directly; the .aab is for Google Play. Both are DEBUG-signed unless you set RM_ANDROID_KEYSTORE / _PASS / _ALIAS / _KEY_PASS (a debug-signed .aab is NOT accepted by Play)." } else { "" }
$summary = "RatioMaster.NET v$ver - build complete (desktop artifacts are portable, no installer).`r`n`r`n$artLines$toolLines$androidNote`r`n`r`nmacOS (osx-x64/arm64): build on a Mac (dotnet publish -p:DesktopRid=osx-arm64 -p:PublishAot=true)."
Log "`r`n==== SUMMARY ===="
Log $summary
if ($buildLog) { Log "`r`nFull log: $buildLog" }

if ($RebootNeeded) {
    $r = [System.Windows.Forms.MessageBox]::Show("WSL was just installed and needs a REBOOT before it works (no Linux AppImage this run). Reboot now?", 'RatioMaster.NET Packing - reboot needed', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
    if ($r -eq [System.Windows.Forms.DialogResult]::Yes) { try { $sd = Start-Process 'shutdown.exe' -ArgumentList '/r /t 5 /c "RatioMaster: finishing WSL setup"' -PassThru -Wait -WindowStyle Hidden; if ($sd -and $sd.ExitCode -ne 0) { Restart-Computer -Force } } catch { try { Restart-Computer -Force } catch {} } }
}
exit 0
