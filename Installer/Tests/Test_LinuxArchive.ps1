$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ratiomaster-archive-test-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $scratch ('publish space-' + [char]0x00E9)
$archive = Join-Path $scratch 'linux package.tar.gz'
$extracted = Join-Path $scratch 'extracted'
try {
    New-Item -ItemType Directory -Path $publish, $extracted | Out-Null
    $subdirectory = Join-Path $publish 'nested # files'
    New-Item -ItemType Directory -Path $subdirectory | Out-Null
    $binary = [byte[]]@(0, 1, 127, 128, 255)
    [IO.File]::WriteAllBytes((Join-Path $publish 'RatioMaster.NET'), $binary)
    [IO.File]::WriteAllBytes((Join-Path $publish 'createdump'), $binary)
    [IO.File]::WriteAllBytes((Join-Path $subdirectory ('data-' + [char]0x00E9 + '.dll')), $binary)
    $longName = ('a' * 95) + '.dll'
    [IO.File]::WriteAllBytes((Join-Path $subdirectory $longName), $binary)
    foreach ($length in @(0, 512, 513)) {
        $block = New-Object byte[] $length
        if ($length -gt 0) {
            $block[$length - 1] = 255
        }
        [IO.File]::WriteAllBytes((Join-Path $publish ('block-' + $length + '.bin')), $block)
    }
    [IO.File]::WriteAllText((Join-Path $publish 'ratiomaster.session'), 'fixture data')
    [IO.File]::WriteAllText((Join-Path $publish 'ratiomaster.session.bak'), 'fixture backup')
    & (Join-Path $PSScriptRoot '../Scripts/New_LinuxArchive.ps1') -PublishDirectory $publish -ArchivePath $archive
    $listing = (& tar.exe -tvf $archive | Out-String)
    if ($LASTEXITCODE -ne 0 -or $listing -notmatch '(?m)^-rwxr-xr-x\s+.*RatioMaster\.NET\r?$' -or
        $listing -notmatch '(?m)^-rwxr-xr-x\s+.*createdump\r?$') {
        throw 'The Linux apphost does not have mode 0755 in the archive.'
    }
    if ($listing -match '\.session') {
        throw 'The Linux archive contains session data.'
    }
    & tar.exe -xf $archive -C $extracted
    if ($LASTEXITCODE -ne 0) {
        throw 'The Linux archive could not be extracted.'
    }
    foreach ($relative in @('RatioMaster.NET', 'createdump', ('nested # files/data-' + [char]0x00E9 + '.dll'), ('nested # files/' + $longName))) {
        $restored = [IO.File]::ReadAllBytes((Join-Path $extracted $relative))
        if ([Convert]::ToBase64String($restored) -cne [Convert]::ToBase64String($binary)) {
            throw ('The Linux archive changed file contents: ' + $relative)
        }
    }
    foreach ($length in @(0, 512, 513)) {
        $restored = [IO.File]::ReadAllBytes((Join-Path $extracted ('block-' + $length + '.bin')))
        if ($restored.Length -ne $length -or ($length -gt 0 -and $restored[$length - 1] -ne 255)) {
            throw 'The Linux archive has invalid block padding or entry boundaries.'
        }
    }
    $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($archive))
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot '../Scripts/New_LinuxArchive.ps1') -PublishDirectory $publish -ArchivePath $archive
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($archive)) -cne $before) {
        throw 'An existing Linux archive must remain untouched.'
    }
    Write-Host 'PASS: Linux executable modes, Unicode and long paths, binary contents, session exclusion, existing archive preservation.'
}
finally {
    $absolute = [IO.Path]::GetFullPath($scratch)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $absolute -Leaf) -notmatch '^ratiomaster-archive-test-[a-f0-9]{32}$') {
        throw 'Refusing to remove an unexpected archive test directory.'
    }
    if (Test-Path -LiteralPath $absolute) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
