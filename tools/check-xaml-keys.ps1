Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\SquadDns'
$xaml = Get-ChildItem -LiteralPath $project -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$defined = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
$definedIn = @{}

foreach ($file in $xaml) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), 'x:Key\s*=\s*"([^"]+)"')) {
        [void]$defined.Add($match.Groups[1].Value)
        if (-not $definedIn.ContainsKey($match.Groups[1].Value)) {
            $definedIn[$match.Groups[1].Value] = $file.Name
        }
    }
}

# Styles/target types and framework keys that are never declared in our dictionaries.
$implicit = @(
    'Background', 'Foreground', 'BorderBrush', 'BorderThickness', 'Padding', 'Margin',
    'FontFamily', 'FontSize', 'FontWeight', 'Cursor', 'Template', 'ItemsPanel',
    'HorizontalScrollBarVisibility', 'VerticalScrollBarVisibility', 'MinWidth', 'MinHeight'
)

$missing = @()
$used = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)

foreach ($file in $xaml) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\{(?:Static|Dynamic)Resource\s+([^}]+?)\s*\}')) {
        $key = $match.Groups[1].Value.Trim()
        if ($key.StartsWith('System.') -or $implicit -contains $key) {
            continue
        }
        [void]$used.Add($key)
        if (-not $defined.Contains($key)) {
            $missing += [pscustomobject]@{ File = $file.Name; Key = $key }
        }
    }
}

$fr = Get-Content -LiteralPath (Join-Path $project 'Localization\Strings.fr.xaml') -Raw
$en = Get-Content -LiteralPath (Join-Path $project 'Localization\Strings.en.xaml') -Raw

$frKeys = @([regex]::Matches($fr, 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$enKeys = @([regex]::Matches($en, 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value })

Write-Host ("defined keys : {0}" -f $defined.Count)
Write-Host ("used keys    : {0}" -f $used.Count)
Write-Host ("fr keys      : {0}" -f $frKeys.Count)
Write-Host ("en keys      : {0}" -f $enKeys.Count)
$onlyFr = @(Compare-Object -ReferenceObject $frKeys -DifferenceObject $enKeys | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject })
$onlyEn = @(Compare-Object -ReferenceObject $frKeys -DifferenceObject $enKeys | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'MISSING RESOURCE KEYS:'
    $missing | Sort-Object Key -Unique | ForEach-Object { Write-Host ("  {0}  (used in {1})" -f $_.Key, $_.File) }
}

if ($onlyFr.Count -gt 0) {
    Write-Host ''
    Write-Host 'KEYS ONLY IN FRENCH:'
    $onlyFr | ForEach-Object { Write-Host ("  " + $_) }
}

if ($onlyEn.Count -gt 0) {
    Write-Host ''
    Write-Host 'KEYS ONLY IN ENGLISH:'
    $onlyEn | ForEach-Object { Write-Host ("  " + $_) }
}

$duplicates = @()
foreach ($file in $xaml) {
    $keys = [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), 'x:Key\s*=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $duplicates += ($keys | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { "$($file.Name): $($_.Name) x$($_.Count)" })
}

if ($duplicates.Count -gt 0) {
    Write-Host ''
    Write-Host 'DUPLICATE KEYS WITHIN A FILE:'
    $duplicates | ForEach-Object { Write-Host ("  " + $_) }
}

if ($missing.Count -eq 0 -and $onlyFr.Count -eq 0 -and $onlyEn.Count -eq 0 -and $duplicates.Count -eq 0) {
    Write-Host ''
    Write-Host 'OK - every referenced key is defined and fr/en are symmetric.'
}

$codeKeys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
foreach ($file in (Get-ChildItem -LiteralPath $project -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })) {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw), '(?:LocalizationManager|\bT)\s*\(\s*"([^"]+)"')) {
        [void]$codeKeys.Add($match.Groups[1].Value)
    }
}

$unresolved = @($codeKeys | Where-Object { -not $defined.Contains($_) })
Write-Host ''
Write-Host ("keys requested from C#: {0}" -f $codeKeys.Count)
if ($unresolved.Count -gt 0) {
    Write-Host 'LOCALIZATION KEYS WITH NO ENTRY:'
    $unresolved | ForEach-Object { Write-Host ("  " + $_) }
} else {
    Write-Host 'every key requested from C# has a dictionary entry.'
}
