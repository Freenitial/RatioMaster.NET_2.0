<# :
    @echo off & Title RatioMaster.NET Packing
    setlocal
    set "RM_BAT_ARGS=%*"
    if "%RM_BUILD_HEADLESS%"=="1" goto :runHeadless
    for %%a in (%*) do if /I "%%~a"=="--headless" goto :runHeadless

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
        "foreach($d in @((Join-Path $root 'artifacts\publish'),(Join-Path $root 'artifacts\logs\build'),(Join-Path $root 'artifacts\cache'),(Split-Path -Parent $bat),(Join-Path (Split-Path -Parent $bat) 'Output'),(Join-Path (Split-Path -Parent $bat) 'Data'))){try{New-Item -ItemType Directory -Force -Path $d | Out-Null; $t=Join-Path $d ('.w_'+[guid]::NewGuid().ToString('N')); [IO.File]::WriteAllText($t,'x'); Remove-Item $t -Force}catch{$reasons+=('output folder not writable: '+$d)}};" ^
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
    exit /b %ERRORLEVEL%
    :runHeadless
    set "RM_BUILD_HEADLESS=1"
    powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0Scripts\Invoke_RatioMasterBuilder.ps1" -BuilderPath "%~f0"
    exit /b %ERRORLEVEL%
#>

# ══════════════════════ RatioMaster.NET portable-build packer ══════════════════════
# Produces portable artifacts: Windows x64/ARM64 single executables and Linux AppImages.

$script:Headless = ($env:RM_BUILD_HEADLESS -eq '1') -or ($env:RM_BAT_ARGS -match '(?i)--headless\b')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$mg = [IntPtr]::Zero
if (-not $script:Headless) {
$t=$d.DefineType('E','Public,Class')
foreach($x in @(
    ,@('SetWindowTextW','user32.dll',([Bool]),@([IntPtr],[String]))
    ,@('DestroyWindow','user32.dll',([Bool]),@([IntPtr]))
    ,@('PeekMessageW','user32.dll',([Bool]),@([IntPtr],[IntPtr],[UInt32],[UInt32],[UInt32]))
    ,@('TranslateMessage','user32.dll',([Bool]),@([IntPtr]))
    ,@('DispatchMessageW','user32.dll',([IntPtr]),@([IntPtr]))
    ,@('MsgWaitForMultipleObjectsEx','user32.dll',([UInt32]),@([UInt32],[IntPtr[]],[UInt32],[UInt32],[UInt32]))
)){$z=$t.DefinePInvokeMethod($x[0],$x[1],'Public,Static,PinvokeImpl','Standard',$x[2],$x[3],'Winapi','Unicode');$z.SetImplementationFlags($z.GetMethodImplementationFlags()-bor128)}
$E=$t.CreateType()
$mg=$M::AllocHGlobal(48)
}

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

$logDir = Join-Path (Split-Path -Parent (Split-Path -Parent $batFile)) 'artifacts\logs\build'
$buildLog = $null
try {
    [IO.Directory]::CreateDirectory($logDir) | Out-Null
    $buildLog = Join-Path $logDir 'build-ratiomaster.log'
    if ($env:RM_BAT_ARGS -match '(?i)--rm-elevated') {
        [IO.File]::AppendAllText($buildLog, "---- relaunched elevated; continuing ----`r`n")
    }
    else {
        [IO.File]::WriteAllText($buildLog, "==== RatioMaster.NET build $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) ====`r`n")
    }
} catch { $buildLog = $null }
function Log([string]$m) { if ($null -eq $m) { return }; Write-Host $m; if ($buildLog) { try { [IO.File]::AppendAllText($buildLog, $m + "`r`n") } catch {} } }
trap {
    try {
        Log ("[ERROR] " + $_.Exception.Message)
        Log ("        at " + $_.InvocationInfo.PositionMessage)
    } catch {}
    try { Close-LoadingPopup } catch {}
    exit 1
}

$script:LastPopupMsg = $null; $script:LastPct = 0
function Invoke-LoadingPump {
    if ($script:Headless) {
        return
    }
    while ($E::PeekMessageW($mg, [IntPtr]::Zero, 0, 0, 1)) {
        $null = $E::TranslateMessage($mg)
        $null = $E::DispatchMessageW($mg)
    }
}
function Update-LoadingPopup([int]$pct, [string]$s) {
    $pct = [Math]::Max($pct, $script:LastPct)
    $script:LastPct = $pct
    if ($s -and $s -ne $script:LastPopupMsg) {
        Log ("[{0,3}%] {1}" -f $pct, $s)
        $script:LastPopupMsg = $s
    }
    if (-not $script:Headless) {
        $null = $A::SendMessageW($hb, 0x402, [IntPtr]$pct, [IntPtr]::Zero)
        $null = $E::SetWindowTextW($hl, $s)
        Invoke-LoadingPump
    }
}
function Close-LoadingPopup {
    if ($script:Headless) {
        return
    }
    try { $null = $E::DestroyWindow($hw) } catch {}
    try { Invoke-LoadingPump } catch {}
    if ($mg -ne [IntPtr]::Zero) {
        $M::FreeHGlobal($mg)
        $script:mg = [IntPtr]::Zero
    }
}
Update-LoadingPopup 5 "Loading assemblies..."

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
if (-not $script:Headless) {
    Add-Type -AssemblyName System.Windows.Forms
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function Fail([string]$msg) {
    Log ("[ERROR] " + $msg)
    try { Update-LoadingPopup 100 "Failed" } catch {}
    try { Close-LoadingPopup } catch {}
    if (-not $script:Headless) {
        [System.Windows.Forms.MessageBox]::Show($msg, 'RatioMaster.NET Packing - failed', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
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
function ConvertTo-ProcessArgument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') {
        return $value
    }
    $quoted = New-Object System.Text.StringBuilder
    $null = $quoted.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            $null = $quoted.Append('\', (2 * $slashes + 1))
        } else {
            $null = $quoted.Append('\', $slashes)
        }
        $null = $quoted.Append($character)
        $slashes = 0
    }
    $null = $quoted.Append('\', (2 * $slashes))
    $null = $quoted.Append('"')
    return $quoted.ToString()
}
function Wait-BuildProcess([System.Diagnostics.Process]$process, [int]$timeoutSec) {
    if ($script:Headless) {
        if ($timeoutSec -gt 0) {
            return $process.WaitForExit($timeoutSec * 1000)
        }
        $process.WaitForExit()
        return $true
    }
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited) {
        $remaining = [UInt32]::MaxValue
        if ($timeoutSec -gt 0) {
            $remainingMs = $timeoutSec * 1000 - $elapsed.ElapsedMilliseconds
            if ($remainingMs -le 0) {
                return $false
            }
            $remaining = [UInt32]$remainingMs
        }
        $result = $E::MsgWaitForMultipleObjectsEx(1, [IntPtr[]]@($process.Handle), $remaining, 0x04FF, 0x0004)
        if ($result -eq 258) {
            return $false
        }
        if ($result -eq [UInt32]::MaxValue) {
            throw 'Waiting for the build process or UI messages failed.'
        }
        Invoke-LoadingPump
    }
    return $true
}
function Invoke-Stage([string]$exe, [object[]]$argList, [int]$from, [int]$to, [string]$msg, [string]$log, [int]$timeoutSec = 0) {
    Update-LoadingPopup $from $msg
    $quotedArguments = @()
    foreach ($argument in $argList) {
        $quotedArguments += ConvertTo-ProcessArgument ([string]$argument)
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = $quotedArguments -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = $null
    try {
        $process = [System.Diagnostics.Process]::Start($info)
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $completed = Wait-BuildProcess $process $timeoutSec
        if (-not $completed) {
            $killInfo = New-Object System.Diagnostics.ProcessStartInfo
            $killInfo.FileName = 'taskkill.exe'
            $killInfo.Arguments = '/PID ' + $process.Id + ' /T /F'
            $killInfo.UseShellExecute = $false
            $killInfo.CreateNoWindow = $true
            $killInfo.RedirectStandardOutput = $true
            $killInfo.RedirectStandardError = $true
            $killer = [Diagnostics.Process]::Start($killInfo)
            try { $null = $killer.WaitForExit(5000) } finally { $killer.Dispose() }
            if (-not $process.HasExited) {
                $process.Kill()
            }
            if (-not $process.WaitForExit(5000)) {
                throw 'The timed-out build process did not terminate.'
            }
        }
        $process.WaitForExit()
        $captured = $standardOutput.GetAwaiter().GetResult() + [Environment]::NewLine + $standardError.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($log, $captured, (New-Object System.Text.UTF8Encoding($false)))
        if ($captured.Length -gt 0) {
            Log $captured.TrimEnd()
        }
        if (-not $completed) {
            Log ('[timeout] Stage exceeded {0}s and was terminated: {1}' -f $timeoutSec, $msg)
            return 1460
        }
        Update-LoadingPopup $to $msg
        return $process.ExitCode
    } finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }
}
function Tail([string]$log,[int]$n = 25) { $t = Read-TextRobust $log; if (-not $t) { return '' }; (($t -split "`r?`n") | Select-Object -Last $n) -join "`r`n" }

# ── Installer holds packaging sources and final packages; artifacts holds generated build files. ──
$root    = Split-Path -Parent (Split-Path -Parent $batFile)
$batDir  = Split-Path -Parent $batFile
$scriptDir = Join-Path $batDir 'Scripts'
$dataDir = Join-Path $batDir 'Data'
$cacheDir = Join-Path $root 'artifacts\cache'
$outDir  = Join-Path $batDir 'Output'
$appDir  = Join-Path $root 'RatioMaster.App'
$proj    = Join-Path $appDir 'RatioMaster.App.csproj'
Set-Location -LiteralPath $root
$publishRoot = Join-Path $root 'artifacts\publish'
$pub      = Join-Path $publishRoot 'win-x64'
$pubArm   = Join-Path $publishRoot 'win-arm64'
$stamp    = Join-Path $cacheDir 'last_update_check.txt'

$argStr    = [string]$env:RM_BAT_ARGS
$selAot    = [bool]($argStr -match '(?i)--aot\b')        # bare win-x64 AOT publish, no packaging
$selWinX64 = [bool]($argStr -match '(?i)--winx64\b')     # win-x64 single .exe
$selWinArm = [bool]($argStr -match '(?i)--winarm64\b')   # win-arm64 single .exe
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
$selectedTargets = @()
if ($doWinX64Aot) {
    $selectedTargets += 'win-x64'
}
if ($doWinArm64) {
    $selectedTargets += 'win-arm64'
}
if ($doLinux) {
    $selectedTargets += 'linux'
}
if ($doApk) {
    $selectedTargets += 'apk'
}
if ($doAab) {
    $selectedTargets += 'aab'
}
Log ("[setup] selected targets: " + ($selectedTargets -join ', '))
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$built  = [ordered]@{}
$tools  = [ordered]@{}
New-Item -ItemType Directory -Force -Path $dataDir, $outDir, $logDir, $cacheDir | Out-Null

# ── helpers ──
function Have-Cmd([string]$n) { [bool](Get-Command $n -ErrorAction SilentlyContinue) }
function Is-Admin { try { (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator) } catch { $false } }
function Refresh-Path {
    try {
        $mm = [Environment]::GetEnvironmentVariable('Path','Machine'); $u = [Environment]::GetEnvironmentVariable('Path','User')
        $joined = ((@($mm,$u) | Where-Object { $_ }) -join ';')
        $env:PATH = $joined
        if ($script:PathPrefix) {
            $env:PATH = "$($script:PathPrefix);$joined"
        }
        Get-Command -Name dotnet,winget -ErrorAction SilentlyContinue | Out-Null
    } catch {}
}
function Stale24h { if (-not [IO.File]::Exists($stamp)) { return $true }; try { return (((Get-Date) - [datetime]::Parse(([IO.File]::ReadAllText($stamp)).Trim())).TotalHours -ge 24) } catch { return $true } }
function To-WslPath([string]$w) { $p = $w -replace '\\','/'; if ($p -match '^([A-Za-z]):(.*)$') { '/mnt/' + $Matches[1].ToLower() + $Matches[2] } else { $p } }
function Winget-Present([string]$id) { try { & winget list --id $id -e --disable-interactivity --accept-source-agreements *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } }
function Ensure-Winget([string]$id,[int]$from,[int]$to,[string]$label) {
    if ($script:Headless) {
        Fail ("Headless builds do not install or update tools. Install manually: " + $label)
    }
    if (-not (Have-Cmd 'winget')) { $tools[$label] = 'SKIPPED - winget not available; install manually'; return $false }
    $present = Winget-Present $id
    $log = Join-Path $logDir ("rm_wg_" + ($id -replace '[^\w]','_') + ".log")
    $wgVerb = 'install'
    if ($present) {
        $wgVerb = 'upgrade'
    }
    Invoke-Stage 'winget' @($wgVerb,'--id',$id,'-e','--silent','--accept-package-agreements','--accept-source-agreements','--disable-interactivity') $from $to "$label..." $log 1800 | Out-Null
    Refresh-Path
    $ok = Winget-Present $id
    $tools[$label] = "FAILED (see $log)"
    if ($ok) {
        $tools[$label] = 'installed'
        if ($present) {
            $tools[$label] = 'up to date'
        }
    }
    return $ok
}
function Net11Ver {
    try {
        $version = (& dotnet --version 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -eq 0 -and $version -match '^11\.0\.\d\S*$') {
            return $version
        }
    }
    catch {
        return $null
    }
    return $null
}
function Wg-Exists([string]$id) { if (-not (Have-Cmd 'winget')) { return $false }; try { & winget show --id $id -e --disable-interactivity --accept-source-agreements *> $null; return ($LASTEXITCODE -eq 0) } catch { return $false } }
function Ensure-Dotnet11 {
    Refresh-Path
    if ($script:Headless) {
        $installedVersion = Net11Ver
        if (-not $installedVersion) {
            Fail 'Headless build requires an installed .NET 11 SDK. No SDK installation was attempted.'
        }
        $tools['.NET 11 SDK'] = "present ($installedVersion)"
        return (Get-Command dotnet).Source
    }
    if (-not (Net11Ver)) {
        Update-LoadingPopup 4 "Checking .NET 11 SDK..."
        $id = 'Microsoft.DotNet.SDK.Preview'
        if (Wg-Exists 'Microsoft.DotNet.SDK.11') {
            $id = 'Microsoft.DotNet.SDK.11'
        }
        Ensure-Winget $id 3 6 ".NET 11 SDK" | Out-Null; Refresh-Path
    } else { $tools['.NET 11 SDK'] = "present ($(Net11Ver))" }
    $dn = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not (Net11Ver) -or -not $dn) {
        Fail('The installed SDK cannot satisfy global.json. Install its .NET 11 SDK version, then re-run.')
    }
    return $dn
}
function Get-LinuxBuildDistribution {
    if (-not [string]::IsNullOrWhiteSpace($env:RM_WSL_DISTRO)) {
        return $env:RM_WSL_DISTRO
    }
    return 'Ubuntu-22.04'
}
function Wsl-Ready {
    try {
        $wslCommand = Get-Command wsl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $wslCommand) {
            return $false
        }
        $distribution = Get-LinuxBuildDistribution
        & $wslCommand.Source -d $distribution --exec /bin/true *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}
function HasAndroidWorkload { try { return [bool](((& $dotnet workload list) 2>&1 | Out-String) -match '(?im)^\s*android\b') } catch { return $false } }
function Ensure-AndroidWorkload {
    if (HasAndroidWorkload) { $tools['Android workload'] = 'present'; return }
    if ($script:Headless) {
        Fail 'The .NET Android workload is missing. Install it explicitly before running a headless Android build.'
    }
    $rc = Invoke-Stage $dotnet @('workload','install','android') 8 12 "Installing .NET Android workload (~1 GB)..." (Join-Path $logDir 'rm_wlinstall.log') 2400
    $tools['Android workload'] = 'FAILED - run as admin: dotnet workload install android'
    if ($rc -eq 0 -and (HasAndroidWorkload)) {
        $tools['Android workload'] = 'installed'
    }
}
function OutName([string]$suffix) { Join-Path $outDir ("RatioMaster.NET_{0}_v{1}" -f $suffix,$ver) }
function Find-VCLink([string]$architecture) {
    $installations = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if ([IO.File]::Exists($vswhere)) {
        $instances = (& $vswhere -products '*' -format json 2>$null | Out-String) | ConvertFrom-Json
        $installations = @($instances | Sort-Object { [version]$_.installationVersion } -Descending | ForEach-Object { $_.installationPath })
    }
    foreach ($base in @("${env:ProgramFiles}\Microsoft Visual Studio", "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) {
        if ([IO.Directory]::Exists($base)) {
            $installations += @(Get-ChildItem -Path (Join-Path $base '*\*') -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | ForEach-Object { $_.FullName })
        }
    }
    foreach ($installation in ($installations | Select-Object -Unique)) {
        $toolsets = @(Get-ChildItem -Path (Join-Path $installation 'VC\Tools\MSVC\*') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending)
        foreach ($toolset in $toolsets) {
            $linker = Join-Path $toolset.FullName ('bin\Hostx64\' + $architecture + '\link.exe')
            if ([IO.File]::Exists($linker)) {
                return $linker
            }
        }
    }
    return $null
}
function Find-VCArm64Link {
    return Find-VCLink 'arm64'
}
function Ensure-VCArm64 {
    if (Find-VCArm64Link) { return $true }
    if ($script:Headless) {
        Fail 'MSVC ARM64 build tools are missing. Headless mode does not install Visual Studio components.'
    }
    $base  = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'; $vsw = Join-Path $base 'vswhere.exe'; $setup = Join-Path $base 'setup.exe'
    if (-not [IO.File]::Exists($vsw) -or -not [IO.File]::Exists($setup)) { return $false }
    $inst = & $vsw -latest -products '*' -property installationPath | Select-Object -First 1
    if (-not $inst) {
        return $false
    }
    Invoke-Stage $setup @('modify','--installPath',$inst,'--add','Microsoft.VisualStudio.Component.VC.Tools.ARM64','--quiet','--norestart','--force','--wait') 63 66 "Installing MSVC ARM64 build tools (~2 GB)..." (Join-Path $logDir 'rm_vcarm64.log') | Out-Null
    return [bool](Find-VCArm64Link)
}
function Find-VCx64Link {
    return Find-VCLink 'x64'
}
function Find-WinSdk { foreach ($r in @("${env:ProgramFiles(x86)}\Windows Kits\10\Lib", "$env:ProgramFiles\Windows Kits\10\Lib")) { if ([IO.Directory]::Exists($r)) { $k = Get-ChildItem (Join-Path $r '*\um\x64\kernel32.lib') -ErrorAction SilentlyContinue | Select-Object -First 1; if ($k) { return $k.FullName } } }; return $null }
function Ensure-VSCppX64 {
    if ((Find-VCx64Link) -and (Find-WinSdk)) { $tools['MSVC x64 + Windows SDK'] = 'present'; return $true }
    if ($script:Headless) {
        Fail 'MSVC x64 build tools or Windows SDK are missing. Headless mode does not install Visual Studio components.'
    }
    Update-LoadingPopup 8 "Installing C++ build tools + Windows SDK (Native-AOT)..."
    $base = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'; $vsw = Join-Path $base 'vswhere.exe'; $vsSetup = Join-Path $base 'setup.exe'
    if ([IO.File]::Exists($vsw) -and [IO.File]::Exists($vsSetup)) {
        $inst = & $vsw -latest -products '*' -property installationPath 2>$null | Select-Object -First 1
        if ($inst) {
            Invoke-Stage $vsSetup @('modify','--installPath',$inst,'--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended','--quiet','--norestart','--force','--wait') 8 12 "Adding C++ build tools + Windows SDK to VS (~4 GB)..." (Join-Path $logDir 'rm_vcx64.log') 3600 | Out-Null
        }
    }
    if (((-not (Find-VCx64Link)) -or (-not (Find-WinSdk))) -and (Have-Cmd 'winget')) {
        Invoke-Stage 'winget' @('install','--id','Microsoft.VisualStudio.2022.BuildTools','-e','--silent','--accept-package-agreements','--accept-source-agreements','--override','--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended') 8 12 "Installing VS Build Tools (C++ + Windows SDK, ~4 GB)..." (Join-Path $logDir 'rm_bt_install.log') 3600 | Out-Null
        Refresh-Path
    }
    if ((-not (Find-VCx64Link)) -or (-not (Find-WinSdk))) {
        $bs = Join-Path $env:TEMP 'vs_BuildTools.exe'
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
        Update-LoadingPopup 8 "Downloading VS Build Tools bootstrapper..."
        $got = $false
        try { (New-Object System.Net.WebClient).DownloadFile('https://aka.ms/vs/17/release/vs_BuildTools.exe', $bs); $got = [IO.File]::Exists($bs) -and ((Get-Item $bs).Length -gt 100000) } catch { Log "[toolchain] VS Build Tools bootstrapper download failed: $($_.Exception.Message)" }
        if ($got) {
            Invoke-Stage $bs @('--quiet','--wait','--norestart','--nocache','--add','Microsoft.VisualStudio.Workload.VCTools','--includeRecommended') 9 12 "Installing VS Build Tools (C++ + Windows SDK, ~4 GB)..." (Join-Path $logDir 'rm_bt_install.log') 3600 | Out-Null
            Refresh-Path
            try { Remove-Item $bs -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
    $ok = [bool]((Find-VCx64Link) -and (Find-WinSdk))
    $tools['MSVC x64 + Windows SDK'] = 'MISSING - install VS Build Tools "Desktop development with C++"'
    if ($ok) {
        $tools['MSVC x64 + Windows SDK'] = 'installed'
    } elseif (Find-VCx64Link) {
        $tools['MSVC x64 + Windows SDK'] = 'MSVC present but Windows SDK NOT found'
    }
    return $ok
}
# A running application can lock publish outputs. Only a normal application exit saves session counters.
function Ensure-AppNotRunning {
    $live = @()
    try {
        $live = @(Get-Process -Name 'RatioMaster.NET' -ErrorAction SilentlyContinue | Where-Object {
            $exe = $null; try { $exe = $_.Path } catch {}
            # No readable path -> can't rule it out, so treat it as ours (a locked file is the failure we're
            # preventing). A copy installed elsewhere can't lock OUR publish dir, so it is ignored.
            (-not $exe) -or $exe.StartsWith($appDir + '\', [StringComparison]::OrdinalIgnoreCase) -or $exe.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase)
        })
    } catch { return }
    if (-not $live) { return }

    $ids = ($live | ForEach-Object { $_.Id }) -join ', '
    Log "[app] build blocked: RatioMaster.NET is running (PID $ids) and locks the published exe."
    Fail("RatioMaster.NET is running (PID $ids) and is locking its own published .exe, so the build cannot overwrite it.`r`n`r`nQuit it, then re-run this builder.`r`n`r`nLook in the SYSTEM TRAY (notification area, next to the clock): closing the window only MINIMISES the app there, so it keeps running invisibly. Right-click the RatioMaster tray icon and choose Exit.`r`n`r`n(The builder will not kill it for you: a forced kill skips the session save and would lose the current upload/download counters.)")
}

# Windows Native AOT outputs link their graphics libraries into the executable.
function Test-WindowsStaticArtifact([string]$directory, [string]$rid) {
    $executable = Join-Path $directory 'RatioMaster.NET.exe'
    $reader = $null
    try {
        if (-not [IO.File]::Exists($executable)) {
            throw "Native executable is missing: $executable"
        }
        $nativeLibraries = @(Get-ChildItem -LiteralPath $directory -Filter '*.dll' -File)
        if ($nativeLibraries.Count -gt 0) {
            throw ('The Windows static distribution still contains DLLs: ' + (($nativeLibraries | ForEach-Object { $_.Name }) -join ', '))
        }
        $expectedMachine = 0x8664
        if ($rid -eq 'win-arm64') {
            $expectedMachine = 0xAA64
        }
        elseif ($rid -ne 'win-x64') {
            throw "Unsupported Windows architecture: $rid"
        }
        $reader = New-Object IO.BinaryReader([IO.File]::OpenRead($executable))
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The published file is not a Windows executable.'
        }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt $reader.BaseStream.Length - 264) {
            throw 'The published PE header is truncated.'
        }
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne $expectedMachine) {
            throw "The published PE architecture does not match $rid."
        }
        $reader.BaseStream.Position = $peOffset + 24
        if ($reader.ReadUInt16() -ne 0x20B) {
            throw 'The published executable does not have a PE32+ optional header.'
        }
        $reader.BaseStream.Position = $peOffset + 24 + 112 + 14 * 8
        $clrAddress = $reader.ReadUInt32()
        $clrSize = $reader.ReadUInt32()
        if ($clrAddress -ne 0 -or $clrSize -ne 0) {
            throw 'The published executable contains a CLR header instead of the expected native output.'
        }
        Log "[publish] Verified native $rid executable without graphics DLL sidecars."
        return $true
    }
    catch {
        Log ('[publish] ' + $_.Exception.Message)
        return $false
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }
}

function Publish-WinAot([string]$rid,[string]$out,[int]$from,[int]$to) {
    $log = Join-Path $logDir "ratiomaster_publish_$rid.log"
    $rc = Invoke-Stage $dotnet @('publish', $proj, '-c','Release',"-p:DesktopRid=$rid",'-p:PublishAot=true','-o',$out) $from $to "Building Windows app ($rid, Native-AOT)..." $log
    if ($rc -ne 0) {
        return $false
    }
    return Test-WindowsStaticArtifact $out $rid
}

function Package-WindowsExecutable([string]$rid, [string]$directory) {
    $destination = (OutName $rid) + '.exe'
    try {
        Copy-Item -LiteralPath (Join-Path $directory 'RatioMaster.NET.exe') -Destination $destination -Force
        $built["Windows ($rid, single .exe)"] = $destination
    }
    catch {
        $built["Windows ($rid)"] = 'copy failed: ' + $_.Exception.Message
    }
}
function Package-Linux([string]$dir,[string]$rid) {
    $pkg = (OutName $rid) + '.tar.gz'
    $candidate = $pkg + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Update-LoadingPopup 98 ("Packaging $rid (.tar.gz)...")
        & (Join-Path $scriptDir 'New_LinuxArchive.ps1') -PublishDirectory $dir -ArchivePath $candidate
        if (-not [IO.File]::Exists($candidate)) {
            throw ('Linux archive creation failed for ' + $rid + '.')
        }
        Move-Item -LiteralPath $candidate -Destination $pkg -Force
        $built["Linux ($rid, self-contained JIT .tar.gz)"] = $pkg
        Update-LoadingPopup 99 ("Packaged $rid (.tar.gz).")
    }
    finally {
        if ([IO.File]::Exists($candidate)) {
            Remove-Item -LiteralPath $candidate -Force
        }
    }
}

# ── validate ──
Update-LoadingPopup 6 "Validating sources..."
if (-not [IO.File]::Exists($proj)) { Fail("Project not found: $proj") }

# ── PHASE 0 : tools ──
$daily = Stale24h
if ($script:Headless) {
    $daily = $false
    Log '[setup] Headless mode: existing tools only; no UI, elevation or automatic tool installation.'
}
$dotnet = Ensure-Dotnet11
Update-LoadingPopup 7 "Checking build tools..."
if ($doWinX64Aot -or $doWinArm64) { Ensure-VSCppX64 | Out-Null }
if ($doApk -or $doAab) { Ensure-AndroidWorkload }
if ($script:Headless -and $doWinArm64) {
    Ensure-VCArm64 | Out-Null
}
if ($daily -and $doLinux) {
    if (Wsl-Ready) { $tools['WSL'] = 'ready' }
    else {
        $tools['WSL'] = 'Ubuntu 22.04 build distribution unavailable; Linux uses self-contained archives. See Installer/Scripts/package_linux.sh prerequisites.'
    }
    try { [IO.File]::WriteAllText($stamp, (Get-Date).ToString('o')) } catch {}
} else { $tools['Daily tool check'] = 'skipped (< 24h ago)' }
Update-LoadingPopup 22 "Building..."
if ($argStr -match '(?i)--check-tools\b') {
    Log '[setup] Requested build-tool checks completed; no build was started.'
    Close-LoadingPopup
    exit 0
}

# Windows publishing writes into the portable executable directory and requires that application to be closed.
if ($doWinX64Aot -or $doWinArm64) { Ensure-AppNotRunning }

# ── 1) Windows app : Native-AOT (MSVC's own link.exe first on PATH so a rogue GnuWin32 `link` loses) ──
$vcBin = $null; $vcl = Find-VCx64Link; if ($vcl) { $vcBin = Split-Path $vcl -Parent }
$script:PathPrefix = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ($vcBin) {
    $script:PathPrefix = "$vcBin;$($script:PathPrefix)"
}
$env:PATH = "$($script:PathPrefix);$env:PATH"
if ($doWinX64Aot) {
    if (-not (Publish-WinAot 'win-x64' $pub 22 40)) {
        $wl = Join-Path $logDir 'ratiomaster_publish_win-x64.log'
        $hint = ''
        if (-not ((Find-VCx64Link) -and (Find-WinSdk))) {
            $hint = "`r`n`r`nMSVC x64 C++ build tools or Windows SDK missing. Install Visual Studio Build Tools with Desktop development with C++."
        }
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

# Windows distributions contain the native executable for the selected architecture.
if ($doWinX64Pack) {
    Package-WindowsExecutable 'win-x64' $pub
}
if ($doWinArm64) {
    Update-LoadingPopup 60 "Building win-arm64..."
    if (Ensure-VCArm64) {
        if (Publish-WinAot 'win-arm64' $pubArm 62 70) {
            Package-WindowsExecutable 'win-arm64' $pubArm
        }
        else {
            $built['Windows (win-arm64)'] = "FAILED - AOT publish or static artifact validation failed (see $logDir\ratiomaster_publish_win-arm64.log)"
        }
    }
    else {
        $built['Windows (win-arm64)'] = 'SKIPPED - MSVC ARM64 build tools missing (run the .bat as Administrator to install them)'
    }
}

# ── 4) Android : RELEASE .apk (sideload) and/or .aab (Play App Bundle). CoreCLR = .NET's default Android
#       runtime. Needs the android workload (phase 0) + the Android SDK/JDK (self-acquired to user paths,
#       no admin). aapt2/d8 reject SPACES + non-ASCII in the project path or %TEMP%, so when the path is
#       hostile the build runs through a short ASCII junction with %TEMP% redirected. Best-effort. ──
if ($doApk -or $doAab) {
    Update-LoadingPopup 70 "Checking Android toolchain..."
    if (HasAndroidWorkload) {
        $logDeps = Join-Path $logDir 'ratiomaster_android_deps.log'
        $logApk  = Join-Path $logDir 'ratiomaster_apk.log'
        $logAab  = Join-Path $logDir 'ratiomaster_aab.log'
        $androidSdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
        $androidJdk = Join-Path $env:LOCALAPPDATA 'Android\jdk'
        if ($script:Headless) {
            $hasSdk = [IO.Directory]::Exists((Join-Path $androidSdk 'platform-tools'))
            $hasJdk = [IO.File]::Exists((Join-Path $androidJdk 'bin\keytool.exe'))
            if (-not ($hasSdk -and $hasJdk)) {
                Fail 'Android SDK or JDK is missing from the configured build directories. Headless mode does not install Android dependencies.'
            }
        }
        $androidArgs = @("-p:AndroidSdkDirectory=$androidSdk", "-p:JavaSdkDirectory=$androidJdk", '-p:AcceptAndroidSdkLicenses=True', '-p:IncludeAndroid=true', "-p:ApplicationDisplayVersion=$ver", "-p:ApplicationVersion=$verCode")

        # aapt2 (APT2265) + the D8 dexer choke on spaces / non-ASCII in the PROJECT path or %TEMP%.
        # The junction is only needed when the PROJECT path is hostile.
        $apkProj = $proj; $junction = $null; $savedTemp = $env:TEMP; $savedTmp = $env:TMP
        if ($root -match '[^\x21-\x7E]') {
            $junction = Join-Path $env:SystemDrive ('rm-apk-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Junction -Path $junction -Target $root | Out-Null
            $jp = Join-Path $junction 'RatioMaster.App\RatioMaster.App.csproj'
            if ([IO.File]::Exists($jp)) { $apkProj = $jp }
        }
        # Java resolves short paths to their long form. An ASCII temporary directory avoids spaces and accents.
        $asciiTmp = Join-Path $env:SystemDrive 'rm-tmp'
        try { New-Item -ItemType Directory -Force -Path $asciiTmp | Out-Null; $env:TEMP = $asciiTmp; $env:TMP = $asciiTmp } catch {}
        $savedJavaHome = $env:JAVA_HOME
        $rc = $null
        $rcAab = $null
        try {
            # Acquire/refresh the Android SDK + the JDK .NET needs (user paths, no admin).
            if ((-not [IO.Directory]::Exists((Join-Path $androidSdk 'platform-tools'))) -or (-not [IO.File]::Exists((Join-Path $androidJdk 'bin\keytool.exe'))) -or $daily) {
                if ($script:Headless) {
                    Fail 'Android dependencies are missing; automatic installation is disabled in headless mode.'
                }
                Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-t:InstallAndroidDependencies') + $androidArgs) 70 72 "Installing Android SDK + JDK..." $logDeps | Out-Null
            }
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
                $sign = @()
                if ([IO.File]::Exists($ks)) {
                    $sign = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$ks", '-p:AndroidSigningKeyAlias=androiddebugkey', '-p:AndroidSigningKeyPass=android', '-p:AndroidSigningStorePass=android')
                }
                $signKind = 'debug'
            }
            # CoreCLR is .NET's DEFAULT Android runtime; RunAOTCompilation is Mono-only (XA1044) → not passed.
            if ($doApk) { $rc    = Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-p:AndroidPackageFormat=apk') + $androidArgs + $sign) 72 82 "Building Android APK ($signKind-signed) - slow..." $logApk }
            if ($doAab) { $rcAab = Invoke-Stage $dotnet (@('build', $apkProj, '-c','Release','-f','net11.0-android','-p:AndroidPackageFormat=aab') + $androidArgs + $sign) 82 84 "Building Android App Bundle (.aab)..." $logAab }
        }
        finally {
            $env:JAVA_HOME = $savedJavaHome
            $env:TEMP = $savedTemp; $env:TMP = $savedTmp
            if ($junction) {
                $junctionInfo = Get-Item -LiteralPath $junction -Force
                $expectedJunction = [IO.Path]::GetFullPath($junction)
                $actualJunction = [IO.Path]::GetFullPath($junctionInfo.FullName)
                if ($actualJunction -ne $expectedJunction -or ($junctionInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                    throw 'Refusing to remove an unexpected Android build junction.'
                }
                [IO.Directory]::Delete($actualJunction)
            }
        }
        $logOutput = Join-Path $logDir 'ratiomaster_android_output-path.log'
        $outputResult = Invoke-Stage $dotnet @('msbuild', $proj, '-nologo', '-getProperty:OutputPath', '-p:Configuration=Release', '-p:IncludeAndroid=true', '-p:TargetFramework=net11.0-android') 84 84 'Locating Android build output...' $logOutput
        if ($outputResult -ne 0) {
            Fail ('Unable to locate the Android build output. See ' + $logOutput)
        }
        $abin = (Read-TextRobust $logOutput).Trim()
        if (-not [IO.Path]::IsPathRooted($abin)) {
            $abin = [IO.Path]::GetFullPath((Join-Path $appDir $abin))
        }
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

# Linux native and portable fallback stages share the build runner and canonical outputs.
if ($doLinux) {
    . (Join-Path $scriptDir 'Build_LinuxArtifacts.ps1')
}

# ── summary ──
Update-LoadingPopup 100 "Done"
Close-LoadingPopup
$artLines  = ($built.GetEnumerator() | ForEach-Object { "  - $($_.Key):`r`n      $($_.Value)" }) -join "`r`n"
$toolLines = ''
if ($tools.Count) {
    $toolLines = "`r`nTools:`r`n" + (($tools.GetEnumerator() | ForEach-Object { "  - $($_.Key): $($_.Value)" }) -join "`r`n")
}
$androidNote = ''
if ($doApk -or $doAab) {
    $androidNote = "`r`n`r`nAndroid artifacts use the configured keystore, or a local debug keystore for sideloading. A debug-signed AAB is not suitable for Google Play."
}
$summary = "RatioMaster.NET v$ver - build complete (desktop artifacts are portable, no installer).`r`n`r`n$artLines$toolLines$androidNote`r`n`r`nmacOS (osx-x64/arm64): build on a Mac (dotnet publish -p:DesktopRid=osx-arm64 -p:PublishAot=true)."
Log "`r`n==== SUMMARY ===="
Log $summary
if ($buildLog) { Log "`r`nFull log: $buildLog" }
$failedArtifacts = @($built.Values | Where-Object { [string]$_ -match '(?i)^(FAILED|SKIPPED|copy failed|zip failed|tar failed|packaging failed|built, but no)' })
if ($failedArtifacts.Count -gt 0) {
    Fail ('One or more selected artifacts failed or could not be built. ' + ($failedArtifacts -join '; '))
}

exit 0
