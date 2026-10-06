param(
    [switch] $SelfContained,
    [switch] $SkipTests,
    [switch] $SkipInstaller,
    [switch] $Probe,
    [string] $Version = '1.0.0',
    [string] $PfxFile,
    [securestring] $PfxPassword,
    [string] $CertThumbprint,
    [switch] $GenerateTestCertificate,
    [switch] $SkipSigning,
    [string] $Dlib,
    [string] $MetadataFile,
    # Non passe = laisse sign.ps1 choisir selon le mode. Une valeur vide explicite = aucun horodatage.
    [string] $TimestampUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:timestampProvided = $PSBoundParameters.ContainsKey('TimestampUrl')

$root = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $root 'installer'
$project = Join-Path $root 'src\SquadDns\SquadDns.csproj'
$publishDir = Join-Path $installer 'artifacts\publish'
$outputDir = Join-Path $installer 'artifacts\output'

function Step([string]$message) {
    Write-Host ''
    Write-Host ("=== {0}" -f $message) -ForegroundColor Cyan
}

$signScript = Join-Path $installer 'sign.ps1'

function Invoke-Signing {
    param([string] $Target, [switch] $Recurse)

    $a = @{ Path = $Target }
    # Ne forwards -TimestampUrl que si l'utilisateur en a donne un : sinon sign.ps1 choisit son
    # default par mode, et une chaine vide signifierait "ne pas horodater".
    # PSBoundParameters dans la function ne connait que ses propres parametres : le drapeau vient
    # du script. Sans lui, ne pas forwarder -TimestampUrl laisserait sign.ps1 horoder par defaut.
    if ($script:timestampProvided) { $a['TimestampUrl'] = $TimestampUrl }
    if ($Recurse) { $a['Recurse'] = $true }
    if ($PfxFile) {
        $a['PfxFile'] = $PfxFile
        if ($PfxPassword) { $a['PfxPassword'] = $PfxPassword }
    }
    if ($CertThumbprint) { $a['CertThumbprint'] = $CertThumbprint }
    if ($MetadataFile) { $a['MetadataFile'] = $MetadataFile }
    if ($Dlib) { $a['Dlib'] = $Dlib }
    if ($GenerateTestCertificate) { $a['GenerateTestCertificate'] = $true }

    & $signScript @a
    if ($LASTEXITCODE -ne 0) {
        throw ("signing failed for {0} (exit code {1})" -f $Target, $LASTEXITCODE)
    }
}

$signingRequested = (-not $SkipSigning) -and ($PfxFile -or $CertThumbprint -or $MetadataFile -or $Dlib -or $GenerateTestCertificate)

Step 'Build environment'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw 'dotnet SDK not found on PATH. Install the .NET 8 SDK (Desktop Runtime for WPF).'
}
Write-Host ("dotnet : {0}" -f $dotnet.Source)
& dotnet --version

Step 'Wiring audit'
& (Join-Path $installer 'audit.ps1') -Root $root
if ($LASTEXITCODE -ne 0) {
    throw 'static wiring audit reported defects (see above)'
}

if (-not $SkipTests) {
    Step 'Unit tests'
    & dotnet test (Join-Path $root 'SquadDns.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }
}

Step 'Publish'
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir,
    ('-p:Version=' + $Version),
    '--nologo', '-v', 'm'
)

if ($SelfContained) {
    $publishArgs += '--self-contained'
    $publishArgs += '-p:PublishTrimmed=false'
} else {
    $publishArgs += '--self-contained'
    $publishArgs += 'false'
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $publishDir 'SquadDns.exe'
if (-not (Test-Path $exe)) {
    throw "publish did not produce $exe"
}

$sizeMb = [math]::Round(((Get-ChildItem $publishDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ("published : {0} ({1} MB)" -f $exe, $sizeMb)

Step 'Smoke test (headless self-test)'
$selfTestArgs = @('--selftest')
if ($Probe) {
    $selfTestArgs += '--probe'
    Write-Host 'probing live DoH/DoT latency for all 7 services (slower)'
}

$selfTest = Start-Process -FilePath $exe -ArgumentList $selfTestArgs -PassThru -WindowStyle Hidden
if (-not $selfTest.WaitForExit(180000)) {
    Stop-Process -Id $selfTest.Id -Force
    throw 'the published build did not complete its self-test in 180 s'
}

$report = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'SquadDns\logs\selftest-*.json') |
    Sort-Object LastWriteTime | Select-Object -Last 1

if ($null -eq $report) {
    throw 'self-test produced no report'
}

$json = Get-Content $report.FullName -Raw | ConvertFrom-Json
Write-Host ("os        : {0}" -f $json.OsLine)
Write-Host ("elevated  : {0}" -f $json.Elevated)
Write-Host ("adapters  : {0}" -f $json.Interfaces.Count)
Write-Host ("providers : {0}" -f $json.Catalog.Count)
Write-Host ("plan      : {0} commands" -f $json.PlannedCommands.Count)
Write-Host ("latency   : {0} rows" -f $json.Latency.Count)

if ($json.Latency.Count -gt 0) {
    $json.Latency | ForEach-Object { Write-Host ("            {0}" -f $_) }
}

if ($json.Catalog.Count -ne 7) {
    throw 'the published catalog does not contain the 7 services of the specification'
}

Write-Host ("report    : {0}" -f $report.FullName)

if ($SkipInstaller) {
    Write-Host ''
    Write-Host 'Installer step skipped.' -ForegroundColor Yellow
    return
}

# La signature des binaires precede ISCC : l'installeur embarque alors des fichiers deja signes,
# et le signer lui-meme ensuite suffit. L'inverse produirait un installeur aux binaires non signes.
$signBinaries = $false
if ($SkipSigning) {
    Step 'Code signing'
    Write-Host 'skipped by request (-SkipSigning): the shipped files stay unsigned.' -ForegroundColor Yellow
}
elseif (-not $signingRequested) {
    Step 'Code signing'
    Write-Host 'no certificate supplied: binaries and installer ship unsigned (SmartScreen will warn).' -ForegroundColor Yellow
    Write-Host '  -PfxFile build\release.pfx | -CertThumbprint <thumbprint> | -MetadataFile installer\azure-codesign.json | -GenerateTestCertificate'
}
else {
    Step 'Code signing (published binaries)'
    Invoke-Signing -Target $publishDir -Recurse
    $signBinaries = $true
}

Step 'Inno Setup'
$isccCandidates = @(
    (Get-Command iscc -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($null -eq $iscc) {
    Write-Host 'Inno Setup 6 not found: the portable publish folder is still usable.' -ForegroundColor Yellow
    Write-Host "  $publishDir"
    Write-Host 'Install it with:  winget install JRSoftware.InnoSetup'
    return
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" (Join-Path $installer 'setup.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setup = Get-ChildItem (Join-Path $outputDir 'SquadDNS-Setup-*.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
if ($null -eq $setup) {
    throw "ISCC reported success but no installer was produced in $outputDir"
}

Write-Host ''
Write-Host ("setup     : {0}" -f $setup.FullName)
Write-Host ("size      : {0} MB" -f [math]::Round($setup.Length / 1MB, 1))

if ($signBinaries) {
    Step 'Code signing (installer)'
    Invoke-Signing -Target $setup.FullName

    Step 'Signature verification'
    $verifyTargets = @((Join-Path $publishDir 'SquadDns.exe'), $setup.FullName)
    foreach ($file in $verifyTargets) {
        $auth = Get-AuthenticodeSignature $file
        $status = $auth.Status
        $signer = if ($auth.SignerCertificate) { $auth.SignerCertificate.Subject } else { '(none)' }

        # PowerShell 5.1 n'expose pas TimeStamp : TimeStamperCertificate est la seule preuve
        # portable d'un horodatage RFC 3161.
        $stamper = if ($auth.TimeStamperCertificate) { $auth.TimeStamperCertificate.Subject } else { '(none)' }
        Write-Host ("{0,-14} : {1}" -f $status, (Split-Path -Leaf $file))
        Write-Host ("               : signer {0} (expires {1:yyyy-MM-dd}) | timestamp {2}" -f $signer, $auth.SignerCertificate.NotAfter, $stamper)

        if ($status -ne 'Valid') {
            $msg = $auth.StatusMessage
            if ($msg -and $msg.Length -gt 120) { $msg = $msg.Substring(0, 120) + '...' }
            Write-Host ("               : {0}" -f $msg) -ForegroundColor DarkGray
        }
    }
    Write-Host ''
    Write-Host 'sign.ps1 ran "signtool verify /pa" on each file above. A self-signed test certificate'
    Write-Host 'cannot build a trusted chain, so expect "error 57: A certificate chain could not be built'
    Write-Host 'to a trusted root". A CA-issued certificate must print "Successfully verified".'
}

Write-Host ''
if (-not $signBinaries) {
    Write-Host 'Nothing was signed. To ship with a CA-issued certificate:' -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version $Version -PfxFile build\release.pfx"
    Write-Host '  (or -CertThumbprint <thumbprint> once the certificate is imported, -GenerateTestCertificate to rehearse)'
}
