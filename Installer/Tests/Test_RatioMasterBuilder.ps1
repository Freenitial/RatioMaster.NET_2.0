param([switch]$CheckWsl)

$ErrorActionPreference = 'Stop'
$installerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptRoot = Join-Path $installerRoot 'Scripts'
$builderPath = Join-Path $installerRoot 'build_RatioMaster_setup.bat'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($builderPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    throw ($errors.Message -join [Environment]::NewLine)
}
$requiredFunctions = @('ConvertTo-ProcessArgument', 'Wait-BuildProcess', 'Invoke-Stage', 'Read-TextRobust', 'To-WslPath', 'Get-LinuxBuildDistribution', 'Wsl-Ready', 'Find-VCLink', 'Find-VCx64Link', 'Find-VCArm64Link')
foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($requiredFunctions -contains $definition.Name) {
        . ([ScriptBlock]::Create($definition.Extent.Text))
    }
}
$script:Headless = $true
function Update-LoadingPopup([int]$percent, [string]$message) {
    Write-Host $message
}
function Log([string]$message) {
    Write-Host $message
}
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ratiomaster-builder-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
try {
    $childPath = Join-Path $testDirectory 'arguments.ps1'
    $outputPath = Join-Path $testDirectory 'arguments.json'
    $stageLog = Join-Path $testDirectory 'stage.log'
    $child = @'
param([string]$OutputPath, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Values)
[IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject $Values))
[Console]::Out.WriteLine('stdout-marker')
[Console]::Error.WriteLine('stderr-marker')
exit 7
'@
    [IO.File]::WriteAllText($childPath, $child, (New-Object System.Text.UTF8Encoding($true)))
    $values = @('plain', 'with space', 'quote"value', 'trailing\', 'shell&value', ('accent-' + [char]0x00E9))
    $powershellExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $result = Invoke-Stage $powershellExe (@('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $childPath, $outputPath) + $values) 0 100 'Checking argument quoting and exit code...' $stageLog 10
    if ($result -ne 7) {
        throw "Unexpected child exit code: $result"
    }
    $actual = [IO.File]::ReadAllText($outputPath) | ConvertFrom-Json
    if ($actual.Count -ne $values.Count) {
        throw 'The runner changed the argument count.'
    }
    for ($index = 0; $index -lt $values.Count; $index++) {
        if ($actual[$index] -cne $values[$index]) {
            throw "The runner changed argument $index."
        }
    }
    $logText = [IO.File]::ReadAllText($stageLog)
    if (-not $logText.Contains('stdout-marker') -or -not $logText.Contains('stderr-marker')) {
        throw 'The runner failed to capture both output streams.'
    }
    $blockingChild = Join-Path $testDirectory 'wait.ps1'
    [IO.File]::WriteAllText($blockingChild, '$event = New-Object System.Threading.ManualResetEvent($false); $null = $event.WaitOne()', (New-Object System.Text.UTF8Encoding($true)))
    $timeoutResult = Invoke-Stage $powershellExe @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $blockingChild) 0 100 'Checking the real stage deadline...' (Join-Path $testDirectory 'deadline.log') 1
    if ($timeoutResult -ne 1460) {
        throw 'A timed-out stage did not report its timeout exit code.'
    }
    if ((ConvertTo-ProcessArgument '-u') -ne '-u') {
        throw 'An option without special characters must remain unquoted for command-line parsers such as WSL.'
    }
    if ($CheckWsl) {
        if (-not (Wsl-Ready)) {
            throw 'WSL is not available for the requested real transport check.'
        }
        $linuxHelper = Join-Path $scriptRoot 'Build_LinuxArtifacts.ps1'
        $linuxAst = [Management.Automation.Language.Parser]::ParseFile($linuxHelper, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) {
            throw ($errors.Message -join [Environment]::NewLine)
        }
        $linuxFunction = $linuxAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-LinuxStage' }, $true) | Select-Object -First 1
        . ([ScriptBlock]::Create($linuxFunction.Extent.Text))
        $literalFunction = $linuxAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-BashLiteral' }, $true) | Select-Object -First 1
        . ([ScriptBlock]::Create($literalFunction.Extent.Text))
        $linuxScript = Join-Path $installerRoot ('Data\rm_linux_probe_' + [guid]::NewGuid().ToString('N') + '.sh')
        try {
            $prepare = 'bash ' + (ConvertTo-BashLiteral (To-WslPath (Join-Path $scriptRoot 'package_linux.sh'))) + ' --check-prerequisites linux-x64'
            $wslResult = Invoke-LinuxStage $prepare 'Checking real WSL invocation and existing native tools...' (Join-Path $testDirectory 'wsl-preflight.log') 0 100
            if ($wslResult -eq 0) {
                Write-Host 'PASS: WSL invocation and native tool prerequisites are available.'
            } elseif ($wslResult -eq 125) {
                Write-Host 'PASS: WSL invocation worked and reported specifically missing native prerequisites.'
            } else {
                throw ("WSL invocation or preflight failed with unexpected exit code " + $wslResult)
            }
        } finally {
            if ([IO.File]::Exists($linuxScript)) {
                Remove-Item -LiteralPath $linuxScript -Force
            }
        }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if ([IO.File]::Exists($vswhere)) {
        $latest = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
        if ($latest) {
            $linker = Find-VCx64Link
            if (-not $linker -or -not $linker.StartsWith($latest + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The x64 compiler lookup did not select the newest installed Visual Studio instance.'
            }
            Write-Host ('PASS: Selected installed compiler ' + $linker)
        }
    }
    Write-Host 'PASS: PowerShell 5.1 parsing, exact arguments, output capture, exit codes and process deadline.'
} finally {
    $absolute = [IO.Path]::GetFullPath($testDirectory)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $absolute -Leaf) -notmatch '^ratiomaster-builder-test-[a-f0-9]{32}$') {
        throw 'Refusing to remove an unexpected test directory.'
    }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
