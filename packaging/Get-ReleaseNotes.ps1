<#
.SYNOPSIS
    Writes the CHANGELOG.md section of a version (its release notes) to a file.

.DESCRIPTION
    Finds "## <Version> – <date>" (an en dash or a hyphen) in CHANGELOG.md and writes everything up to the next "## "
    heading to -OutFile, with wrapped lines joined (GitHub's release page keeps every line break). Fails when the
    version has no section or an empty one, so a release can't go out without its notes.

.EXAMPLE
    ./packaging/Get-ReleaseNotes.ps1 -Version 0.2.0 -OutFile artifacts/release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutFile,
    [string] $Changelog = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$lines = Get-Content $Changelog -Encoding utf8
$heading = '^## ' + [regex]::Escape($Version) + '\s+[–-]\s+\d{4}-\d{2}-\d{2}\s*$'
$start = 0..($lines.Count - 1) | Where-Object { $lines[$_] -match $heading } | Select-Object -First 1
if ($null -eq $start) { throw "CHANGELOG.md has no '## $Version – <yyyy-MM-dd>' section." }

$end = ($start + 1)..($lines.Count - 1) | Where-Object { $lines[$_] -match '^## ' } | Select-Object -First 1
if ($null -eq $end) { $end = $lines.Count }

if ($end -le $start + 1) { throw "The CHANGELOG.md section of $Version is empty." }

# CHANGELOG.md wraps its lines; GitHub's release page shows every line break, so wrapped lines are joined again.
$out = [System.Collections.Generic.List[string]]::new()
foreach ($line in $lines[($start + 1)..($end - 1)]) {
    $continues = $line.Trim() -and $out.Count -gt 0 -and $out[-1].Trim() -and $out[-1] -notmatch '^#' -and
        $line -notmatch '^\s*([-*#|]|\d+\.|```)'
    if ($continues) { $out[-1] = $out[-1].TrimEnd() + ' ' + $line.Trim() } else { $out.Add($line) }
}
$notes = ($out -join "`n").Trim()
if (-not $notes) { throw "The CHANGELOG.md section of $Version is empty." }

$dir = Split-Path -Parent $OutFile
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
Set-Content -Path $OutFile -Value $notes -Encoding utf8NoBOM
Write-Host "Release notes of $Version -> $OutFile"
