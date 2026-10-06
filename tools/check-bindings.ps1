Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\SquadDns'

# Public members the views can bind to: the view model plus every item class.
$members = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::Ordinal)
$declFiles = Get-ChildItem -LiteralPath (Join-Path $project 'ViewModels') -Recurse -Filter *.cs

foreach ($file in $declFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($pattern in @(
            'public\s+(?:static\s+)?[\w\.<>\[\]\?]+\s+(\w+)\s*\{',
            'public\s+(?:static\s+)?[\w\.<>\[\]\?]+\s+(\w+)\s*=>'
        )) {
        foreach ($match in [regex]::Matches($text, $pattern)) {
            [void]$members.Add($match.Groups[1].Value)
        }
    }

    foreach ($record in [regex]::Matches($text, 'record\s+\w+[^({]*\(([^)]*)\)')) {
        foreach ($parameter in ($record.Groups[1].Value -split ',')) {
            if ($parameter -match '(\w+)\s*(?:=|$)') {
                [void]$members.Add($Matches[1])
            }
        }
    }
}

# WPF-inherited names and the empty path used by RelativeSource bindings.
$allow = @(
    'Item', 'Items', 'DataContext', 'Content', 'Command', 'CommandParameter', 'Tag',
    'Path', 'RelativeSource', 'ElementName', 'Source', 'Name', 'Id', 'Length', 'Count'
)
foreach ($name in $allow) { [void]$members.Add($name) }

$unbound = @()

foreach ($file in (Get-ChildItem -LiteralPath $project -Recurse -Filter *.xaml | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\{Binding\s+([A-Za-z_]\w*)')) {
        $path = $match.Groups[1].Value
        if (-not $members.Contains($path)) {
            $unbound += [pscustomobject]@{ File = $file.Name; Path = $path }
        }
    }
}

Write-Host ("bindable members discovered : {0}" -f $members.Count)
Write-Host ("unresolved binding paths    : {0}" -f @($unbound).Count)

if ($unbound.Count -eq 0) {
    Write-Host 'OK - every {Binding X} path names a member declared in Viewmodels.'
} else {
    Write-Host ''
    Write-Host 'BINDING PATHS WITH NO DECLARED MEMBER:'
    $unbound | Group-Object Path | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0}  ({1})" -f $_.Name, (($_.Group | ForEach-Object { $_.File } | Sort-Object -Unique) -join ', '))
    }
}
