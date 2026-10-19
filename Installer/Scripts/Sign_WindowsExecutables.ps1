[CmdletBinding(PositionalBinding = $false)]
param(
    # Executables to sign; all are signed in one signtool call, so a hardware token asks for its PIN once.
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Path,

    # SHA-1 thumbprint of the code-signing certificate; by default the only valid one with a private key.
    [string]$Thumbprint = $env:RM_SIGN_THUMBPRINT,

    # RFC 3161 time-stamping service: the signature stays valid after the certificate expires.
    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    # Only checks files already signed with the certificate (no PIN needed).
    [switch]$VerifyOnly
)

<#
    Authenticode signing of the Windows executables with the developer's code-signing certificate
    (SHA-256 file digest and SHA-256 RFC 3161 timestamp), then verification. The private key may live on a
    hardware token: its own driver asks for the PIN; this script never handles it.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The .NET store is read directly: Windows PowerShell does not load the Cert: drive in a -NoProfile session.
$codeSigning = '1.3.6.1.5.5.7.3.3'
$now = Get-Date
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
$store.Open('ReadOnly')
try {
    $certificates = @($store.Certificates | Where-Object {
        $usages = @($_.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
        $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now -and $usages -contains $codeSigning
    })
}
finally {
    $store.Close()
}
if ($Thumbprint) {
    $wanted = ($Thumbprint -replace '\s', '').ToUpperInvariant()
    $certificates = @($certificates | Where-Object { $_.Thumbprint -eq $wanted })
}
if ($certificates.Count -eq 0) {
    throw 'No valid code-signing certificate with a private key is available in the current user store (is the token connected?).'
}
if ($certificates.Count -gt 1) {
    $choices = ($certificates | ForEach-Object { $_.Thumbprint + ' (' + $_.Subject + ')' }) -join '; '
    throw ('Several code-signing certificates are available; choose one with -Thumbprint or RM_SIGN_THUMBPRINT: ' + $choices)
}
$certificate = $certificates[0]

$hostArchitecture = 'x64'
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
    $hostArchitecture = 'arm64'
}
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signtool = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName "$hostArchitecture\signtool.exe" } |
    Where-Object { [IO.File]::Exists($_) } |
    Select-Object -First 1
if (-not $signtool) {
    throw 'signtool.exe was not found; install the signing tools of the Windows SDK.'
}

$files = @($Path | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($files.Count -eq 0) {
    throw 'No executable to sign.'
}

function Invoke-SignTool([string[]]$Arguments) {
    # Windows PowerShell turns redirected native error output into errors; the exit code decides here.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $script:SignToolOutput = @(& $signtool @Arguments 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    $script:SignToolOutput | ForEach-Object { Write-Host $_ }
    return $code
}

if (-not $VerifyOnly) {
    Write-Host ('Signing {0} file(s) with {1} ({2}).' -f $files.Count, $certificate.Subject, $certificate.Thumbprint)
    $code = Invoke-SignTool (@('sign', '/sha1', $certificate.Thumbprint, '/fd', 'sha256', '/d', 'RatioMaster.NET',
        '/du', 'https://github.com/Freenitial/RatioMaster.NET_2.0') + $files)
    if ($code -ne 0) {
        throw "signtool sign failed with exit code $code."
    }

    # Time-stamping needs no key: it is retried on its own when the service is briefly unavailable.
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $code = Invoke-SignTool (@('timestamp', '/tr', $TimestampUrl, '/td', 'sha256') + $files)
        if ($code -eq 0) {
            break
        }
        if ($attempt -eq 4) {
            throw "Time-stamping failed with exit code $code; the files are signed but not time-stamped."
        }
        Start-Sleep -Seconds (5 * $attempt)
    }
}

# Checked with signtool and .NET only: a Windows PowerShell started from PowerShell 7 may fail to load the
# Microsoft.PowerShell.Security module that Get-AuthenticodeSignature belongs to.
foreach ($file in $files) {
    $code = Invoke-SignTool @('verify', '/pa', '/v', $file)
    if ($code -ne 0 -or -not ($script:SignToolOutput -match 'The signature is timestamped')) {
        throw ('The signature of {0} is invalid or not time-stamped.' -f $file)
    }
    $signer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (
        [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($file))
    if ($signer.Thumbprint -ne $certificate.Thumbprint) {
        throw ('{0} is signed by {1}, not by the selected certificate.' -f $file, $signer.Subject)
    }
    Write-Host ('Signed and time-stamped: {0}' -f $file)
}
