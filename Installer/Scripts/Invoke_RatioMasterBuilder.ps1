param(
    [Parameter(Mandatory = $true)]
    [string]$BuilderPath
)

$ErrorActionPreference = 'Stop'
$batFile = [IO.Path]::GetFullPath($BuilderPath)
$env:RM_BUILD_HEADLESS = '1'
try {
    & ([ScriptBlock]::Create([IO.File]::ReadAllText($batFile)))
    exit 0
} catch {
    [Console]::Error.WriteLine('[ERROR] ' + $_.Exception.Message)
    exit 1
}
