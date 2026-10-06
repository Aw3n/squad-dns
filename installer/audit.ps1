param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Static wiring audit for the WPF layer. Catches the defect classes that compile and unit-test
# cleanly but crash the interface at first paint: a TwoWay-by-default dependency property bound
# to a read-only view model member, an unresolved StaticResource/DynamicResource key, a command
# declared but never reachable from the UI (or bound to a member that does not exist), a missing
# localization key in one of the two languages, and catalog drift away from the spec file.
#
#   powershell -ExecutionPolicy Bypass -File installer\audit.ps1

$app = Join-Path $Root 'src\SquadDns'
$core = Join-Path $Root 'src\SquadDns.Core'
$fail = 0
$warn = 0

function Section([string]$name) {
    Write-Host ''
    Write-Host ("--- {0}" -f $name) -ForegroundColor Cyan
}

function Report([string]$level, [string]$message) {
    if ($level -eq 'FAIL') {
        $script:fail++
        Write-Host ("  FAIL  {0}" -f $message) -ForegroundColor Red
    }
    elseif ($level -eq 'WARN') {
        $script:warn++
        Write-Host ("  warn  {0}" -f $message) -ForegroundColor Yellow
    }
    else {
        Write-Host ("  ok    {0}" -f $message) -ForegroundColor Green
    }
}

function Read-Text([string]$path) {
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8
}

$xamlFiles = Get-ChildItem -LiteralPath $Root -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object { $_.FullName }

$vmFiles = Get-ChildItem -LiteralPath (Join-Path $app 'ViewModels') -Filter *.cs |
    ForEach-Object { $_.FullName }

Section 'Resource keys'

$defined = @{}
foreach ($file in $xamlFiles) {
    foreach ($m in [regex]::Matches((Read-Text $file), 'x:Key="([^"]+)"')) {
        $defined[$m.Groups[1].Value] = (Split-Path -Leaf $file)
    }
}

$usedKeys = @{}
foreach ($file in $xamlFiles) {
    foreach ($m in [regex]::Matches((Read-Text $file), '\{\s*(?:Static|Dynamic)Resource\s+([^}]+?)\s*\}')) {
        $key = $m.Groups[1].Value
        if (-not $usedKeys.ContainsKey($key)) {
            $usedKeys[$key] = (Split-Path -Leaf $file)
        }
    }
}

$unresolved = @()
foreach ($key in $usedKeys.Keys) {
    if (-not $defined.ContainsKey($key)) {
        $unresolved += ("{0} (used in {1})" -f $key, $usedKeys[$key])
    }
}

if ($unresolved.Count -eq 0) {
    Report 'OK' ("{0} distinct keys defined, {1} referenced, none unresolved" -f $defined.Keys.Count, $usedKeys.Keys.Count)
}
else {
    foreach ($item in $unresolved) { Report 'FAIL' ("unresolved resource key {0}" -f $item) }
}

$duplicates = @()
foreach ($file in $xamlFiles) {
    $keys = [regex]::Matches((Read-Text $file), 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    foreach ($group in ($keys | Group-Object | Where-Object { $_.Count -gt 1 })) {
        $duplicates += ("{0}: {1} declared {2} times" -f (Split-Path -Leaf $file), $group.Name, $group.Count)
    }
}

if ($duplicates.Count -eq 0) {
    Report 'OK' 'no duplicate x:Key inside a single dictionary'
}
else {
    foreach ($d in $duplicates) { Report 'FAIL' ("duplicate resource key crashes dictionary load: {0}" -f $d) }
}

Section 'Binding paths'

# A {Binding Typo} fails silently: the control simply stays empty, which the user reads as a broken app.
$members = @{}
foreach ($file in $vmFiles) {
    $text = Read-Text $file
    foreach ($pattern in @('public\s+(?:static\s+)?[\w\.<>\[\]\?]+\s+([A-Za-z_]\w*)\s*\{',
            'public\s+(?:static\s+)?[\w\.<>\[\]\?]+\s+([A-Za-z_]\w*)\s*=>')) {
        foreach ($m in [regex]::Matches($text, $pattern)) {
            $members[$m.Groups[1].Value] = $true
        }
    }
    foreach ($record in [regex]::Matches($text, 'record\s+\w+[^({]*\(([^)]*)\)')) {
        foreach ($parameter in ($record.Groups[1].Value -split ',')) {
            if ($parameter -match '([A-Za-z_]\w*)\s*(?:=|$)') {
                $members[$Matches[1]] = $true
            }
        }
    }
}

foreach ($name in @('Item', 'Items', 'DataContext', 'Content', 'Command', 'CommandParameter', 'Tag',
    'Path', 'RelativeSource', 'ElementName', 'Source', 'Name', 'Id', 'Length', 'Count')) {
    $members[$name] = $true
}

$orphans = @()
foreach ($file in $xamlFiles) {
    foreach ($m in [regex]::Matches((Read-Text $file), '\{Binding\s+([A-Za-z_]\w*)')) {
        if (-not $members.ContainsKey($m.Groups[1].Value)) {
            $orphans += ("{0} (in {1})" -f $m.Groups[1].Value, (Split-Path -Leaf $file))
        }
    }
}

if ($orphans.Count -eq 0) {
    Report 'OK' ("{0} bindable members known, every binding root names one of them" -f $members.Keys.Count)
}
else {
    foreach ($o in ($orphans | Sort-Object -Unique)) { Report 'FAIL' ("binding path has no declared member: {0}" -f $o) }
}

Section 'Localization'

$frPath = Join-Path $app 'Localization\Strings.fr.xaml'
$enPath = Join-Path $app 'Localization\Strings.en.xaml'

function Get-StringKeys([string]$path) {
    $set = @{}
    foreach ($m in [regex]::Matches((Read-Text $path), 'x:Key="([^"]+)"')) {
        $set[$m.Groups[1].Value] = $true
    }
    return $set
}

$fr = Get-StringKeys $frPath
$en = Get-StringKeys $enPath

$onlyFr = @($fr.Keys | Where-Object { -not $en.ContainsKey($_) })
$onlyEn = @($en.Keys | Where-Object { -not $fr.ContainsKey($_) })

if ($onlyFr.Count -eq 0 -and $onlyEn.Count -eq 0) {
    Report 'OK' ("fr {0} keys = en {1} keys, symmetric" -f $fr.Keys.Count, $en.Keys.Count)
}
else {
    foreach ($k in $onlyFr) { Report 'FAIL' ("key present in fr only: {0}" -f $k) }
    foreach ($k in $onlyEn) { Report 'FAIL' ("key present in en only: {0}" -f $k) }
}

$callsT = @()
foreach ($file in (Get-ChildItem -LiteralPath $app -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object { $_.FullName })) {
    foreach ($m in [regex]::Matches((Read-Text $file), '(?:LocalizationManager|\bT)\s*\(\s*"([^"]+)"\s*\)')) {
        $callsT += $m.Groups[1].Value
    }
}

$badT = @($callsT | Sort-Object -Unique | Where-Object { (-not $fr.ContainsKey($_)) -or (-not $en.ContainsKey($_)) })
if ($badT.Count -eq 0) {
    Report 'OK' ("{0} distinct T() literals all exist in both languages" -f ($callsT | Sort-Object -Unique).Count)
}
else {
    foreach ($k in $badT) { Report 'FAIL' ("T() literal has no translation entry: {0}" -f $k) }
}

Section 'Merged dictionaries'

$appText = Read-Text (Join-Path $app 'App.xaml')
$dictRefs = @([regex]::Matches($appText, 'Source="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
foreach ($ref in $dictRefs) {
    $resolved = Join-Path $app ($ref -replace '/.*$', '')
    $candidate = Join-Path $app ($ref -replace '/', '\')
    if (Test-Path -LiteralPath $candidate) {
        Report 'OK' ("packaged dictionary exists: {0}" -f $ref)
    }
    else {
        Report 'FAIL' ("App.xaml merges a missing file: {0}" -f $ref)
    }
}

Section 'TwoWay-by-default bindings on read-only members'

# WPF binds these dependency properties TwoWay unless told otherwise, and throws
# InvalidOperationException at first layout when the source member has no public setter.
$dpRules = @(
    @{ Name = 'Value'; Tags = 'ProgressBar|Slider|ScrollBar' },
    @{ Name = 'IsChecked'; Tags = 'ToggleButton|CheckBox|RadioButton' },
    @{ Name = 'Text'; Tags = 'TextBox' },
    @{ Name = 'IsSelected'; Tags = 'ListBoxItem|TreeViewItem' },
    @{ Name = 'IsOpen'; Tags = 'Popup' },
    @{ Name = 'IsExpanded'; Tags = 'Expander' }
)

$memberBlocks = @{}
foreach ($file in $vmFiles) {
    $text = Read-Text $file
    foreach ($m in [regex]::Matches($text, '(?m)^\s*public\s+[^\s=]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*(=>|\{)')) {
        $name = $m.Groups[1].Value
        $start = $m.Index
        $rest = $text.Substring($start)
        $next = $rest.Substring(1).IndexOf("`n    public ")
        if ($next -lt 0) { $next = [Math]::Min(400, $rest.Length - 1) }
        $block = $rest.Substring(0, [Math]::Min($next + 1, $rest.Length))
        $settable = $false
        if ($m.Groups[2].Value -ne '=>') {
            if ($block -match '\bset\b' -and $block -notmatch 'private\s+set' -and $block -notmatch 'init\s*[;}]') {
                $settable = $true
            }
        }
        if (-not $memberBlocks.ContainsKey($name)) {
            $memberBlocks[$name] = $settable
        }
        elseif ($settable) {
            $memberBlocks[$name] = $true
        }
    }
}

$checked = 0
$findings = 0
foreach ($file in $xamlFiles) {
    $text = Read-Text $file
    foreach ($rule in $dpRules) {
        $pattern = '(<{0}[^>]*?\b{1}="\{{Binding\s+([^,}}]+)([^}}]*)\}}"[^>]*>)' -f $rule.Tags, $rule.Name, '([\w\.\[\]]+)'
        foreach ($m in [regex]::Matches($text, $pattern, 'Singleline')) {
            $checked++
            $path = $m.Groups[2].Value
            if ($path.StartsWith('DataContext.')) {
                $path = $path.Substring(12)
            }
            $member = ($path -split '\.')[0]
            $tail = $m.Groups[3].Value
            if ($tail -match 'Mode=OneWay|Mode=OneTime') { continue }
            if (-not $memberBlocks.ContainsKey($member)) { continue }
            if ($memberBlocks[$member]) { continue }
            $findings++
            Report 'FAIL' ("{0}: binding {1}=""{2}"" targets read-only member '{3}' (throws at first layout)" -f (Split-Path -Leaf $file), $rule.Name, $member, $member)
        }
    }
}

if ($findings -eq 0) {
    Report 'OK' ("{0} TwoWay-by-default bindings examined, every reachable member has a public setter or an explicit OneWay mode" -f $checked)
}

Section 'Command wiring'

$declared = @{}
foreach ($file in $vmFiles) {
    foreach ($m in [regex]::Matches((Read-Text $file), 'public\s+(?:RelayCommand|AsyncRelayCommand|ICommand)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get')) {
        $declared[$m.Groups[1].Value] = $true
    }
}

$bound = @{}
foreach ($file in $xamlFiles) {
    foreach ($m in [regex]::Matches((Read-Text $file), '\{Binding\s+(?:DataContext\.)?([A-Za-z_][A-Za-z0-9_]*Command)\b')) {
        $bound[$m.Groups[1].Value] = (Split-Path -Leaf $file)
    }
}

$orphan = @($bound.Keys | Where-Object { -not $declared.ContainsKey($_) })
$unused = @($declared.Keys | Where-Object { -not $bound.ContainsKey($_) })

if ($orphan.Count -eq 0) {
    Report 'OK' ("{0} commands bound in XAML all exist on the view model" -f $bound.Keys.Count)
}
else {
    foreach ($c in $orphan) { Report 'FAIL' ("XAML binds a command that does not exist: {0} ({1})" -f $c, $bound[$c]) }
}

if ($unused.Count -eq 0) {
    Report 'OK' ("{0} view model commands all reachable from the UI" -f $declared.Keys.Count)
}
else {
    foreach ($c in $unused) { Report 'WARN' ("command declared but never bound in XAML: {0}" -f $c) }
}

Section 'Catalog versus specification'

# Le brief impose des points d'entree et des descriptions litterales. S'en ecarter est une
# deviation : elle n'est admise que si l'humain l'a autorisee explicitement, et l'audit doit la
# repeter a chaque build au lieu de laisser passer le changement en silence. Une entree de cette
# liste ne transforme le FAIL en WARN que si le remplacement est effectivement present au
# catalogue ; sinon le FAIL reste, et un service non autorise retire du catalogue tombe aussi.
$substitutions = @(
    @{ Needle = 'dns.mullvad.net'; Replacement = 'https://dnsforge.de/dns-query'; Reason = 'approved 2026-10-06: Mullvad shuts its public encrypted DNS on 2026-11-02, replaced by dnsForge' },
    @{ Needle = '194.242.2.2'; Replacement = '49.12.67.122'; Reason = 'same approved substitution, DoT side' },
    @{ Needle = 'QNAME'; Replacement = 'adminForge'; Reason = 'same approved substitution, provider description side' }
)

function Get-Head([string]$text) {
    if ($text.Length -gt 60) { return $text.Substring(0, 60) }
    return $text
}

function Find-Substitution([string]$specValue) {
    foreach ($entry in $substitutions) {
        if ($specValue.Contains($entry.Needle)) { return $entry }
    }
    return $null
}

function Resolve-Missing([string]$kind, [string]$specValue, [string]$catalogText) {
    $entry = Find-Substitution $specValue
    if ($null -ne $entry -and $catalogText.Contains($entry.Replacement)) {
        Report 'WARN' ("{0} '{1}' is a documented deviation: {2}" -f $kind, $specValue, $entry.Reason)
        return $true
    }
    Report 'FAIL' ("{0} missing from catalog: {1}" -f $kind, $specValue)
    return $false
}

$specPath = Join-Path $Root 'Squad DNS.txt'
if (Test-Path -LiteralPath $specPath) {
    $spec = Read-Text $specPath
    $catalog = Read-Text (Join-Path $core 'Catalog\ProviderCatalog.cs')

    $specDoh = @([regex]::Matches($spec, '-\s*DoH:\s*(\S+)') | ForEach-Object { $_.Groups[1].Value })
    $specDot = @([regex]::Matches($spec, '-\s*DoT:\s*(\d+\.\d+\.\d+\.\d+):(\d+)') | ForEach-Object { $_.Groups[1].Value })
    $specDesc = @([regex]::Matches($spec, 'Description:\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })

    Report 'OK' ("spec lists {0} DoH endpoints, {1} DoT endpoints, {2} descriptions" -f $specDoh.Count, $specDot.Count, $specDesc.Count)

    foreach ($url in $specDoh) {
        if (-not $catalog.Contains($url)) { [void] (Resolve-Missing 'spec DoH endpoint' $url $catalog) }
    }
    foreach ($ip in $specDot) {
        if (-not $catalog.Contains($ip)) { [void] (Resolve-Missing 'spec DoT server' $ip $catalog) }
    }

    $portLines = @([regex]::Matches($catalog, 'DoTPort\s*=\s*(\d+)') | ForEach-Object { $_.Groups[1].Value })
    $defaultPort = Read-Text (Join-Path $core 'Models\Domain.cs')
    if ($defaultPort -notmatch 'DoTPort\s*\{\s*get;\s*init;\s*}\s*=\s*853' -and $portLines -notcontains '853') {
        Report 'WARN' 'cannot confirm DoT port 853 for every service'
    }
    else {
        Report 'OK' 'DoT port 853 confirmed (model default or explicit)'
    }

    $missingDesc = 0
    $deviatedDesc = 0
    foreach ($d in $specDesc) {
        if ($catalog.Contains($d)) { continue }
        $entry = Find-Substitution $d
        if ($null -ne $entry -and $catalog.Contains($entry.Replacement)) {
            $deviatedDesc++
            $head = $entry.Reason
            Report 'WARN' ("spec description '{0}...' replaced by another provider: {1}" -f (Get-Head $d), $head)
            continue
        }
        $missingDesc++
        Report 'FAIL' ("spec description not verbatim in catalog: {0}..." -f (Get-Head $d))
    }
    if ($missingDesc -eq 0) {
        Report 'OK' ('{0} of {1} French descriptions are verbatim from the specification, {2} substituted under authorization' -f ($specDesc.Count - $deviatedDesc), $specDesc.Count, $deviatedDesc)
    }
}
else {
    Report 'WARN' ("specification file not found: {0}" -f $specPath)
}

Section 'Donation block'

$appx = Read-Text (Join-Path $app 'Views\AboutView.xaml')
$srcRoot = Join-Path $Root 'src'
$allSource = @{}
foreach ($file in (Get-ChildItem -LiteralPath $srcRoot -Recurse |
        Where-Object { (-not $_.PSIsContainer) -and ($_.Extension -eq '.cs' -or $_.Extension -eq '.xaml') -and ($_.FullName -notmatch '\\(obj|bin)\\|artifacts') } |
        ForEach-Object { $_.FullName })) {
    $allSource[$file] = Read-Text $file
}

foreach ($needle in @('xel:6mmj85x7504h3z9qwendxhahc4804xgrek59rec3zhhexcdywe8qqvnypnh', 'https://trocador.app/?ref=BLbjXxTsoK', 'https://www.xelis.io')) {
    $hit = $null
    foreach ($file in $allSource.Keys) {
        if ($allSource[$file].Contains($needle)) { $hit = (Split-Path -Leaf $file); break }
    }
    if ($null -eq $hit) {
        Report 'FAIL' ("required literal missing from the source tree: {0}" -f $needle)
    }
    else {
        Report 'OK' ("literal defined in {0}: {1}" -f $hit, $needle)
    }
}

foreach ($command in @('CopyDonationCommand', 'OpenSwapCommand', 'OpenXelisCommand')) {
    if ($appx.Contains($command)) {
        Report 'OK' ("About page wires {0}" -f $command)
    }
    else {
        Report 'FAIL' ("About page does not reference {0}" -f $command)
    }
}

Write-Host ''
if ($fail -eq 0) {
    Write-Host ('Wiring audit clean ({0} warning(s)).' -f $warn) -ForegroundColor Green
}
else {
    Write-Host ("Wiring audit found {0} defect(s)." -f $fail) -ForegroundColor Red
    exit 1
}

exit 0
