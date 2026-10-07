<#
.SYNOPSIS
Builds the public evidence bundle for a V110-21 migration corpus run.

.DESCRIPTION
Assembles a committed bundle under docs/evidence/1.1.0/v110-21-<run-id>/ from
the local (uncommitted) run evidence and the workspace run directories, then
passes every emitted file through eng/Sanitize-Evidence.ps1. Full traces,
workspace checkouts and machine logs never enter the bundle: it carries
summaries, hashes, exact rerun commands, the version manifest, limits and the
per-site pass/fail list. Stage results and counts are preserved verbatim.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RunId,
    [Parameter(Mandatory)] [string] $WorkspaceRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = Join-Path $repo ".local/verification/1.1.0/v110-21-$RunId"
if (!(Test-Path -LiteralPath (Join-Path $evidence 'result.json') -PathType Leaf)) {
    throw "Local run evidence not found: $evidence"
}
$WorkspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot)
$runRoot = Join-Path $WorkspaceRoot "runs/$RunId"
$bundle = Join-Path $repo "docs/evidence/1.1.0/v110-21-$RunId"
$null = New-Item -ItemType Directory -Force -Path $bundle
# Static README files are authored, not generated; refresh only machine output.
foreach ($generated in @('version-manifest.json', 'run.json', 'commands.txt', 'sanitizer-manifest.json', 'sites', 'routes')) {
    $victim = Join-Path $bundle $generated
    if (Test-Path -LiteralPath $victim) { Remove-Item -LiteralPath $victim -Recurse -Force }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("v110-22-bundle-{0}" -f [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging, (Join-Path $staging 'sites'), (Join-Path $staging 'routes')

function Get-Hash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$result = Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json -AsHashtable
$repoCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
# Tracked modifications at bundle time are listed verbatim; reviewers confirm
# they belong to the documented task instead of trusting a clean flag.
$trackedModified = @((& git -C $repo status --porcelain=v1 --untracked-files=no | Out-String).Split("`n") |
    Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
$dotnetSdk = ((& dotnet --version | Out-String).Trim())
$nodeVersion = ((& node --version | Out-String).Trim())
$workerLock = Join-Path $repo 'src/LithoSharp.Mdx/worker/package-lock.json'

$versionManifest = [ordered]@{
    schemaVersion = '1.0'
    runId = $RunId
    corpusId = $result.corpusId
    corpusManifestSha256 = $result.manifestSha256
    repositoryCommit = $repoCommit
    trackedModifiedFiles = @($trackedModified)
    corePackages = '1.0.0'
    extensionVersion = '0.1.0'
    languageServerPackage = '1.1.0'
    dotnetSdk = $dotnetSdk
    node = $nodeVersion
    workerLockSha256 = (Get-Hash $workerLock)
    containerImage = $result.containerRuntime.image.image
    containerImageId = $result.containerRuntime.image.imageId
    containerNodeVersion = $result.containerRuntime.image.nodeVersion
    containerDotnetSdkVersion = $result.containerRuntime.image.dotnetSdkVersion
    migrationReplayTreeSha256 = $result.containerRuntime.migrationReplaySha256
    generatedAt = ((Get-Date).ToUniversalTime().ToString('o'))
}
[IO.File]::WriteAllText((Join-Path $staging 'version-manifest.json'), (($versionManifest | ConvertTo-Json -Depth 8)))

$runSummary = [ordered]@{
    schemaVersion = '1.0'
    planTask = $result.planTask
    runId = $result.runId
    corpusId = $result.corpusId
    state = $result.state
    fullSiteEquivalenceClaimed = $result.fullSiteEquivalenceClaimed
    manifestSha256 = $result.manifestSha256
    sites = @($result.sites | ForEach-Object {
        [ordered]@{ siteId = $_.siteId; state = $_.state; failure = $(if ($_.ContainsKey('failure')) { $_.failure } else { $null }) }
    })
}
[IO.File]::WriteAllText((Join-Path $staging 'run.json'), (($runSummary | ConvertTo-Json -Depth 8)))

function Get-Prop($Object, [string] $Name) {
    if ($Object -is [Collections.IDictionary] -and $Object.Contains($Name)) { return $Object[$Name] }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

foreach ($site in $result.sites) {
    $siteId = [string]$site.siteId
    $siteReport = Get-Content -LiteralPath (Join-Path $evidence "$siteId/site-report.json") -Raw | ConvertFrom-Json -AsHashtable
    $siteRun = Join-Path $runRoot $siteId
    $candidate = Get-Content -LiteralPath (Join-Path $siteRun 'reports/candidate-build.json') -Raw | ConvertFrom-Json -AsHashtable
    $serve = Get-Content -LiteralPath (Join-Path $siteRun 'reports/serve-check.json') -Raw | ConvertFrom-Json -AsHashtable
    $manualPath = Join-Path $siteRun 'manual-patches.json'
    $manual = Get-Content -LiteralPath $manualPath -Raw | ConvertFrom-Json -AsHashtable

    $tallies = @{}
    foreach ($action in @($manual.unappliedManualActions)) {
        $key = [string]$action.before.diagnosticId
        if (!$tallies.ContainsKey($key)) { $tallies[$key] = 0 }
        $tallies[$key]++
    }
    $componentTally = @{}
    foreach ($change in @($candidate.manualComponentChanges)) {
        $key = "$($change.kind)/$($change.functionalEquivalence)"
        if (!$componentTally.ContainsKey($key)) { $componentTally[$key] = 0 }
        $componentTally[$key]++
    }

    $summary = [ordered]@{
        schemaVersion = '1.0'
        siteId = $siteId
        state = $siteReport.state
        source = $siteReport.source
        stages = @($siteReport.stages | ForEach-Object {
            [ordered]@{
                id = $_.id; description = (Get-Prop $_ 'description'); status = $_.status
                command = (Get-Prop $_ 'command'); network = (Get-Prop $_ 'network')
                exitCode = (Get-Prop $_ 'exitCode'); timedOut = (Get-Prop $_ 'timedOut'); elapsedMs = (Get-Prop $_ 'elapsedMs')
                inputSha256 = (Get-Prop $_ 'inputSha256'); outputSha256 = (Get-Prop $_ 'outputSha256')
                stdoutFile = (Get-Prop $_ 'stdoutFile'); stderrFile = (Get-Prop $_ 'stderrFile')
                stdoutSha256 = (Get-Prop $_ 'stdoutSha256'); stderrSha256 = (Get-Prop $_ 'stderrSha256')
            }
        })
        migration = [ordered]@{
            exitCode = $candidate.migrationExitCode
            rawStatus = $candidate.rawRouteComparison.status
            missing = @($candidate.rawRouteComparison.missing)
            extra = @($candidate.rawRouteComparison.extra)
            sourceRouteCount = $candidate.rawRouteComparison.sourceRouteCount
            targetRouteCount = $candidate.rawRouteComparison.targetRouteCount
        }
        candidatePageSet = $candidate.candidatePageSet
        selectedPages = @($candidate.selectedPages)
        selectedPageLimit = $candidate.selectedPageLimit
        candidateBuild = [ordered]@{
            diagnosticCount = $candidate.candidateBuild.diagnosticCount
            warningCount = $candidate.candidateBuild.warningCount
            generatedRouteCount = $candidate.candidateBuild.generatedRouteCount
            selectedRouteCount = $candidate.candidateBuild.selectedRouteCount
            actualOutputHash = $candidate.candidateBuild.actualOutputHash
            compiledMdxModules = $candidate.candidateBuild.compiledMdxModules
            renderedMdxPages = $candidate.candidateBuild.renderedMdxPages
        }
        operations = [ordered]@{
            status = $serve.operations.status
            pages = @($serve.operations.pages)
            navigation = $serve.operations.navigation
            asset = $serve.operations.asset
            search = $serve.operations.search
        }
        manualActions = [ordered]@{
            appliedCount = 0
            unappliedCount = @($manual.unappliedManualActions).Count
            diagnosticIdTally = $tallies
            componentChangeTally = $componentTally
            fullReportSha256 = (Get-Hash $manualPath)
            note = 'Full manual-action text stays local; rerun the corpus to reproduce it byte-for-byte.'
        }
        knownLimits = @($candidate.knownLimits)
    }
    [IO.File]::WriteAllText((Join-Path $staging "sites/$siteId.json"), (($summary | ConvertTo-Json -Depth 16)))

    Copy-Item -LiteralPath (Join-Path $siteRun 'routes/route-oracle.json') -Destination (Join-Path $staging "routes/$siteId-route-oracle.json")
}

$commands = @(
    '# Reproduce this bundle (Windows, Docker Desktop running, user approval for third-party builds required).'
    '# Workspace must be outside the repository; it holds sources, build trees and full traces.'
    "pwsh -NoProfile -File eng/Run-MigrationCorpus.ps1 -Action run-all -WorkspaceRoot <workspace> -RunId $RunId -AllowThirdPartyBuild"
    "pwsh -NoProfile -File eng/New-EvidenceBundle.ps1 -RunId $RunId -WorkspaceRoot <workspace>"
    '# Manifest and container digests are pinned in eng/verification/1.1.0/migration-sites.json.'
)
[IO.File]::WriteAllText((Join-Path $staging 'commands.txt'), (($commands -join "`r`n") + "`r`n"))

$roots = @("repo=$repo", "workspace=$WorkspaceRoot")
& (Join-Path $repo 'eng/Sanitize-Evidence.ps1') -InputPath $staging -OutputPath $bundle -RootToken ($roots -join ';')
if ($LASTEXITCODE -ne 0) { throw 'Sanitizer rejected the bundle content.' }

Remove-Item -LiteralPath $staging -Recurse -Force
Write-Host "Evidence bundle written to $bundle"
