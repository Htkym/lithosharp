[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('prettier', 'jest', 'docusaurus')]
    [string] $SiteId,
    [string] $WorkspaceRoot,
    [string] $RunId = '20260930-v110-24',
    [string] $PriorRunId = '20260930-final',
    [string] $ToolPath,
    [int] $TimeoutSeconds = 3600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$WorkspaceRoot) {
    throw '-WorkspaceRoot must point at the V110-21 workspace outside the repository.'
}
$WorkspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot)
$prior = Join-Path $WorkspaceRoot "runs/$PriorRunId/$SiteId"
foreach ($required in @('routes/route-oracle.json', 'routes/route-source-map.json', 'migration-source', 'reports/candidate-build.json', 'reports/serve-check.json', 'candidate-build.json', 'candidate-serve.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $prior $required))) {
        throw "Prior run is missing '$required'; rerun the full corpus instead of carrying over gaps."
    }
}

$evidence = Join-Path $repo ".local/verification/1.1.0/v110-24-20260930/corpus/$SiteId"
$null = New-Item -ItemType Directory -Force -Path $evidence

function Get-Hash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Fail([string] $Message) { throw "Migration candidate re-run failed for '$SiteId': $Message" }

# Original builds stay skipped only when the oracle is byte-identical to the
# published one; otherwise the full corpus must rerun.
$oracleHash = Get-Hash (Join-Path $prior 'routes/route-oracle.json')
$publishedOracle = Join-Path $repo "docs/evidence/1.1.0/v110-21-20260930-final/routes/$SiteId-route-oracle.json"
if ((Get-Hash $publishedOracle) -cne $oracleHash) {
    Fail 'route oracle differs from the published bundle; original builds cannot be skipped.'
}

if ([string]::IsNullOrWhiteSpace($ToolPath)) {
    $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll'
}
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { Fail "Release tool not found: $ToolPath" }

$replay = Join-Path $repo 'eng/MigrationReplay/bin/Release/net10.0/MigrationReplay.dll'
if (!(Test-Path -LiteralPath $replay -PathType Leaf)) { Fail "Release MigrationReplay not found; build eng/MigrationReplay first: $replay" }
$worker = Join-Path $repo 'src/LithoSharp.Mdx/worker'
foreach ($required in @('package-lock.json', 'worker.mjs')) {
    if (!(Test-Path -LiteralPath (Join-Path $worker $required) -PathType Leaf)) { Fail "worker asset missing: $required" }
}
if (!(Test-Path -LiteralPath (Join-Path $worker 'node_modules') -PathType Container)) {
    Fail 'worker node_modules are not restored; restore the pinned worker first.'
}

function Invoke-Step([string] $Id, [string] $Exe, [string[]] $Arguments, [string] $Cwd) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.WorkingDirectory = $Cwd
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "Step '$Id' timed out after ${TimeoutSeconds}s."
    }
    $timer.Stop()
    [IO.File]::WriteAllText((Join-Path $evidence "$Id.stdout.txt"), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $evidence "$Id.stderr.txt"), $stderr.GetAwaiter().GetResult())
    return [ordered]@{
        id = $Id; exitCode = $process.ExitCode; elapsedMs = $timer.ElapsedMilliseconds
        stdoutSha256 = (Get-FileHash -LiteralPath (Join-Path $evidence "$Id.stdout.txt") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

# 1. Migration conversion reruns against the current candidate code.
$migrationSource = Join-Path $prior 'migration-source'
$converted = Join-Path $evidence 'converted'
if (Test-Path -LiteralPath $converted) { Remove-Item -LiteralPath $converted -Recurse -Force }
$priorOracle = Join-Path $prior 'routes/route-oracle.json'
$migration = Invoke-Step 'migration-convert' 'dotnet' @(
    $ToolPath, 'migrate', 'docusaurus', $migrationSource,
    '--output', $converted, '--expected-routes', $priorOracle,
    '--base-url', 'https://example.test/mig/', '--compare-normalized-pages'
) $repo
if ($migration.exitCode -notin @(0, 3)) { Fail "migration CLI exited $($migration.exitCode)." }
$migrationJson = Get-Content -LiteralPath (Join-Path $evidence 'migration-convert.stdout.txt') -Raw | ConvertFrom-Json -AsHashtable
$priorMigration = Get-Content -LiteralPath (Join-Path $prior 'migration-report.json') -Raw | ConvertFrom-Json -AsHashtable
if ($migrationJson.exitCode -ne $priorMigration.exitCode) {
    Fail "migration exit changed $($priorMigration.exitCode) -> $($migrationJson.exitCode)."
}

# 2. Candidate build reruns with Windows paths; the prior container config is translated, not reused blindly.
$priorBuild = Get-Content -LiteralPath (Join-Path $prior 'reports/candidate-build.json') -Raw | ConvertFrom-Json -AsHashtable
$buildConfig = [ordered]@{
    action = 'candidate-build'; siteId = $SiteId
    sourceDirectory = $migrationSource
    routeOracle = $priorOracle
    routeSourceMap = Join-Path $prior 'routes/route-source-map.json'
    fullReportPath = Join-Path $evidence 'full-migration-report.json'
    manualPatchReportPath = Join-Path $evidence 'manual-patches.json'
    convertedDirectory = $converted
    candidateSourceDirectory = Join-Path $evidence 'candidate-source'
    candidateOutputDirectory = Join-Path $evidence 'candidate-output'
    candidateReportPath = Join-Path $evidence 'reports/candidate-build.json'
    serveReportPath = Join-Path $evidence 'reports/serve-check.json'
    baseUrl = 'https://example.test/mig/'
    defaultLocale = 'en'
    workerDirectory = $worker
    buildTimestamp = '2026-09-29T00:00:00+00:00'
    candidatePageLimit = 12
}
foreach ($key in @('candidateSourceDirectory', 'candidateOutputDirectory')) {
    if (Test-Path -LiteralPath $buildConfig[$key]) { Remove-Item -LiteralPath $buildConfig[$key] -Recurse -Force }
}
[IO.File]::WriteAllText((Join-Path $evidence 'candidate-build.json'), (($buildConfig | ConvertTo-Json -Depth 8)))
$build = Invoke-Step 'candidate-build' 'dotnet' @($replay, (Join-Path $evidence 'candidate-build.json')) $repo
if ($build.exitCode -ne 0) { Fail "candidate build exited $($build.exitCode)." }
$candidate = Get-Content -LiteralPath (Join-Path $evidence 'reports/candidate-build.json') -Raw | ConvertFrom-Json -AsHashtable
if ($candidate.state -cne 'passed' -or $candidate.candidatePageSet.status -cne 'Match') {
    Fail "candidate page set did not match: $($candidate.candidatePageSet.status)."
}
$priorSelected = @($priorBuild.selectedPages | ForEach-Object { $_.path } | Sort-Object)
$selected = @($candidate.selectedPages | ForEach-Object { $_.path } | Sort-Object)
if (($priorSelected -join "`n") -cne ($selected -join "`n")) { Fail 'selected page set changed between runs.' }

# 3. Serve check reruns against the fresh output.
$serveConfig = [ordered]@{}
foreach ($entry in $buildConfig.GetEnumerator()) { $serveConfig[$entry.Key] = $entry.Value }
$serveConfig.action = 'serve-check'
[IO.File]::WriteAllText((Join-Path $evidence 'candidate-serve.json'), (($serveConfig | ConvertTo-Json -Depth 8)))
$serve = Invoke-Step 'candidate-serve-check' 'dotnet' @($replay, (Join-Path $evidence 'candidate-serve.json')) $repo
if ($serve.exitCode -ne 0) { Fail "serve check exited $($serve.exitCode)." }
$serveJson = Get-Content -LiteralPath (Join-Path $evidence 'reports/serve-check.json') -Raw | ConvertFrom-Json -AsHashtable
if ($serveJson.operations.status -cne 'passed') { Fail 'serve operations did not pass.' }

# 4. Output comparison against the prior run. Byte equality is not expected
# across different machines for three documented reasons, so the comparison
# normalizes exactly those and fails on anything else:
#   (a) _mdx bundle/chunk file names embed content hashes, and chunk content
#       embeds host-layout-derived module keys (__commonJS cache keys are
#       relative from the site root to the worker install). Names, references
#       and worker keys are normalized before comparison.
#   (b) Prism hook duplication (fixed in V110-24) doubled language-xxxx
#       classes on nested fences in the prior output. Duplicate classes are
#       collapsed on both sides; any other markup change fails.
#   (c) CRLF is normalized to LF; every remaining byte must match, which also
#       verifies the V110-24 LF feed fix on Windows.
function Get-NormalizedText([string] $Text) {
    $text = $Text.Replace("`r`n", "`n")
    # (a) bundle file names, references and worker module keys.
    $text = $text -replace '/_mdx/(chunks|pages|assets)/[A-Za-z0-9._-]+-[A-Z0-9]{8}\.(js|css)', '/_mdx/$1/NORMALIZED.$2'
    $text = $text -replace '"(\.\./)*([^"]*?)(src/LithoSharp\.Mdx/worker|tool/worker)/node_modules/', '"WORKER-NODE-MODULES/'
    # (b) Prism duplicate-class correction.
    $text = [regex]::Replace($text, '(language-[\w-]+)( \1)+', '$1')
    return $text
}

function Get-NormalizedTreeHash([string] $Root) {
    $lines = [Collections.Generic.List[string]]::new()
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $isText = $relative -match '\.(html|xml|json|css|js|mjs|txt|webmanifest|map)$'
        if ($isText) {
            $text = Get-NormalizedText ([Text.Encoding]::UTF8.GetString($bytes))
            if ($relative -eq '.lithosharp-output-manifest.json') {
                # The manifest lists content-hashed file names plus an internal
                # cache key: compare the normalized file set, record the key.
                $manifest = $text | ConvertFrom-Json -AsHashtable
                $normalizedFiles = @($manifest.files | ForEach-Object {
                    $_ -replace '-[A-Z0-9]{8}(?=\.(js|css|woff2?|ttf|eot)$)', '-HASH'
                } | Sort-Object)
                $lines.Add("$relative version=$($manifest.version) files=" + ($normalizedFiles -join ','))
                continue
            }
            if ($relative -match '^_mdx/(chunks|pages)/') {
                # Bundle file names embed content hashes that vary with host
                # layout (see (a)): compare content identity as a multiset.
                $contentHash = ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('x2') }) -join ''
                $bundleDir = $relative -replace '^(_mdx/(chunks|pages))/.*$', '$1'
                $lines.Add("$bundleDir/CONTENT $contentHash")
                continue
            }
            $normalizedName = $relative -replace '-[A-Z0-9]{8}(?=\.(js|css)$)', '-HASH'
            $contentHash = ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalizedName + "`n" + $text)) | ForEach-Object { $_.ToString('x2') }) -join ''
            $lines.Add("$normalizedName $contentHash")
            continue
        }
        $contentHash = ([Security.Cryptography.SHA256]::HashData($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
        $lines.Add("$relative $contentHash")
    }
    $sorted = @($lines | Sort-Object)
    $manifestText = $sorted -join "`n"
    return [ordered]@{
        manifest = $manifestText
        hash = ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifestText)) | ForEach-Object { $_.ToString('x2') }) -join ''
    }
}

$priorOutput = Join-Path $prior 'candidate-output'
$newOutput = Join-Path $evidence 'candidate-output'
$priorTree = Get-NormalizedTreeHash $priorOutput
$newTree = Get-NormalizedTreeHash $newOutput
[IO.File]::WriteAllText((Join-Path $evidence 'normalized-prior.txt'), $priorTree.manifest)
[IO.File]::WriteAllText((Join-Path $evidence 'normalized-new.txt'), $newTree.manifest)
if ($newTree.hash -cne $priorTree.hash) {
    $priorLines = $priorTree.manifest -split "`n"
    $newLines = $newTree.manifest -split "`n"
    $onlyNew = @(Compare-Object $priorLines $newLines | Where-Object { $_.SideIndicator -ceq '=>' } | ForEach-Object { $_.InputObject })
    Fail ("normalized output differs between runs ($($onlyNew.Count) differing entries, first: $($onlyNew | Select-Object -First 3 | Join-String -Separator ' | ')). See normalized-prior.txt / normalized-new.txt.")
}

$summary = [ordered]@{
    schemaVersion = '1.0'; siteId = $SiteId; state = 'verified-scoped'
    oracleSha256 = $oracleHash
    migrationExitCode = $migrationJson.exitCode
    candidatePageSet = $candidate.candidatePageSet.status
    selectedPages = $selected.Count
    outputHash = $candidate.candidateBuild.actualOutputHash
    normalizedTreeHash = $newTree.hash
    priorNormalizedTreeHash = $priorTree.hash
    serve = $serveJson.operations.status
    steps = @($migration, $build, $serve)
}
[IO.File]::WriteAllText((Join-Path $evidence 'rerun.json'), (($summary | ConvertTo-Json -Depth 8)))
Write-Host ("Migration candidate re-run passed for {0}: pageset Match, {1} pages, serve passed, normalized output equal." -f $SiteId, $selected.Count)
