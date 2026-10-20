<#
.SYNOPSIS
    Regenerates THIRD-PARTY-NOTICES.md and 3RD_PARTY_LICENSES from the dependencies Overglow ships.

.DESCRIPTION
    - backend: the NuGet packages of Overglow.App (direct and transitive; restore first), with the license and
      copyright of each package's .nuspec and its license file when it has one;
    - ui: the npm production dependencies (npm ls --omit=dev; run npm ci first) plus Tailwind CSS, whose generated
      styles are part of the bundle, with each package's LICENSE file;
    - components that come with the build: the .NET runtime and ASP.NET Core (self-contained), SQLite (in
      e_sqlite3.dll), and Hyperion.ng, whose code HyperHDR (and so Overglow) is derived from.

    Fails when a license isn't on the permissive allow list (no copyleft in the shipped program).
    3RD_PARTY_LICENSES holds the license texts of the service's components in the format the UI's About page reads
    (ui/src/settings/licenses.ts); the UI lists its own libraries itself.

.PARAMETER Check
    Don't write; fail when the files are out of date (CI).
#>
[CmdletBinding()]
param([switch] $Check)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }
$permissive = @('MIT', 'Apache-2.0', 'BSD-3-Clause', 'ISC', 'Public domain')
$mitTemplate = (Get-Content -Raw (Join-Path $PSScriptRoot 'licenses/MIT.txt')).TrimEnd()
$apacheText = (Get-Content -Raw (Join-Path $PSScriptRoot 'licenses/Apache-2.0.txt')).Trim("`r", "`n")

function Normalize([string] $text) {
    ($text -replace "`r`n?", "`n").Trim("`n") -replace '[ \t]+(?=\n)', ''
}

function New-Component($Name, $Version, $License, $Copyright, $Url, $Text, $Area) {
    if ($License -notin $permissive) { throw "$Name $Version is licensed under '$License', which isn't on the permissive allow list." }
    [pscustomobject]@{ Name = $Name; Version = $Version; License = $License; Copyright = $Copyright; Url = $Url; Text = (Normalize $Text); Area = $Area }
}

function Get-MitText([string] $copyright) {
    $line = if ($copyright -match '^(Copyright|©|\(c\))') { $copyright } else { "Copyright (c) $copyright" }
    $mitTemplate.Replace('{copyright}', $line)
}

# --- backend: NuGet packages of the app ---
$packagesRoot = ((& $dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$listing = & $dotnet list (Join-Path $repo 'backend/src/Overglow.App/Overglow.App.csproj') package --include-transitive --format json | ConvertFrom-Json
$packages = foreach ($framework in $listing.projects[0].frameworks) {
    foreach ($kind in 'topLevelPackages', 'transitivePackages') {
        if ($framework.PSObject.Properties[$kind]) { $framework.$kind | ForEach-Object { [pscustomobject]@{ Id = $_.id; Version = $_.resolvedVersion } } }
    }
}

$components = [System.Collections.Generic.List[object]]::new()
foreach ($package in ($packages | Where-Object { $_.Id -notmatch '^runtime\.' } | Sort-Object Id -Unique)) {
    $dir = Join-Path $packagesRoot "$($package.Id.ToLowerInvariant())/$($package.Version)"
    [xml] $nuspec = Get-Content (Get-ChildItem $dir -Filter '*.nuspec' | Select-Object -First 1).FullName
    $meta = $nuspec.package.metadata
    $copyright = if ($meta.PSObject.Properties['copyright']) { $meta.copyright } else { $meta.authors }
    $url = if ($meta.PSObject.Properties['projectUrl']) { $meta.projectUrl } else { "https://www.nuget.org/packages/$($package.Id)" }
    $licenseNode = $meta.license
    if ($licenseNode.type -eq 'file') {
        $text = Get-Content -Raw (Join-Path $dir $licenseNode.'#text')
        $license = switch -Regex ($text) {
            'Apache License\s+Version 2\.0' { 'Apache-2.0'; break }
            'Permission is hereby granted, free of charge' { 'MIT'; break }
            'Redistribution and use in source and binary forms' { 'BSD-3-Clause'; break }
            default { throw "Unknown license file in $($package.Id)." }
        }
    }
    else {
        $license = $licenseNode.'#text'
        $text = switch ($license) {
            'MIT' { if (Test-Path (Join-Path $dir 'LICENSE.md')) { Get-Content -Raw (Join-Path $dir 'LICENSE.md') } else { Get-MitText $copyright } }
            'Apache-2.0' { "$copyright`n`n$apacheText" }
            default { throw "No license text for $($package.Id) ($license)." }
        }
    }
    $components.Add((New-Component $package.Id $package.Version $license $copyright $url $text 'Service'))
}

$components.Add((New-Component '.NET runtime and ASP.NET Core' '10.0' 'MIT' 'Copyright (c) .NET Foundation and Contributors' 'https://github.com/dotnet/runtime' `
    ((Get-MitText 'Copyright (c) .NET Foundation and Contributors') + "`n`nThe runtime's own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT") 'Service'))
$components.Add((New-Component 'SQLite' '3' 'Public domain' 'D. Richard Hipp and contributors' 'https://sqlite.org/copyright.html' `
    'The SQLite library (e_sqlite3.dll, from SQLitePCLRaw.lib.e_sqlite3) is in the public domain: https://sqlite.org/copyright.html' 'Service'))
$components.Add((New-Component 'Hyperion.ng' '' 'MIT' 'Copyright (c) 2014-2020 Hyperion Project' 'https://github.com/hyperion-project/hyperion.ng' `
    (Get-MitText 'Copyright (c) 2014-2020 Hyperion Project') 'Service'))

# --- ui: npm production dependencies and the bundled Tailwind CSS ---
$ui = Join-Path $repo 'ui'
Push-Location $ui
try { $npmPaths = npm ls --omit=dev --all --parseable } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'npm ls failed: run npm ci in ui/ first.' }
$npmDirs = @($npmPaths | Select-Object -Skip 1) + (Join-Path $ui 'node_modules/tailwindcss')
foreach ($dir in ($npmDirs | Sort-Object -Unique)) {
    $manifest = Get-Content -Raw (Join-Path $dir 'package.json') | ConvertFrom-Json
    $licenseFile = Get-ChildItem $dir -File | Where-Object Name -match '^licen[cs]e' | Select-Object -First 1
    if (-not $licenseFile) { throw "$($manifest.name) has no license file." }
    $text = Get-Content -Raw $licenseFile.FullName
    $copyright = ((Normalize $text) -split "`n" | Where-Object { $_ -match 'Copyright' } | Select-Object -First 1)
    $url = if ($manifest.PSObject.Properties['homepage']) { $manifest.homepage } else { "https://www.npmjs.com/package/$($manifest.name)" }
    $components.Add((New-Component $manifest.name $manifest.version $manifest.license $copyright $url $text 'Web interface'))
}

# --- THIRD-PARTY-NOTICES.md ---
$md = [System.Text.StringBuilder]::new()
[void] $md.AppendLine('# Third-party notices')
[void] $md.AppendLine()
[void] $md.AppendLine('Overglow includes the components below. Overglow itself is derived from [HyperHDR](https://github.com/awawa-dev/HyperHDR)')
[void] $md.AppendLine('(MIT, see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md)).')
[void] $md.AppendLine()
[void] $md.AppendLine('Generated by `packaging/Update-ThirdPartyNotices.ps1` from the NuGet packages of the app and the npm production')
[void] $md.AppendLine('dependencies of the web interface; don''t edit it by hand. Every license is permissive (' + (($components.License | Sort-Object -Unique) -join ', ') + '): no copyleft.')
foreach ($area in 'Service', 'Web interface') {
    [void] $md.AppendLine()
    [void] $md.AppendLine("## $area")
    [void] $md.AppendLine()
    [void] $md.AppendLine('| Component | Version | License | Copyright |')
    [void] $md.AppendLine('|---|---|---|---|')
    foreach ($c in ($components | Where-Object Area -eq $area | Sort-Object Name)) {
        [void] $md.AppendLine("| [$($c.Name)]($($c.Url)) | $($c.Version) | $($c.License) | $($c.Copyright -replace '\|', '\|') |")
    }
}
[void] $md.AppendLine()
[void] $md.AppendLine('## License texts')
foreach ($c in ($components | Sort-Object Area, Name)) {
    [void] $md.AppendLine()
    [void] $md.AppendLine("### $($c.Name) $($c.Version)".TrimEnd())
    [void] $md.AppendLine()
    [void] $md.AppendLine('```text')
    [void] $md.AppendLine($c.Text)
    [void] $md.AppendLine('```')
}

# --- 3RD_PARTY_LICENSES (the UI's About page) ---
$plain = [System.Text.StringBuilder]::new()
foreach ($c in ($components | Where-Object Area -eq 'Service' | Sort-Object Name)) {
    if ($plain.Length -gt 0) { [void] $plain.AppendLine() }
    [void] $plain.AppendLine('==============================')
    [void] $plain.AppendLine($c.Name)
    [void] $plain.AppendLine('==============================')
    [void] $plain.AppendLine()
    [void] $plain.AppendLine($c.Text)
}

$outputs = @{
    (Join-Path $repo 'THIRD-PARTY-NOTICES.md') = $md.ToString() -replace "`r`n", "`n"
    (Join-Path $repo '3RD_PARTY_LICENSES') = $plain.ToString() -replace "`r`n", "`n"
}
$stale = @()
foreach ($path in $outputs.Keys) {
    $current = if (Test-Path $path) { [IO.File]::ReadAllText($path) -replace "`r`n", "`n" } else { '' }
    if ($current -ne $outputs[$path]) {
        $stale += $path
        if (-not $Check) { [IO.File]::WriteAllText($path, $outputs[$path], [Text.UTF8Encoding]::new($false)) }
    }
}
if ($Check -and $stale) { throw "Out of date (run packaging/Update-ThirdPartyNotices.ps1): $($stale -join ', ')" }
"{0} components ({1}); licenses: {2}" -f $components.Count, (($components | Group-Object Area | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '), (($components.License | Sort-Object -Unique) -join ', ')
