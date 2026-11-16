[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ls-pages-guards-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$passed = 0
function Reject([scriptblock] $Action, [string] $Reason) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        $caught = $true
    }
    if (!$caught) { throw "Expected rejection: $Reason" }
    $script:passed++
}
function Check-Workflow([string] $Text) {
    $trigger = [regex]::Match($Text, '(?ms)^on:\r?\n(.*?)(?=^permissions:)').Groups[1].Value
    if ($trigger -notmatch '^  workflow_dispatch:' -or $trigger -match '(?m)^  (push|pull_request|workflow_run|schedule):' -or
        $trigger -notmatch 'type: boolean\r?\n        default: false') { throw 'Pages must be manual and default to validation only.' }
    $deploy = [regex]::Match($Text, '(?ms)^  deploy:\r?\n.*\z').Value
    if ($deploy -notmatch [regex]::Escape("if: github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.publish") -or
        $deploy -notmatch 'needs: build' -or $deploy -notmatch 'name: github-pages') { throw 'Pages deploy must require explicit main publication and the checked build.' }
    if ([regex]::Matches($Text, 'pages: write').Count -ne 1 -or [regex]::Matches($Text, 'id-token: write').Count -ne 1 -or
        $deploy -notmatch '      pages: write\r?\n      id-token: write' -or
        ($Text.Substring(0, $Text.IndexOf('  deploy:')) -match '\bwrite\b')) { throw 'Only Pages deploy may request write permissions.' }
    if ([regex]::Matches($Text, 'Test-PagesPublish.ps1 -Check Published -ReleaseTag \$env:RELEASE_TAG').Count -ne 2) { throw 'Pages must check publication before build and deployment.' }
    if ($Text -notmatch 'SITE_BASE_URL: https://htkym.github.io/lithosharp/' -or
        $Text -notmatch '-BaseUrl \$env:SITE_BASE_URL' -or $Text -match '-PackageFeed|dotnet pack' -or
        $Text -notmatch 'Test-PagesPublish.ps1 -Check Output' -or $deploy -notmatch 'enablement: false' -or
        $deploy -notmatch 'CONFIGURED_BASE_URL.TrimEnd') { throw 'Pages must use checked official packages, output and destination without enabling hosting.' }
}
$workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/publish-pages.yml') -Raw
Check-Workflow $workflow
$passed++
Reject { Check-Workflow ($workflow.Replace('  workflow_dispatch:', "  push:`n  workflow_dispatch:")) } 'manual and default'
Reject { Check-Workflow ($workflow.Replace('default: false', 'default: true')) } 'manual and default'
Reject { Check-Workflow ($workflow.Replace(' && inputs.publish', '')) } 'explicit main publication'
Reject { Check-Workflow ($workflow.Replace('needs: build', 'needs: other')) } 'checked build'
Reject { Check-Workflow ($workflow.Replace('  contents: read', "  contents: read`n  pages: write")) } 'Only Pages deploy'
Reject { Check-Workflow ($workflow.Replace('-Check Published', '-Check Configuration')) } 'before build and deployment'
Reject { Check-Workflow ($workflow.Replace('-BaseUrl $env:SITE_BASE_URL', '-PackageFeed local')) } 'checked official packages'
Reject { Check-Workflow ($workflow.Replace('enablement: false', 'enablement: true')) } 'without enabling hosting'

# Run the real fail-closed configuration gate against owned offline fixtures.
$packages = Get-Content -LiteralPath (Join-Path $repo 'eng/release-packages.json') -Raw | ConvertFrom-Json
foreach ($relative in @('eng/Test-PagesPublish.ps1', 'eng/release-packages.json', 'eng/markdown/component-pair.json') + @($packages.project)) {
    $target = Join-Path $fixture $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $target) -Force
    Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
}
$guard = Join-Path $fixture 'eng/Test-PagesPublish.ps1'
[xml] $project = Get-Content -LiteralPath (Join-Path $repo $packages[0].project) -Raw
$tag = 'v' + @($project.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText })[0]
$pairPath = Join-Path $fixture 'eng/markdown/component-pair.json'
$pair = Get-Content -LiteralPath $pairPath -Raw | ConvertFrom-Json
$pair.componentVersion = '2.0.0'
$pair | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $pairPath
$names = @('GITHUB_REPOSITORY', 'DEFAULT_BRANCH', 'GITHUB_EVENT_NAME', 'GITHUB_REF')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:GITHUB_REPOSITORY = 'Htkym/lithosharp'
    $env:DEFAULT_BRANCH = 'main'
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    $env:GITHUB_REF = 'refs/heads/main'
    & $guard -ReleaseTag $tag
    $passed++
    foreach ($bad in @('', 'v3.0.0-preview.1', 'v3.0.0+local', 'v03.0.0', '../v3.0.0')) {
        Reject { & $guard -ReleaseTag $bad } 'existing stable'
    }
    Reject { & $guard -ReleaseTag 'v999.0.0' } 'match every release package'
    $env:GITHUB_REF = 'refs/heads/work/major-integrated-20261113'
    Reject { & $guard -ReleaseTag $tag } 'official repository default main'
    $env:GITHUB_REF = 'refs/tags/' + $tag
    Reject { & $guard -ReleaseTag $tag } 'official repository default main'
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_EVENT_NAME = 'pull_request'
    Reject { & $guard -ReleaseTag $tag } 'official repository default main'
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    $env:GITHUB_REPOSITORY = 'someone/lithosharp'
    Reject { & $guard -ReleaseTag $tag } 'official repository default main'
    $env:GITHUB_REPOSITORY = 'Htkym/lithosharp'
    $pair.componentVersion = '2.0.0-preview.2026111303'
    $pair | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $pairPath
    Reject { & $guard -ReleaseTag $tag } 'accepted published stable Syntamark pair'
    # Exercise the live-provenance code with fake command boundaries, never the
    # real network. Only fixture copies of the existing release scripts are stubbed.
    $pair.componentVersion = '2.0.0'
    $pair | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $pairPath
    'param($CiWorkflow, $ReleaseTag)' | Set-Content (Join-Path $fixture 'eng/Test-MainRelease.ps1')
    'param($Check, $Tag)' | Set-Content (Join-Path $fixture 'eng/Test-ReleasePipeline.ps1')
    $savedSha = $env:GITHUB_SHA
    $env:GITHUB_SHA = '1234567890123456789012345678901234567890'
    $savedFixture = Get-Variable PagesGuardFixture -Scope Global -ErrorAction SilentlyContinue
    $global:PagesGuardFixture = @{}
    $global:PagesGuardFixture.publicationStatus = 'success'
    $global:PagesGuardFixture.archiveCommit = $env:GITHUB_SHA
    $global:PagesGuardFixture.publicationBranch = $tag
    $global:PagesGuardFixture.archiveVersion = $tag.Substring(1)
    $global:PagesGuardFixture.archiveRepository = 'https://github.com/Htkym/lithosharp'
    $global:PagesGuardFixture.missingArchive = $false
    function gh {
        $global:LASTEXITCODE = 0
        @{ workflow_runs = @(@{ id = 123; head_sha = $env:GITHUB_SHA; head_branch = $global:PagesGuardFixture.publicationBranch;
            event = 'push'; path = '.github/workflows/publish-nuget.yml'; status = 'completed'; conclusion = $global:PagesGuardFixture.publicationStatus }) } |
            ConvertTo-Json -Depth 5 -Compress
    }
    function Invoke-WebRequest($Uri, $OutFile) {
        if ($global:PagesGuardFixture.missingArchive) { throw 'Official archive unavailable.' }
        $id = @($packages | Where-Object { $Uri -like ('*/' + $_.id.ToLowerInvariant() + '/*') })[0].id
        $archive = [IO.Compression.ZipFile]::Open($OutFile, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry("$id.nuspec").Open())
            try {
                $writer.Write("<package><metadata><id>$id</id><version>$($global:PagesGuardFixture.archiveVersion)</version><repository type=`"git`" url=`"$($global:PagesGuardFixture.archiveRepository)`" commit=`"$($global:PagesGuardFixture.archiveCommit)`" /></metadata></package>")
            } finally { $writer.Dispose() }
        } finally { $archive.Dispose() }
    }
    try {
        & $guard -Check Published -ReleaseTag $tag
        $passed++
        $global:PagesGuardFixture.publicationStatus = 'failure'
        Reject { & $guard -Check Published -ReleaseTag $tag } 'NuGet publication must have succeeded'
        $global:PagesGuardFixture.publicationStatus = 'success'
        $global:PagesGuardFixture.publicationBranch = 'main'
        Reject { & $guard -Check Published -ReleaseTag $tag } 'NuGet publication must have succeeded'
        $global:PagesGuardFixture.publicationBranch = $tag
        $global:PagesGuardFixture.archiveCommit = '0' * 40
        Reject { & $guard -Check Published -ReleaseTag $tag } 'exact release source identity'
        $global:PagesGuardFixture.archiveCommit = $env:GITHUB_SHA
        $global:PagesGuardFixture.archiveVersion = '999.0.0'
        Reject { & $guard -Check Published -ReleaseTag $tag } 'exact release source identity'
        $global:PagesGuardFixture.archiveVersion = $tag.Substring(1)
        $global:PagesGuardFixture.archiveRepository = 'https://github.com/someone/lithosharp'
        Reject { & $guard -Check Published -ReleaseTag $tag } 'exact release source identity'
        $global:PagesGuardFixture.archiveRepository = 'https://github.com/Htkym/lithosharp'
        $global:PagesGuardFixture.missingArchive = $true
        Reject { & $guard -Check Published -ReleaseTag $tag } 'Official archive unavailable'
    } finally {
        $env:GITHUB_SHA = $savedSha
        if ($savedFixture) { $global:PagesGuardFixture = $savedFixture.Value } else { Remove-Variable PagesGuardFixture -Scope Global }
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
$output = Join-Path $fixture 'output'
$null = New-Item -ItemType Directory -Path $output
'<html></html>' | Set-Content (Join-Path $output 'index.html')
'<a href="/lithosharp/en/">Home</a>' | Set-Content (Join-Path $output '404.html')
'<urlset><url><loc>https://htkym.github.io/lithosharp/en/</loc></url></urlset>' | Set-Content (Join-Path $output 'sitemap.xml')
& $guard -Check Output -Output $output
$passed++
'<a href="/docs/en/">Home</a>' | Set-Content (Join-Path $output '404.html')
Reject { & $guard -Check Output -Output $output } 'custom404'
'<a href="/lithosharp/en/">Home</a>' | Set-Content (Join-Path $output '404.html')
'<urlset><url><loc>https://example.invalid/lithosharp/en/</loc></url></urlset>' | Set-Content (Join-Path $output 'sitemap.xml')
Reject { & $guard -Check Output -Output $output } 'sitemap'
$consumptionGuard = Join-Path $repo 'eng/Test-PagesPackageConsumption.ps1'
$consumer = Join-Path $fixture 'consumer'
$cache = Join-Path $fixture 'cache'
$official = Join-Path $fixture 'official'
$null = New-Item -ItemType Directory -Force -Path (Join-Path $consumer 'obj'), (Join-Path $consumer 'checks/obj'), (Join-Path $cache 'lithosharp/3.0.0'), $official
$archive = Join-Path $cache 'lithosharp/3.0.0/lithosharp.3.0.0.nupkg'
'official fixture archive' | Set-Content $archive
Copy-Item $archive (Join-Path $official 'lithosharp.3.0.0.nupkg')
foreach ($relative in @('obj/project.assets.json', 'checks/obj/project.assets.json')) {
    '{"libraries":{"LithoSharp/3.0.0":{"type":"package"}}}' | Set-Content (Join-Path $consumer $relative)
}
& $consumptionGuard -SiteRoot $consumer -PackageCache $cache -PublishedPackageDirectory $official -Version '3.0.0'
$passed++
'{"libraries":{"LithoSharp/3.0.1":{"type":"package"}}}' | Set-Content (Join-Path $consumer 'checks/obj/project.assets.json')
Reject { & $consumptionGuard -SiteRoot $consumer -PackageCache $cache -PublishedPackageDirectory $official -Version '3.0.0' } 'does not match Pages release version'
'{"libraries":{"LithoSharp/3.0.0":{"type":"package"}}}' | Set-Content (Join-Path $consumer 'checks/obj/project.assets.json')
'candidate archive with same version' | Set-Content $archive
Reject { & $consumptionGuard -SiteRoot $consumer -PackageCache $cache -PublishedPackageDirectory $official -Version '3.0.0' } 'differs from the verified official package'
$build = Get-Content (Join-Path $repo 'site/Build.ps1') -Raw
if ([regex]::Matches($build, 'dotnet restore .* --locked-mode').Count -ne 2 -or
    [regex]::Matches($build, 'dotnet restore .* --force-evaluate').Count -ne 2 -or
    $build -notmatch 'Test-PagesPackageConsumption.ps1' -or $workflow -notmatch '-PublishedPackageDirectory') {
    throw 'Pages must refresh and locked-restore both owned locks and verify exact consumed archives.'
}
$passed++
Write-Host "Pages guards passed: $passed offline checks. Fixtures: $fixture. No network or publication."
