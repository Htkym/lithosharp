[CmdletBinding()]
param(
    [ValidateSet('Configuration', 'Published', 'Output')][string] $Check = 'Configuration',
    [string] $ReleaseTag,
    [string] $PackageDirectory,
    [string] $Output
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Check -eq 'Output') {
    foreach ($file in @('index.html', '404.html', 'sitemap.xml')) {
        if (!(Test-Path -LiteralPath (Join-Path $Output $file) -PathType Leaf)) { throw "Pages output is missing $file." }
    }
    # Serve the generated custom404 at the artifact root, not under a second
    # lithosharp directory. GitHub Pages adds the repository path itself.
    $notFound = Get-Content -LiteralPath (Join-Path $Output '404.html') -Raw
    if ($notFound -notmatch '/lithosharp/' -or $notFound -match 'https://example\.invalid') {
        throw 'Pages custom404 must use the official /lithosharp/ BasePath.'
    }
    $sitemap = Get-Content -LiteralPath (Join-Path $Output 'sitemap.xml') -Raw
    if ($sitemap -notmatch 'https://htkym\.github\.io/lithosharp/' -or $sitemap -match 'https://example\.invalid') {
        throw 'Pages sitemap must use the official BaseUrl.'
    }
    Write-Host 'Pages root, custom404 and canonical URL checked.'
    return
}

if ($ReleaseTag -cnotmatch '^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw 'Pages requires an existing stable vX.Y.Z release tag; previews are not publishable.'
}
if ($env:GITHUB_REPOSITORY -cne 'Htkym/lithosharp' -or $env:DEFAULT_BRANCH -cne 'main' -or
    $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/main') {
    throw 'Pages must be dispatched from the official repository default main branch.'
}
$version = $ReleaseTag.Substring(1)
foreach ($package in (Get-Content -LiteralPath (Join-Path $repo 'eng/release-packages.json') -Raw | ConvertFrom-Json)) {
    [xml] $project = Get-Content -LiteralPath (Join-Path $repo $package.project) -Raw
    if (@($project.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText }) -cnotcontains $version) { throw 'Pages release tag must match every release package version.' }
}
$pair = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
if ($pair.componentVersion -match '-') { throw 'Pages requires the accepted published stable Syntamark pair, not the active preview pair.' }
if ($Check -eq 'Configuration') { return }

& "$PSScriptRoot/Test-MainRelease.ps1" -CiWorkflow build.yml -ReleaseTag $ReleaseTag
# Reuse the stable-pair gate, including live source/producer/NuGet archive checks.
& "$PSScriptRoot/Test-ReleasePipeline.ps1" -Check CoreTag -Tag $ReleaseTag
$json = gh api --method GET --paginate --slurp "repos/$env:GITHUB_REPOSITORY/actions/workflows/publish-nuget.yml/runs" -f "head_sha=$env:GITHUB_SHA" -f event=push -f per_page=100
if ($LASTEXITCODE -ne 0) { throw 'Could not read NuGet publication evidence.' }
$runs = @(($json -join "`n" | ConvertFrom-Json) | ForEach-Object { $_.workflow_runs } | Where-Object {
    $_.head_sha -eq $env:GITHUB_SHA -and $_.head_branch -ceq $ReleaseTag -and $_.event -eq 'push' -and
    $_.path -eq '.github/workflows/publish-nuget.yml'
} | Sort-Object id -Descending)
if (!$runs.Count -or $runs[0].status -ne 'completed' -or $runs[0].conclusion -ne 'success') {
    throw 'The latest exact-tag/SHA NuGet publication must have succeeded before Pages.'
}
# Verify official archive identity before the package-only consumer restores from
# NuGet.org. Never pack/relabel the development tree to satisfy this gate.
$feed = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path ([IO.Path]::GetTempPath()) ('ls-pages-published-' + [guid]::NewGuid().ToString('N')) }
$null = New-Item -ItemType Directory -Path $feed
foreach ($package in (Get-Content -LiteralPath (Join-Path $repo 'eng/release-packages.json') -Raw | ConvertFrom-Json)) {
    $id = $package.id.ToLowerInvariant()
    $archivePath = Join-Path $feed "$id.$version.nupkg"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $archivePath
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $specs = @($archive.Entries | Where-Object { $_.FullName -cmatch '^[^/]+\.nuspec$' })
        if ($specs.Count -ne 1) { throw "Official $id archive must contain one root nuspec." }
        $reader = [IO.StreamReader]::new($specs[0].Open())
        try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.package.metadata
        if ($metadata.id -cne $package.id -or $metadata.version -cne $version -or
            $metadata.repository.commit -cne $env:GITHUB_SHA -or
            $metadata.repository.url.TrimEnd('/') -cne 'https://github.com/Htkym/lithosharp') {
            throw "Official $id package does not match the exact release source identity."
        }
    } finally { $archive.Dispose() }
}
Write-Host "Verified published stable packages for $ReleaseTag at $env:GITHUB_SHA."
