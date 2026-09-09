# Linux stages use the same process runner, output directories and packaging helpers as desktop builds.
$linuxTargets = @(
    @{ Rid = 'linux-x64'; Directory = $linux; Architecture = 'x86_64' },
    @{ Rid = 'linux-arm64'; Directory = $linuxArm; Architecture = 'aarch64' }
)
$linuxRunId = [guid]::NewGuid().ToString('N')
$linuxScript = Join-Path $dataDir ('rm_linux_' + $linuxRunId + '.sh')
$linuxWrapper = Join-Path $dataDir ('rm_aarch64_' + $linuxRunId)
$linuxNativeReady = $false

function ConvertTo-BashLiteral([string]$value) {
    $quote = [string][char]39
    $escapedQuote = $quote + '"' + $quote + '"' + $quote
    return $quote + $value.Replace($quote, $escapedQuote) + $quote
}
function Invoke-LinuxStage([string]$body, [string]$name, [string]$logPath, [int]$from, [int]$to) {
    $scriptText = "#!/usr/bin/env bash" + [char]10 + "set -euo pipefail" + [char]10 + $body + [char]10
    [IO.File]::WriteAllText($linuxScript, $scriptText.Replace([string][char]13, ''), (New-Object System.Text.UTF8Encoding($false)))
    $wslCommand = Get-Command wsl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    return Invoke-Stage $wslCommand.Source @('-u', 'root', '-e', 'bash', (To-WslPath $linuxScript)) $from $to $name $logPath
}
function Remove-LinuxPackagingScratch([string]$path, [string]$parent) {
    $absolute = [IO.Path]::GetFullPath($path)
    $expectedParent = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
    $expectedName = 'AppDir-' + $linuxRunId
    if (-not $absolute.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $absolute -Leaf) -ne $expectedName) {
        throw 'Refusing to remove an unexpected Linux packaging directory.'
    }
    if (Test-Path -LiteralPath $absolute) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
try {
    $wrapperBody = @'
#!/usr/bin/env bash
args=()
for a in "$@"; do
    case "$a" in
        --target=*|--gcc-toolchain=*) ;;
        *) args+=("$a");;
    esac
done
exec aarch64-linux-gnu-gcc "${args[@]}"
'@
    [IO.File]::WriteAllText($linuxWrapper, $wrapperBody.Replace([string][char]13, ''), (New-Object System.Text.UTF8Encoding($false)))
    $dotnetSelector = @'
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^11\.0\.'; then
    DOTNET=dotnet
elif [ -x /root/.dotnet/dotnet ] && /root/.dotnet/dotnet --list-sdks | grep -q '^11\.0\.'; then
    DOTNET=/root/.dotnet/dotnet
else
    echo '[WSL] .NET 11 SDK missing.'
    exit 125
fi
"$DOTNET" --list-sdks | grep -q '^11\.0\.'
'@
    if (Wsl-Ready) {
        $prepare = @'
for tool in clang aarch64-linux-gnu-gcc aarch64-linux-gnu-objcopy; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        echo "[WSL] Required native tool missing: $tool"
        exit 125
    fi
done
if [ ! -f /usr/include/zlib.h ]; then
    echo '[WSL] zlib development headers missing.'
    exit 125
fi
__DOTNET__
'@
        if (-not $script:Headless) {
            $bootstrap = @'
export DEBIAN_FRONTEND=noninteractive
if ! command -v clang >/dev/null 2>&1 || ! command -v aarch64-linux-gnu-gcc >/dev/null 2>&1 || [ ! -f /usr/include/zlib.h ]; then
    apt-get update -y
    apt-get install -y clang zlib1g-dev libicu-dev curl ca-certificates gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu file
fi
if ! command -v dotnet >/dev/null 2>&1 && [ ! -x /root/.dotnet/dotnet ]; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/ratiomaster-dotnet-install.sh
    bash /tmp/ratiomaster-dotnet-install.sh --channel 11.0 --quality preview --install-dir /root/.dotnet
fi
'@
            $prepare = $bootstrap + [char]10 + $prepare
        }
        $prepare = $prepare.Replace('__DOTNET__', $dotnetSelector)
        $prepareResult = Invoke-LinuxStage $prepare 'Checking existing WSL native tools...' (Join-Path $env:TEMP 'ratiomaster_linux_preflight.log') 84 85
        if ($prepareResult -ne 0 -and $prepareResult -ne 125) {
            Fail ("WSL native preflight failed with exit code " + $prepareResult + '. This is not a missing-tool verdict. See ratiomaster_linux_preflight.log.')
        }
        $linuxNativeReady = $prepareResult -eq 0
    }
    if (-not $linuxNativeReady) {
        Log '[Linux] Existing WSL native tools are unavailable; building self-contained JIT archives with the host SDK.'
    }
    foreach ($target in $linuxTargets) {
        $published = $false
        $native = $false
        if ($linuxNativeReady) {
            $compilerArguments = ''
            if ($target.Rid -eq 'linux-arm64') {
                $compilerArguments = '-p:CppCompilerAndLinker=' + (ConvertTo-BashLiteral (To-WslPath $linuxWrapper)) + ' -p:ObjCopyName=aarch64-linux-gnu-objcopy'
            }
            $nativeBody = @'
__DOTNET__
chmod +x __WRAPPER__
"$DOTNET" publish __PROJECT__ -c Release -p:DesktopRid=__RID__ -p:PublishAot=true -p:DebugType=none __COMPILER__ -o __OUTPUT__
test -f __BINARY__
'@
            $nativeBody = $nativeBody.Replace('__DOTNET__', $dotnetSelector).
                Replace('__WRAPPER__', (ConvertTo-BashLiteral (To-WslPath $linuxWrapper))).
                Replace('__PROJECT__', (ConvertTo-BashLiteral (To-WslPath $proj))).
                Replace('__RID__', $target.Rid).
                Replace('__COMPILER__', $compilerArguments).
                Replace('__OUTPUT__', (ConvertTo-BashLiteral (To-WslPath $target.Directory))).
                Replace('__BINARY__', (ConvertTo-BashLiteral (To-WslPath (Join-Path $target.Directory 'RatioMaster.NET'))))
            $nativeResult = Invoke-LinuxStage $nativeBody ("Publishing Native AOT " + $target.Rid + '...') (Join-Path $env:TEMP ("ratiomaster_native_" + $target.Rid + '.log')) 85 94
            $published = $nativeResult -eq 0 -and [IO.File]::Exists((Join-Path $target.Directory 'RatioMaster.NET'))
            $native = $published
        }
        if (-not $published) {
            Log ("[Linux] Publishing self-contained JIT fallback for " + $target.Rid + '.')
            $jitLog = Join-Path $env:TEMP ("ratiomaster_linux_" + $target.Rid + '_jit.log')
            $jitResult = Invoke-Stage $dotnet @('publish', $proj, ('-p:DesktopRid=' + $target.Rid), '-c', 'Release',
                '--self-contained', 'true', '-p:PublishAot=false', '-p:DebugType=none', '-o', $target.Directory) 94 97 ("Publishing " + $target.Rid + ' self-contained JIT...') $jitLog
            $published = $jitResult -eq 0 -and [IO.File]::Exists((Join-Path $target.Directory 'RatioMaster.NET'))
        }
        if (-not $published) {
            $built["Linux ($($target.Rid))"] = 'FAILED - publish did not produce a validated output in this run'
            continue
        }
        $packagedAppImage = $false
        if ($native) {
            $appDirectory = Join-Path $target.Directory ('AppDir-' + $linuxRunId)
            New-Item -ItemType Directory -Path $appDirectory | Out-Null
            $imageCandidate = Join-Path $outDir ('ratiomaster-' + $linuxRunId + '-' + $target.Architecture + '.AppImage')
            try {
                $packageBody = @'
C=/root/.cache/rm-appimage
__APPIMAGE_TOOLS__
test -x "$C/appimagetool"
test -f "$C/runtime-__ARCH__"
mkdir -p __APPDIR__/usr/bin
cp -f __BINARY__ __APPDIR__/usr/bin/
find __PUBLISH__ -maxdepth 1 -type f -name '*.so' -exec cp -f '{}' __APPDIR__/usr/bin/ \;
cp -f __APPRUN__ __APPDIR__/AppRun
sed -i 's/\r$//' __APPDIR__/AppRun
chmod +x __APPDIR__/AppRun
cp -f __DESKTOP__ __APPDIR__/RatioMaster.desktop
sed -i 's/\r$//' __APPDIR__/RatioMaster.desktop
cp -f __ICON__ __APPDIR__/ratiomaster.png
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=__ARCH__ "$C/appimagetool" --runtime-file "$C/runtime-__ARCH__" __APPDIR__ __IMAGE__
'@
                $imageTools = ''
                if (-not $script:Headless) {
                    $imageTools = @'
mkdir -p "$C"
if [ ! -x "$C/appimagetool" ]; then
    curl -fsSL -o "$C/appimagetool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
    chmod +x "$C/appimagetool"
fi
if [ ! -f "$C/runtime-__ARCH__" ]; then
    curl -fsSL -o "$C/runtime-__ARCH__" https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-__ARCH__
fi
'@
                }
                $packageBody = $packageBody.Replace('__APPIMAGE_TOOLS__', $imageTools).
                    Replace('__ARCH__', $target.Architecture).
                    Replace('__APPDIR__', (ConvertTo-BashLiteral (To-WslPath $appDirectory))).
                    Replace('__BINARY__', (ConvertTo-BashLiteral (To-WslPath (Join-Path $target.Directory 'RatioMaster.NET')))).
                    Replace('__PUBLISH__', (ConvertTo-BashLiteral (To-WslPath $target.Directory))).
                    Replace('__APPRUN__', (ConvertTo-BashLiteral (To-WslPath $appRun))).
                    Replace('__DESKTOP__', (ConvertTo-BashLiteral (To-WslPath $desktop))).
                    Replace('__ICON__', (ConvertTo-BashLiteral (To-WslPath $iconPng))).
                    Replace('__IMAGE__', (ConvertTo-BashLiteral (To-WslPath $imageCandidate)))
                $imageResult = Invoke-LinuxStage $packageBody ("Packaging " + $target.Rid + ' AppImage...') (Join-Path $env:TEMP ("ratiomaster_appimage_" + $target.Rid + '.log')) 97 98
                if ($imageResult -eq 0 -and [IO.File]::Exists($imageCandidate)) {
                    $imagePath = Join-Path $outDir ("RatioMaster.NET_v{0}_{1}.AppImage" -f $ver, $target.Architecture)
                    Move-Item -LiteralPath $imageCandidate -Destination $imagePath -Force
                    $built["Linux ($($target.Rid), Native AOT .AppImage)"] = $imagePath
                    $packagedAppImage = $true
                } else {
                    Log ("[Linux] AppImage tooling unavailable or packaging failed for " + $target.Rid + '; packaging the successful native build as an archive.')
                }
            } finally {
                Remove-LinuxPackagingScratch $appDirectory $target.Directory
                if ([IO.File]::Exists($imageCandidate)) {
                    Remove-Item -LiteralPath $imageCandidate -Force
                }
            }
        }
        if (-not $packagedAppImage) {
            $runtimeKind = 'self-contained JIT'
            if ($native) {
                $runtimeKind = 'Native AOT'
            }
            Log ("[Linux] Archive runtime: " + $target.Rid + ', ' + $runtimeKind)
            Package-Linux $target.Directory $target.Rid
        }
    }
} finally {
    foreach ($generatedScript in @($linuxScript, $linuxWrapper)) {
        if ([IO.File]::Exists($generatedScript)) {
            Remove-Item -LiteralPath $generatedScript -Force
        }
    }
}
