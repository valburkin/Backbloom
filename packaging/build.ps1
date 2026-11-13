<#
.SYNOPSIS
    Builds the web UI, publishes the backend and packs the Windows installer (Velopack).

.DESCRIPTION
    1. ui/: npm ci (when node_modules is missing) and npm run build -> ui/dist
    2. backend/: dotnet publish of Backbloom.App, self-contained win-x64, ReadyToRun, with ui/dist as ui/
    3. Velopack (vpk, pinned in packaging/dotnet-tools.json): Setup.exe, the full package, the portable zip and the
       release feed files for the channel, in artifacts/releases.

    The version comes from backend/Directory.Build.props (with -Dev: a pre-release of the next patch version).
    Nothing is installed and nothing is uploaded (the release workflow uploads with vpk upload github).

.PARAMETER Channel
    Release channel the package belongs to (an installed copy follows it). Default: stable.

.PARAMETER SignTemplate
    Code signing hook, passed to vpk as --signTemplate: a command with {{file}} where the file to sign goes, e.g.
    'signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a {{file}}'. vpk signs every program
    and library of the package and Setup.exe. Without it the package is unsigned (SmartScreen warns on download).

.PARAMETER ReleaseNotes
    Markdown file with the release notes (packaging/Get-ReleaseNotes.ps1 extracts them from CHANGELOG.md); shown on
    the GitHub release page.

.PARAMETER PreviousReleaseRepo
    GitHub repository URL whose latest release is downloaded first, so the package includes a delta update from it.
    Skipped with a warning when the repository has no release yet.

.PARAMETER Output
    Output folder (default: artifacts/ at the repository root): publish/ and releases/ are recreated.

.PARAMETER SkipUi
    Use the existing ui/dist instead of building the UI.

.PARAMETER Dev
    Development build to try uncommitted changes: version <next patch>-dev.<date>.<time>, e.g.
    0.1.4-dev.20261020.103045 after 0.1.3. Each build installs over the previous one, and the release of 0.1.4 over
    all of them. Directory.Build.props is not changed.

.EXAMPLE
    ./packaging/build.ps1
    ./packaging/build.ps1 -Dev
    ./packaging/build.ps1 -Channel beta -SignTemplate 'signtool sign /fd sha256 /a {{file}}'
#>
[CmdletBinding()]
param(
    [string] $Channel = 'stable',
    [string] $SignTemplate,
    [string] $ReleaseNotes,
    [string] $PreviousReleaseRepo,
    [string] $Output,
    [switch] $SkipUi,
    [switch] $Dev
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
# Relative paths are relative to where the script was started; resolve them before changing directories.
if (-not $Output) { $Output = Join-Path $repo 'artifacts' }
$Output = [System.IO.Path]::GetFullPath($Output, (Get-Location).Path)
if ($ReleaseNotes) { $ReleaseNotes = (Resolve-Path $ReleaseNotes).Path }
$publish = Join-Path $Output 'publish'
$releases = Join-Path $Output 'releases'

# The SDK pinned by backend/global.json: DOTNET_ROOT when set (the development machine keeps it outside PATH).
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { 'dotnet' }

function Invoke-Step([string] $Name, [scriptblock] $Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

[xml] $props = Get-Content (Join-Path $repo 'backend/Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> in backend/Directory.Build.props.' }
if ($Dev) {
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "-Dev needs a release version in Directory.Build.props, not $version." }
    # SemVer: 0.1.3 < 0.1.4-dev.20261020.103045 < 0.1.4-dev.20261020.110000 < 0.1.4. The time is a plain number:
    # numeric identifiers can't have leading zeros (NuGet rejects 002820 after midnight), and they compare as numbers.
    $now = Get-Date
    $version = '{0}.{1}.{2}-dev.{3}.{4}' -f $Matches[1], $Matches[2], ([int] $Matches[3] + 1), $now.ToString('yyyyMMdd'), [int] $now.ToString('HHmmss')
}
Write-Host "Backbloom $version, channel $Channel"

if (-not $SkipUi) {
    Push-Location (Join-Path $repo 'ui')
    try {
        if (-not (Test-Path 'node_modules')) { Invoke-Step 'npm ci' { npm ci --no-audit --no-fund } }
        Invoke-Step 'Build the UI' { npm run build }
    }
    finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $repo 'ui/dist/index.html'))) { throw 'ui/dist/index.html is missing: build the UI first.' }

Remove-Item $publish, $releases -Recurse -Force -ErrorAction SilentlyContinue
Invoke-Step 'Publish the backend' {
    # from backend/, so backend/global.json pins the SDK
    Push-Location (Join-Path $repo 'backend')
    try { & $dotnet publish 'src/Backbloom.App/Backbloom.App.csproj' -c Release -r win-x64 -o $publish --nologo "-p:Version=$version" }
    finally { Pop-Location }
}
foreach ($required in 'backbloom.exe', 'ui/index.html', 'LICENSE.txt', 'NOTICE.md', 'THIRD-PARTY-NOTICES.md') {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "The published folder lacks $required." }
}

Push-Location $PSScriptRoot
try {
    Invoke-Step 'Restore vpk' { & $dotnet tool restore }
    if ($PreviousReleaseRepo) {
        # Velopack builds a delta package from the previous full package found in the output folder.
        $download = @('vpk', 'download', 'github', '--repoUrl', $PreviousReleaseRepo, '--channel', $Channel, '--outputDir', $releases)
        if ($env:GITHUB_TOKEN) { $download += @('--token', $env:GITHUB_TOKEN) }  # avoids the API's anonymous rate limit
        & $dotnet @download
        if ($LASTEXITCODE -ne 0) { Write-Warning "No previous release downloaded from $PreviousReleaseRepo; packing without a delta." }
    }
    $pack = @(
        'vpk', 'pack',
        '--packId', 'BackbloomApp',
        '--packTitle', 'Backbloom',
        '--packAuthors', 'Val Burkin',
        '--packVersion', $version,
        '--packDir', $publish,
        '--mainExe', 'backbloom.exe',
        '--runtime', 'win-x64',
        '--channel', $Channel,
        '--icon', (Join-Path $repo 'branding/backbloom.ico'),
        '--splashImage', (Join-Path $repo 'branding/installer/splash.png'),
        '--shortcuts', 'StartMenuRoot',
        '--outputDir', $releases
    )
    if ($SignTemplate) { $pack += @('--signTemplate', $SignTemplate) }
    if ($ReleaseNotes) { $pack += @('--releaseNotes', $ReleaseNotes) }
    Invoke-Step 'Pack the installer' { & $dotnet @pack }
}
finally { Pop-Location }

Write-Host ''
Get-ChildItem $releases -File | Sort-Object Name | ForEach-Object {
    '{0,-60} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
