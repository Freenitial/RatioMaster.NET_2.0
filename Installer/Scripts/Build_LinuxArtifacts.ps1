# Linux packages use the Ubuntu 22.04 native pipeline or a clean self-contained host publish.
$linuxTargets = @(
    @{ Rid = 'linux-x64'; Architecture = 'x86_64' },
    @{ Rid = 'linux-arm64'; Architecture = 'aarch64' }
)
$linuxRunId = [guid]::NewGuid().ToString('N')
$linuxScript = Join-Path $dataDir ('rm_linux_' + $linuxRunId + '.sh')

function ConvertTo-BashLiteral([string]$value) {
    $quote = [string][char]39
    $escapedQuote = $quote + '"' + $quote + '"' + $quote
    return $quote + $value.Replace($quote, $escapedQuote) + $quote
}
function Invoke-LinuxStage([string]$body, [string]$name, [string]$logPath, [int]$from, [int]$to) {
    $scriptText = "#!/usr/bin/env bash" + [char]10 + "set -euo pipefail" + [char]10 + $body + [char]10
    [IO.File]::WriteAllText($linuxScript, $scriptText.Replace([string][char]13, ''), (New-Object System.Text.UTF8Encoding($false)))
    $wslCommand = Get-Command wsl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $distribution = Get-LinuxBuildDistribution
    return Invoke-Stage $wslCommand.Source @('-d', $distribution, '-u', 'root', '-e', 'bash', (To-WslPath $linuxScript)) $from $to $name $logPath
}
function Remove-LinuxPackagingScratch([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    $expectedParent = [IO.Path]::GetFullPath($outDir).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $absolute -Leaf) -notmatch ('^\.linux-publish-' + $linuxRunId + '-linux-(x64|arm64)$')) {
        throw 'Refusing to remove an unexpected Linux packaging directory.'
    }
    if (Test-Path -LiteralPath $absolute) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
try {
    $wslAvailable = Wsl-Ready
    $packagingScript = ConvertTo-BashLiteral (To-WslPath (Join-Path $scriptDir 'package_linux.sh'))
    foreach ($target in $linuxTargets) {
        $nativeReady = $false
        if ($wslAvailable) {
            $preflight = 'bash ' + $packagingScript + ' --check-prerequisites ' + $target.Rid
            $preflightResult = Invoke-LinuxStage $preflight ('Checking Ubuntu 22.04 tools for ' + $target.Rid + '...') (Join-Path $logDir ('ratiomaster_linux_preflight_' + $target.Rid + '.log')) 84 85
            if ($preflightResult -ne 0 -and $preflightResult -ne 125) {
                Fail ('Linux preflight failed with exit code ' + $preflightResult + '. Check the preflight log.')
            }
            $nativeReady = $preflightResult -eq 0
        }
        if ($nativeReady) {
            $nativeBody = 'bash ' + $packagingScript + ' ' + $target.Rid + ' ' + (ConvertTo-BashLiteral (To-WslPath $outDir))
            if ($script:Headless) {
                $nativeBody = 'export RM_LINUX_OFFLINE=1' + [char]10 + $nativeBody
            }
            $nativeLog = Join-Path $logDir ('ratiomaster_native_' + $target.Rid + '.log')
            $nativeResult = Invoke-LinuxStage $nativeBody ('Publishing and checking ' + $target.Rid + ' AppImage...') $nativeLog 85 98
            $imagePath = Join-Path $outDir ('RatioMaster.NET_v{0}_{1}.AppImage' -f $ver, $target.Architecture)
            if ($nativeResult -ne 0 -or -not [IO.File]::Exists($imagePath)) {
                Fail ('Linux native packaging failed for ' + $target.Rid + '. See ' + $nativeLog)
            }
            $built['Linux (' + $target.Rid + ', Native AOT .AppImage)'] = $imagePath
            continue
        }
        Log ('[Linux] Ubuntu 22.04 native prerequisites unavailable. Set RM_WSL_DISTRO to a prepared Ubuntu 22.04 distribution. Publishing a self-contained archive for ' + $target.Rid + '.')
        $publishDirectory = Join-Path $outDir ('.linux-publish-' + $linuxRunId + '-' + $target.Rid)
        try {
            New-Item -ItemType Directory -Path $publishDirectory | Out-Null
            $jitLog = Join-Path $logDir ('ratiomaster_linux_' + $target.Rid + '_jit.log')
            $jitResult = Invoke-Stage $dotnet @('publish', $proj, ('-p:DesktopRid=' + $target.Rid), '-p:IncludeAndroid=false', '-c', 'Release',
                '--self-contained', 'true', '-p:PublishAot=false', '-p:DebugType=none', '-o', $publishDirectory) 94 97 ('Publishing ' + $target.Rid + ' self-contained JIT...') $jitLog
            if ($jitResult -ne 0 -or -not [IO.File]::Exists((Join-Path $publishDirectory 'RatioMaster.NET'))) {
                Fail ('Linux self-contained publish failed for ' + $target.Rid + '. See ' + $jitLog)
            }
            Package-Linux $publishDirectory $target.Rid
        }
        finally {
            Remove-LinuxPackagingScratch $publishDirectory
        }
    }
}
finally {
    if ([IO.File]::Exists($linuxScript)) {
        Remove-Item -LiteralPath $linuxScript -Force
    }
}
