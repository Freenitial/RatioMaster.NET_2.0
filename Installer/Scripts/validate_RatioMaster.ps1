[CmdletBinding()]
param(
    [ValidateSet('Auto', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$DesktopRid = 'Auto'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $installerRoot '..'))
$projectPath = Join-Path $repositoryRoot 'RatioMaster.App/RatioMaster.App.csproj'
$outputRoot = Join-Path $installerRoot 'Output'
$logRoot = Join-Path $repositoryRoot 'artifacts/logs'
$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop | Select-Object -First 1
$dotnetPath = $dotnetCommand.Source
$runningOnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$buildMutex = $null
$mutexOwned = $false
$validationLock = $null
$lockOwned = $false
$scratchPath = $null
$locationPushed = $false
$exitStatus = 1
$savedEnvironment = @{}
$script:ValidationLogDirectory = $null

function Invoke-CheckedDotnet {
    param(
        [string[]]$Arguments,
        [string]$LogName,
        [switch]$ReturnOutput
    )

    Write-Host ('dotnet ' + ($Arguments -join ' '))
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $dotnetPath @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    $lines | ForEach-Object { Write-Host $_ }
    if ($script:ValidationLogDirectory -and $LogName) {
        $logPath = Join-Path $script:ValidationLogDirectory $LogName
        [IO.File]::WriteAllLines($logPath, [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
    }
    if ($code -ne 0) {
        throw ('dotnet failed with exit code {0}: {1}' -f $code, ($Arguments -join ' '))
    }
    if ($ReturnOutput) {
        return $lines
    }
}

try {
    Push-Location -LiteralPath $repositoryRoot
    $locationPushed = $true
    foreach ($name in @('DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_CLI_UI_LANGUAGE', 'TEMP', 'TMP', 'TMPDIR')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_NOLOGO', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_UI_LANGUAGE', 'en', 'Process')

    if ($runningOnWindows) {
        $buildMutex = New-Object Threading.Mutex($false, 'Local\RatioMaster_build_setup')
        try {
            $mutexOwned = $buildMutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $mutexOwned = $true
        }
        if (-not $mutexOwned) {
            throw 'Another RatioMaster build is running. Validation must run after that build.'
        }
    }

    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $lockPath = Join-Path $outputRoot '.validation-package.lock'
    try {
        $validationLock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $lockOwned = $true
        $lockBytes = [Text.Encoding]::UTF8.GetBytes(('validation PID={0}' -f $PID))
        $validationLock.Write($lockBytes, 0, $lockBytes.Length)
        $validationLock.Flush()
    }
    catch [IO.IOException] {
        throw ('Validation or macOS packaging is already running. If an interrupted process left {0}, verify that no build is running before removing that file.' -f $lockPath)
    }

    $information = (Invoke-CheckedDotnet -Arguments @('--info') -ReturnOutput) -join [Environment]::NewLine
    if ($information -notmatch '(?m)^\s*RID:\s*([^\s]+)\s*$') {
        throw 'Unable to determine the installed dotnet host runtime identifier.'
    }
    $hostRuntime = $Matches[1]
    $hostFamily = ''
    switch -Regex ($hostRuntime) {
        '^win' { $hostFamily = 'win'; break }
        '^linux|^ubuntu|^debian|^fedora|^rhel|^centos|^alpine' { $hostFamily = 'linux'; break }
        '^osx' { $hostFamily = 'osx'; break }
        default { throw ('Unsupported dotnet host: ' + $hostRuntime) }
    }
    $hostArchitecture = 'x64'
    if ($hostRuntime -match '-arm64$') {
        $hostArchitecture = 'arm64'
    }
    elseif ($hostRuntime -notmatch '-x64$') {
        throw ('Only x64 and arm64 validation hosts are supported: ' + $hostRuntime)
    }
    $nativeRid = $hostFamily + '-' + $hostArchitecture
    if ($DesktopRid -eq 'Auto') {
        $DesktopRid = $nativeRid
    }
    if ($DesktopRid -ne $nativeRid) {
        throw ('Validation executes the built program and requires the dotnet host RID {0}; requested {1}.' -f $nativeRid, $DesktopRid)
    }

    $script:ValidationLogDirectory = Join-Path $logRoot ('validation/' + $DesktopRid)
    [IO.Directory]::CreateDirectory($script:ValidationLogDirectory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:ValidationLogDirectory 'dotnet-info.log'), $information, (New-Object Text.UTF8Encoding($false)))
    $scratchPath = [IO.Path]::GetFullPath((Join-Path $script:ValidationLogDirectory ('work-' + [Guid]::NewGuid().ToString('N'))))
    [IO.Directory]::CreateDirectory($scratchPath) | Out-Null
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) {
        [Environment]::SetEnvironmentVariable($name, $scratchPath, 'Process')
    }

    $buildProperties = @("-p:DesktopRid=$DesktopRid", '-p:IncludeAndroid=false')
    Invoke-CheckedDotnet -Arguments (@('build', $projectPath, '-c', 'Debug', '--nologo', '--disable-build-servers') + $buildProperties) -LogName 'build.log'
    $targetLines = Invoke-CheckedDotnet -Arguments (@('msbuild', $projectPath, '-nologo', '-nr:false', '-getProperty:TargetPath', '-p:Configuration=Debug') + $buildProperties) -LogName 'target-path.log' -ReturnOutput
    $targetPath = ($targetLines -join [Environment]::NewLine).Trim()
    if (-not [IO.File]::Exists($targetPath)) {
        throw ('The Debug assembly was not produced: ' + $targetPath)
    }

    Invoke-CheckedDotnet -Arguments @($targetPath, '--selftest') -LogName 'selftest.log'
    Invoke-CheckedDotnet -Arguments @($targetPath, '--ui-selftest') -LogName 'ui-selftest.log'
    Invoke-CheckedDotnet -Arguments @($targetPath, '--runtime-selftest') -LogName 'runtime-selftest.log'
    $exitStatus = 0
}
catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    if ($scratchPath -and [IO.Directory]::Exists($scratchPath)) {
        try {
            $resolvedScratch = [IO.Path]::GetFullPath($scratchPath)
            $allowedPrefix = [IO.Path]::GetFullPath($script:ValidationLogDirectory) + [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedScratch.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to remove an unexpected validation scratch directory.'
            }
            Remove-Item -LiteralPath $resolvedScratch -Recurse -Force -ErrorAction Stop
        }
        catch {
            $exitStatus = 1
            Write-Error -Message ('Validation cleanup failed: ' + $_.Exception.Message) -ErrorAction Continue
        }
    }
    if ($validationLock) {
        $validationLock.Dispose()
    }
    if ($lockOwned) {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction Continue
    }
    if ($mutexOwned) {
        $buildMutex.ReleaseMutex()
    }
    if ($buildMutex) {
        $buildMutex.Dispose()
    }
    if ($locationPushed) {
        Pop-Location
    }
}
if ($exitStatus -eq 0) {
    Write-Host ('Validation passed for {0}. Logs: {1}' -f $DesktopRid, $script:ValidationLogDirectory)
}
exit $exitStatus

