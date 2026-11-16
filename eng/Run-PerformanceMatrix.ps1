<# Runs an explicitly configured comparison. Build identical harness sources in
both source trees first. Products, raw files and failed attempts stay separate.
Use timeoutSeconds=0 for no overall job timeout. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Configuration)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$configPath = [IO.Path]::GetFullPath($Configuration)
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 1) { throw 'Unsupported performance configuration schema.' }
if ($config.rounds -lt 1 -or $config.rounds -gt 20 -or $config.rounds -ne [Math]::Floor($config.rounds)) { throw 'rounds must be an integer from 1 to 20.' }
if (@($config.sizes).Count -eq 0 -or @($config.scenarios).Count -eq 0) { throw 'Configure nonempty sizes and scenarios.' }
if ($config.timeoutSeconds -is [bool] -or ($config.timeoutSeconds -ne 0 -and ($config.timeoutSeconds -lt 30 -or $config.timeoutSeconds -gt 86400))) { throw 'timeoutSeconds must be 0 (unbounded), or between 30 and 86400.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = [IO.Path]::GetFullPath($config.evidenceRoot, (Split-Path $configPath))
$null = New-Item -ItemType Directory -Force -Path $evidence
$manifestPath = Join-Path $evidence 'campaign.json'
if (Test-Path $manifestPath) { throw 'Use a new evidenceRoot; earlier campaigns are never overwritten.' }
$targets = @($config.targets)
if ($targets.Count -lt 1) { throw 'Configure at least one target.' }
$labels = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($target in $targets) {
    if ($target.label -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or !$labels.Add($target.label)) { throw 'Use unique path-safe target labels.' }
    if ($target.role -notin 'baseline','candidate') { throw 'Target roles must be baseline or candidate.' }
}
if (@($targets | Where-Object role -eq 'baseline').Count -gt 1) { throw 'Only one baseline is supported per campaign.' }
if (!$config.dotnetSdk -or $config.dotnetSdk -notmatch '^\d+\.\d+\.\d+') { throw 'Configure the exact shared dotnetSdk before measuring.' }
if ($config.profile -eq 'typed-docs-quality' -and @($targets | Where-Object role -eq 'baseline').Count -gt 0 -and
    $config.comparisonKind -cne 'feature-scope-difference') {
    throw 'Typed Docs raw before/after costs need comparisonKind=feature-scope-difference; source-enabled raw Quality facts differ.'
}
function Source-Manifest([string] $Root, [string[]] $Paths) {
    $result = [ordered]@{}
    foreach ($folder in $Paths) {
        Get-ChildItem -LiteralPath (Join-Path $Root $folder) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' -and $_.Extension -in '.cs','.csproj','.props','.targets','.json','.js','.mjs','.ts','.jsx','.tsx','.css' } |
            Sort-Object FullName | ForEach-Object {
                $result[[IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/')] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
    }
    return $result
}
$identities = @()
$commonHarness = $null
$commonBuildArguments = $null
foreach ($target in $targets) {
    $root = [IO.Path]::GetFullPath($target.root, (Split-Path $configPath))
    if ([string]::IsNullOrWhiteSpace($target.featureScope)) { throw 'Declare the actual feature scope of each target before observing results.' }
    Push-Location $root
    try { $sdk = (& dotnet --version | Out-String).Trim(); if ($LASTEXITCODE -ne 0) { throw 'Could not identify SDK.' } }
    finally { Pop-Location }
    if ($sdk -cne $config.dotnetSdk) { throw "SDK mismatch for $($target.label): $sdk. Rebuild both harnesses with the configured SDK." }
    $commit = (& git -C $root rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cne $target.commit) { throw "Commit mismatch for $($target.label)." }
    $changes = @(& git -C $root status --porcelain=v1 --untracked-files=all)
    if ($target.role -eq 'baseline' -and @($changes | Where-Object { $_ -notmatch '^.. (benchmarks/|eng/performance/)' }).Count) {
        throw 'Baseline product must remain clean; only common harness overlays are permitted.'
    }
    $harnessFiles = Source-Manifest $root @("benchmarks/$($config.harnessProject)", 'eng/performance')
    # Project lockfiles necessarily record different product versions. Preserve
    # those in each target, but compare the measurement sources themselves.
    foreach ($key in @($harnessFiles.Keys)) { if ($key.EndsWith('packages.lock.json') -or $key.EndsWith('packages.acceptance.lock.json')) { $harnessFiles.Remove($key) } }
    $harness = $harnessFiles | ConvertTo-Json -Depth 4 -Compress
    if ($null -eq $commonHarness) { $commonHarness = $harness }
    elseif ($harness -cne $commonHarness) { throw 'Targets must contain identical harness sources.' }
    $dll = [IO.Path]::GetFullPath($target.harnessDll, $root)
    if (!(Test-Path $dll)) { throw "Missing harness: $dll" }
    if (!$target.buildReceipt) { throw 'Each target needs a Measure-CompilationStage build receipt with the harness as an OutputArtifact.' }
    $buildReceipt = [IO.Path]::GetFullPath($target.buildReceipt, (Split-Path $configPath))
    $build = Get-Content -LiteralPath $buildReceipt -Raw | ConvertFrom-Json
    if ($build.schemaVersion -ne 1 -or $build.status -cne 'completed' -or $build.stage -ceq 'restore' -or $build.sdk -cne $sdk) {
        throw "Invalid successful build receipt for $($target.label)."
    }
    $binaryHash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    if (@($build.artifacts | Where-Object { [IO.Path]::GetFullPath($_.path) -eq $dll -and $_.sha256 -ceq $binaryHash }).Count -ne 1) {
        throw 'The measured harness must match the exact artifact in the successful build receipt.'
    }
    $buildArguments = @($build.arguments | Where-Object { $_ -cne $build.project -and $_ -notlike '-bl:*' }) | ConvertTo-Json -Compress
    if ($null -eq $commonBuildArguments) { $commonBuildArguments = $buildArguments }
    elseif ($commonBuildArguments -cne $buildArguments) { throw 'Targets must use identical build arguments apart from project and binlog paths.' }
    $identities += [ordered]@{
        label = $target.label; role = $target.role; root = $root; commit = $commit; sdk = $sdk; featureScope = $target.featureScope
        changes = $changes; productSources = Source-Manifest $root @('src')
        harnessSources = $harness | ConvertFrom-Json; harnessDll = $dll
        harnessSha256 = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
        buildReceipt = $buildReceipt; buildReceiptSha256 = (Get-FileHash -LiteralPath $buildReceipt -Algorithm SHA256).Hash.ToLowerInvariant()
        buildArguments = $build.arguments
        dependencyLockSha256 = (Get-FileHash (Join-Path $root "benchmarks/$($config.harnessProject)/packages.lock.json") -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$campaign = [ordered]@{
    schemaVersion = 1; status = 'running'; configuration = $config
    configurationSha256 = (Get-FileHash $configPath -Algorithm SHA256).Hash.ToLowerInvariant()
    driverSha256 = (Get-FileHash $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    targets = $identities; runs = @(); timingNote = 'Stage durations overlap; never sum stages as total elapsed time.'
    comparisonKind = $config.comparisonKind; automaticSpeedupOrVerdict = 'not-calculated'
}
function Save-Campaign { [IO.File]::WriteAllText($manifestPath, ($campaign | ConvertTo-Json -Depth 20)) }
Save-Campaign
$failed = $false
try {
    for ($round = 1; $round -le $config.rounds; $round++) {
        $order = @(0..($targets.Count - 1))
        if ($round % 2 -eq 0) { [array]::Reverse($order) }
        foreach ($index in $order) {
            $target = $targets[$index]; $identity = $identities[$index]
            $runRoot = Join-Path $evidence "$($target.label)/round-$round"
            $arguments = @{
                Harness = $config.harness; Profile = $config.profile; Label = $target.label
                RunId = $config.runId; TaskId = $config.taskId; Sizes = ($config.sizes -join ',')
                Scenarios = ($config.scenarios -join ','); Runs = 1; TimeoutSeconds = $config.timeoutSeconds
                EvidenceRoot = $evidence; StateRoot = (Join-Path $runRoot 'runs')
                HarnessRoot = $identity.root; HarnessDll = $identity.harnessDll
            }
            if ($config.harness -eq 'mdx') { $arguments.WorkerDirectory = Join-Path $identity.root 'src/LithoSharp.Mdx/worker' }
            try {
                & (Join-Path $repo 'eng/Measure-IncrementalScenarios.ps1') @arguments
                $campaign.runs += @{ label = $target.label; round = $round; status = 'completed'; summary = "$runRoot/benchmark-baseline.json" }
            } catch {
                $failed = $true
                $campaign.runs += @{ label = $target.label; round = $round; status = 'failed'; error = $_.Exception.Message; summary = "$runRoot/benchmark-baseline.json" }
            }
            Save-Campaign
        }
    }
    foreach ($identity in $identities) {
        if ((Get-FileHash -LiteralPath $identity.harnessDll -Algorithm SHA256).Hash.ToLowerInvariant() -cne $identity.harnessSha256 -or
            (Get-FileHash -LiteralPath $identity.buildReceipt -Algorithm SHA256).Hash.ToLowerInvariant() -cne $identity.buildReceiptSha256) {
            throw "Harness binary or build receipt changed during campaign: $($identity.label)."
        }
        if ((Source-Manifest $identity.root @('src') | ConvertTo-Json -Compress) -cne ($identity.productSources | ConvertTo-Json -Compress)) {
            throw "Product sources changed during campaign: $($identity.label). Results are not a fixed-source comparison."
        }
        $finalHarness = Source-Manifest $identity.root @("benchmarks/$($config.harnessProject)", 'eng/performance')
        foreach ($key in @($finalHarness.Keys)) { if ($key.EndsWith('packages.lock.json') -or $key.EndsWith('packages.acceptance.lock.json')) { $finalHarness.Remove($key) } }
        if (($finalHarness | ConvertTo-Json -Compress) -cne ($identity.harnessSources | ConvertTo-Json -Compress)) {
            throw "Harness sources changed during campaign: $($identity.label)."
        }
    }
    $campaign.status = if ($failed) { 'partial' } else { 'completed' }
} catch { $campaign.status = 'failed'; $campaign.error = $_.Exception.Message; throw }
finally { Save-Campaign }
if ($failed) { throw 'A campaign attempt failed; inspect retained raw evidence.' }
