param(
    [Parameter(Mandatory = $true)]
    [string[]] $Path,

    [switch] $Recurse,
    [string] $PfxFile,
    [securestring] $PfxPassword,
    [string] $CertThumbprint,
    # Azure Artifact Signing (ex-Trusted Signing): la cle privee reste chez Microsoft, signtool
    # passe par leur DLL. -MetadataFile suffit, le DLL est trouve tout seul s'il est installe.
    [string] $Dlib,
    [string] $MetadataFile,
    # Non passe = choisi selon le mode (Azure ou magasin). Une valeur vide explicite = aucun horodatage.
    [string] $TimestampUrl,
    [string] $Description = 'Squad DNS',
    [string] $DescriptionUrl = 'https://www.xelis.io',
    [switch] $GenerateTestCertificate,
    [switch] $KeepImportedCert,
    [switch] $SkipVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Signs the published binaries or the finished installer with signtool (SHA-256 digest,
# RFC 3161 timestamp). A password-protected PFX is imported into Cert:\CurrentUser\My so the
# password never appears in a child process command line, then removed unless -KeepImportedCert.
#
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.0.0 -PfxFile build\release.pfx
#   powershell -ExecutionPolicy Bypass -File installer\sign.ps1 -Path installer\artifacts\output\SquadDNS-Setup-1.0.0.exe -PfxFile build\release.pfx
#   powershell -ExecutionPolicy Bypass -File installer\sign.ps1 -Path installer\artifacts\publish -Recurse -CertThumbprint 0123ABC...
#   powershell -ExecutionPolicy Bypass -File installer\sign.ps1 -Path installer\artifacts\publish -Recurse `
#       -MetadataFile installer\azure-codesign.json
#
# Distribution requires a certificate issued by a public CA (DigiCert, GlobalSign, SSL.com, Certum)
# or an Azure Artifact Signing certificate profile. -GenerateTestCertificate only exercises this
# pipeline: Windows will still show SmartScreen "unknown publisher" for a self-signed certificate.

$signablePatterns = @('*.exe', '*.dll', '*.sys', '*.msi', '*.appx', '*.msix')

function Find-SignTool {
    $onPath = Get-Command signtool -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\11\bin",
        "${env:ProgramFiles}\Windows Kits\11\bin",
        "${env:ProgramFiles(x86)}\Windows Kits\8.1\bin\x64",
        "${env:ProgramFiles}\Windows Kits\8.1\bin\x64"
    )

    $candidates = New-Object System.Collections.Generic.List[object]
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }

        # Layout A: bin\<sdk-version>\x64\signtool.exe      Layout B: bin\x64\signtool.exe
        $paths = @(Join-Path $root 'signtool.exe')
        $paths += @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' })

        foreach ($file in $paths) {
            if (-not (Test-Path $file)) { continue }

            $folder = Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $file))
            $candidateVersion = [version] $null
            if (-not [version]::TryParse($folder, [ref] $candidateVersion)) {
                $candidateVersion = [version]"0.0"
            }

            $candidates.Add([pscustomobject]@{ Path = (Get-Item $file).FullName; Version = $candidateVersion }) | Out-Null
        }
    }

    if ($candidates.Count -eq 0) {
        throw 'signtool.exe not found. Install the Windows SDK (winget install Microsoft.WindowsSDK) or put signtool on PATH.'
    }

    return ($candidates | Sort-Object Version, Path -Descending | Select-Object -First 1).Path
}

function Resolve-Target {
    param([string] $Item)

    if (Test-Path $Item -PathType Container) {
        $childParams = @{
            Path    = (Join-Path $Item '*')
            Include = $signablePatterns
            File    = $true
        }
        if ($Recurse) { $childParams['Recurse'] = $true }
        Get-ChildItem @childParams | ForEach-Object { $_.FullName }
    }
    elseif (Test-Path $Item -PathType Leaf) {
        (Resolve-Path $Item).Path
    }
    else {
        throw "path not found: $Item"
    }
}

function Find-AzureDlib {
    $roots = @(
        "${env:ProgramFiles(x86)}\Azure\Azure.CodeSigning.Dlib",
        "${env:ProgramFiles}\Azure\Azure.CodeSigning.Dlib"
    )
    foreach ($root in $roots) {
        $candidate = Join-Path $root 'Azure.CodeSigning.Dlib.dll'
        if (Test-Path $candidate -PathType Leaf) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

function Invoke-SignTool {
    param([string[]] $Arguments)

    Write-Host ("  > signtool {0}" -f ($Arguments -join ' ')) -ForegroundColor DarkGray

    # La sortie de signtool passe par le pipeline : sans capture ici, le `return $LASTEXITCODE`
    # renverrait un tableau [sortie..., code] et tout fichier correctement signe serait declare echoue.
    # 2>&1 exige ErrorActionPreference=Continue : avec Stop, la premiere ligne stderr de signtool
    # (un verification /pa qui echoue, par exemple) devient une erreur terminante et tue le script.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $script:signtool @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    foreach ($line in @($output)) { Write-Host ("    {0}" -f $line) }

    return $code
}

$script:signtool = Find-SignTool
Write-Host "signtool : $script:signtool"
Write-Host ("           version {0}" -f (Get-Item $script:signtool).VersionInfo.ProductVersion)

$targets = @($Path | ForEach-Object { Resolve-Target $_ } | Select-Object -Unique)
if ($targets.Count -eq 0) {
    throw 'no signable file matched the given paths'
}

Write-Host ''
Write-Host ("targets  : {0} file(s)" -f $targets.Count)

# --- certificate selection -------------------------------------------------------------------
$thumbprint = $CertThumbprint
$importedPath = $null
$dlibPath = $null
$identity = $null

$usingDlib = -not [string]::IsNullOrWhiteSpace($Dlib) -or -not [string]::IsNullOrWhiteSpace($MetadataFile)

if ($usingDlib) {
    $others = @()
    if ($PfxFile) { $others += '-PfxFile' }
    if ($CertThumbprint) { $others += '-CertThumbprint' }
    if ($GenerateTestCertificate) { $others += '-GenerateTestCertificate' }
    if ($others.Count -gt 0) {
        throw ("-MetadataFile/-Dlib cannot be combined with {0}: pick one key source." -f ($others -join ', '))
    }
    if ([string]::IsNullOrWhiteSpace($MetadataFile)) {
        throw '-MetadataFile is required with -Dlib (see installer\azure-codesign.example.json).'
    }
    if (-not (Test-Path $MetadataFile -PathType Leaf)) {
        throw "metadata file not found: $MetadataFile"
    }
    if (-not [Environment]::Is64BitProcess) {
        throw 'the Azure signing DLL is x64: run this script from 64-bit PowerShell.'
    }

    $dlibPath = $Dlib
    if ([string]::IsNullOrWhiteSpace($dlibPath)) {
        $dlibPath = Find-AzureDlib
        if ($null -eq $dlibPath) {
            throw 'Azure.CodeSigning.Dlib.dll not found. Install it with:  winget install -e --id Microsoft.Azure.ArtifactSigningClientTools'
        }
    }
    elseif (-not (Test-Path $dlibPath -PathType Leaf)) {
        throw "signing DLL not found: $dlibPath"
    }
    $dlibPath = (Resolve-Path $dlibPath).Path

    $Metadata = Get-Content -LiteralPath (Resolve-Path $MetadataFile).Path -Raw | ConvertFrom-Json
    foreach ($field in @('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')) {
        # Tester $null avant de lire .Value : avec StrictMode, une propriete absente donne $null, et
        # $null.Value leve PropertyNotFound au lieu du message amiable attendu.
        $property = $Metadata.PSObject.Properties[$field]
        $value = if ($null -eq $property) { '' } else { [string] $property.Value }
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "metadata file is missing the '$field' field: $MetadataFile"
        }
    }

    Write-Host ''
    Write-Host 'AZURE ARTIFACT SIGNING - the private key never leaves Microsoft.' -ForegroundColor DarkCyan
    Write-Host ("  dlib     : {0}" -f $dlibPath)
    Write-Host ("  account  : {0}" -f $Metadata.CodeSigningAccountName)
    Write-Host ("  profile  : {0}" -f $Metadata.CertificateProfileName)
    Write-Host ("  endpoint : {0}" -f $Metadata.Endpoint)

    # Le chargeur de la DLL exige un signtool recent (SDK 10.0.2261.755+) : avec un signtool plus ancien
    # l'echec ressort en 'SignTool Error: A signature was required but not found', sans rapport avec la cause.
    $minSigntool = [version]'10.0.2261.755'
    $haveSigntool = [version]$null
    if ([version]::TryParse((Get-Item $script:signtool).VersionInfo.ProductVersion, [ref] $haveSigntool)) {
        if ($haveSigntool -lt $minSigntool) {
            Write-Host ("  WARNING  : signtool {0} is older than {1}, the Azure DLL may refuse it" -f $haveSigntool, $minSigntool) -ForegroundColor Yellow
        }
    }
}
elseif ($GenerateTestCertificate) {
    Write-Host ''
    Write-Host 'TEST CERTIFICATE MODE - the signature will NOT be trusted by Windows.' -ForegroundColor Magenta
    Write-Host '  SmartScreen will still warn end users. Use a CA-issued certificate to ship.' -ForegroundColor Magenta

    # Le mode test doit rester rejouable sans encombrer le magasin : un certificat de test
    # encore valide est recharge au lieu d'etre regenere a chaque build.
    $testSubject = 'CN=Squad DNS (test certificate)'
    $existing = Get-ChildItem 'Cert:\CurrentUser\My' -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $testSubject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($existing) {
        $thumbprint = $existing.Thumbprint
        Write-Host ("reusing    : {0} (expires {1:yyyy-MM-dd})" -f $thumbprint, $existing.NotAfter)
    }
    else {
        $testCert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $testSubject `
            -HashAlgorithm SHA256 `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(1)

        $thumbprint = $testCert.Thumbprint
        Write-Host ("created    : {0} (expires {1:yyyy-MM-dd})" -f $thumbprint, $testCert.NotAfter)
    }
}
elseif ([string]::IsNullOrWhiteSpace($thumbprint) -and -not [string]::IsNullOrWhiteSpace($PfxFile)) {
    if (-not (Test-Path $PfxFile -PathType Leaf)) {
        throw "PFX file not found: $PfxFile"
    }

    $secure = $PfxPassword
    if ($null -eq $secure) {
        $secure = Read-Host "Password for $PfxFile" -AsSecureString
    }

    # Memoriser les empreintes deja presentes : un certificat CA installe une seule fois dans le
    # magasin ressort de l'import avec la MEME empreinte, et le nettoyage du bas le detruirait.
    $alreadyInStore = @{}
    foreach ($existing in @(Get-ChildItem 'Cert:\CurrentUser\My')) {
        $alreadyInStore[$existing.Thumbprint] = $true
    }

    $imported = Import-PfxCertificate `
        -FilePath (Resolve-Path $PfxFile).Path `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -Password $secure

    $thumbprint = $imported.Thumbprint
    if ($alreadyInStore.ContainsKey($thumbprint)) {
        Write-Host ("reused   : {0} was already in Cert:\CurrentUser\My and stays there" -f $thumbprint)
    }
    else {
        $importedPath = $thumbprint
        Write-Host ("imported : {0} -> Cert:\CurrentUser\My (removed again after signing)" -f $thumbprint)
    }
}

if (-not $usingDlib -and [string]::IsNullOrWhiteSpace($thumbprint)) {
    Write-Host ''
    Write-Host 'No certificate supplied. Choose one:' -ForegroundColor Yellow
    Write-Host '  -PfxFile build\release.pfx [-PfxPassword (securestring)]'
    Write-Host '  -CertThumbprint <thumbprint of a CA-issued code signing certificate>'
    Write-Host '  -MetadataFile installer\azure-codesign.json   (Azure Artifact Signing)'
    Write-Host '  -GenerateTestCertificate      (pipeline check only, untrusted)'
    Write-Host ''
    Write-Host 'Already-signed files can be inspected with:'
    Write-Host "  & ""$script:signtool"" verify /pa /v ""$($targets[0])"""
    throw 'nothing to sign: supply -PfxFile, -CertThumbprint, -MetadataFile or -GenerateTestCertificate'
}

$identity = if ($usingDlib) { ("Azure profile '{0}'" -f $Metadata.CertificateProfileName) } else { $thumbprint }

# --- signing ---------------------------------------------------------------------------------
# HTTPS exclu par la mesure du 2026-10-06 : https://timestamp.digicert.com expirait sur ce reseau,
# ce que signtool rend par le message trompeur 'Invalid Timestamp URL'.
# Ne pas remplir quand le parametre n'est pas passe : -TimestampUrl '' doit continuer a signifier
# "pas d'horodatage", sinon cette resolution rendrait l'option impossible.
if (-not $PSBoundParameters.ContainsKey('TimestampUrl')) {
    $TimestampUrl = if ($usingDlib) { 'http://timestamp.acs.microsoft.com' } else { 'http://timestamp.digicert.com' }
}

if ($usingDlib) {
    $signArgs = @('sign', '/fd', 'sha256', '/dlib', $dlibPath, '/dmdf', (Resolve-Path $MetadataFile).Path,
        '/d', $Description, '/du', $DescriptionUrl)
}
else {
    $signArgs = @('sign', '/fd', 'sha256', '/sha1', $thumbprint, '/d', $Description, '/du', $DescriptionUrl)
}

if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    # /as horodate en contre-signature, ce qui laisse la signature primaire du magasin intacte.
    # Le DLL Azure cree lui-meme une signature primaire deja horodatee : /as lui est refuse.
    $signArgs += @('/tr', $TimestampUrl, '/td', 'sha256')
    if (-not $usingDlib) { $signArgs += '/as' }
} else {
    Write-Host ''
    Write-Host 'WARNING: no timestamp requested - the signature dies with the certificate.' -ForegroundColor Yellow
}

$failed = @()
foreach ($file in $targets) {
    Write-Host ''
    Write-Host ("signing  : {0}" -f $file)
    $code = Invoke-SignTool ($signArgs + @($file))
    if ($code -ne 0) {
        Write-Host ("  failed with exit code {0}" -f $code) -ForegroundColor Red
        $failed += $file
    }
}

if (-not $KeepImportedCert -and $importedPath) {
    Get-ChildItem "Cert:\CurrentUser\My\$importedPath" -ErrorAction SilentlyContinue | Remove-Item -Force
}

# --- verification ----------------------------------------------------------------------------
if (-not $SkipVerify) {
    Write-Host ''
    Write-Host '=== Verification (signtool verify /pa /v) ===' -ForegroundColor Cyan
    foreach ($file in $targets) {
        if ($failed -contains $file) { continue }
        $code = Invoke-SignTool @('verify', '/pa', '/v', $file)
        if ($code -ne 0) {
            Write-Host ("  NOT TRUSTED: {0}" -f (Split-Path -Leaf $file)) -ForegroundColor Yellow
            Write-Host '  Expected in test-certificate mode. A CA-issued certificate must chain to a'
            Write-Host '  trusted root and, for a clean SmartScreen reputation, be timestamped.'
        }
        else {
            Write-Host ("  TRUSTED: {0}" -f (Split-Path -Leaf $file)) -ForegroundColor Green
        }
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ("{0} file(s) could not be signed." -f $failed.Count) -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host ("Signed {0} file(s) with {1}." -f $targets.Count, $identity) -ForegroundColor Green
if ($GenerateTestCertificate) {
    Write-Host 'This was a self-signed test certificate: delete it with'
    Write-Host "  Get-ChildItem 'Cert:\CurrentUser\My\$thumbprint' | Remove-Item"
    Write-Host "  (or keep it: it is in CurrentUser\My, not in a machine store)"
}
Write-Host ''
Write-Host 'Standalone signing only patches what exists: installer built from unsigned binaries stays'
Write-Host 'unsigned inside. Prefer "build.ps1 ... -PfxFile|-CertThumbprint|-MetadataFile|-GenerateTestCertificate", which'
Write-Host 'signs the binaries before ISCC and the setup after it.'
exit 0
