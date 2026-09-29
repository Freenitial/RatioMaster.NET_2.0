param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

function Set-TarText([byte[]]$header, [int]$offset, [int]$length, [string]$value) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($value)
    if ($bytes.Length -gt $length) {
        throw 'A tar header field exceeds its supported length.'
    }
    [Array]::Copy($bytes, 0, $header, $offset, $bytes.Length)
}

function Write-TarPadding([IO.Stream]$output, [long]$length) {
    $padding = [int]((512 - ($length % 512)) % 512)
    if ($padding -gt 0) {
        $output.Write((New-Object byte[] $padding), 0, $padding)
    }
}

function Write-TarHeader([IO.Stream]$output, [string]$name, [long]$size, [int]$mode, [char]$type) {
    if ($size -lt 0 -or $size -gt 8589934591) {
        throw 'A Linux package entry exceeds the tar header size limit.'
    }
    $header = New-Object byte[] 512
    Set-TarText $header 0 100 $name
    Set-TarText $header 100 7 ([Convert]::ToString($mode, 8).PadLeft(7, '0'))
    Set-TarText $header 108 7 '0000000'
    Set-TarText $header 116 7 '0000000'
    Set-TarText $header 124 11 ([Convert]::ToString($size, 8).PadLeft(11, '0'))
    Set-TarText $header 136 11 '00000000000'
    Set-TarText $header 148 8 '        '
    $header[156] = [byte]$type
    Set-TarText $header 257 5 'ustar'
    Set-TarText $header 263 2 '00'
    $checksum = 0
    foreach ($value in $header) {
        $checksum += $value
    }
    Set-TarText $header 148 6 ([Convert]::ToString($checksum, 8).PadLeft(6, '0'))
    $header[154] = 0
    $header[155] = 32
    $output.Write($header, 0, $header.Length)
}

function Write-TarPath([IO.Stream]$output, [string]$path, [int]$index) {
    # PAX paths preserve UTF-8 filenames and paths longer than the fixed ustar header fields.
    $value = [Text.Encoding]::UTF8.GetBytes('path=' + $path + [char]10)
    $length = $value.Length + 2
    do {
        $previous = $length
        $text = $length.ToString([Globalization.CultureInfo]::InvariantCulture) + ' '
        $length = $text.Length + $value.Length
    }
    while ($length -ne $previous)
    $prefix = [Text.Encoding]::ASCII.GetBytes($text)
    Write-TarHeader $output ('PaxHeaders/' + $index) $length 420 'x'
    $output.Write($prefix, 0, $prefix.Length)
    $output.Write($value, 0, $value.Length)
    Write-TarPadding $output $length
}

function Write-LinuxDirectory([IO.Stream]$output, [string]$directory, [string]$relative, [ref]$index) {
    foreach ($item in (Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name)) {
        if ($item.Name -like '*.session' -or $item.Name -like '*.session.bak' -or $item.Name -eq 'sessions.json' -or
            ($relative.Length -eq 0 -and $item.Name -like 'AppDir*')) {
            continue
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('Linux publish entries must not be links or reparse points: ' + $item.FullName)
        }
        $entryPath = $relative + $item.Name
        $index.Value++
        $entryName = 'entry-' + $index.Value
        if ($item.PSIsContainer) {
            Write-TarPath $output ($entryPath + '/') $index.Value
            Write-TarHeader $output $entryName 0 493 '5'
            Write-LinuxDirectory $output $item.FullName ($entryPath + '/') $index
            continue
        }
        $mode = 420
        if ($entryPath -in @('RatioMaster.NET', 'createdump')) {
            $mode = 493
        }
        $inputFile = [IO.File]::OpenRead($item.FullName)
        try {
            Write-TarPath $output $entryPath $index.Value
            Write-TarHeader $output $entryName $inputFile.Length $mode '0'
            $inputFile.CopyTo($output)
            Write-TarPadding $output $inputFile.Length
        }
        finally {
            $inputFile.Dispose()
        }
    }
}

$source = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\', '/')
$destination = [IO.Path]::GetFullPath($ArchivePath)
if (-not [IO.Directory]::Exists($source)) {
    throw 'The Linux publish directory does not exist.'
}
if (-not [IO.File]::Exists((Join-Path $source 'RatioMaster.NET'))) {
    throw 'The Linux publish directory does not contain RatioMaster.NET.'
}
if ($destination.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Linux archive must be written outside its publish directory.'
}

$archive = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $compressed = New-Object IO.Compression.GZipStream($archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    try {
        $entryIndex = 0
        Write-LinuxDirectory $compressed $source '' ([ref]$entryIndex)
        $compressed.Write((New-Object byte[] 1024), 0, 1024)
    }
    finally {
        $compressed.Dispose()
    }
}
finally {
    $archive.Dispose()
}
