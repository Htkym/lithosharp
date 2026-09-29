[CmdletBinding()]
param(
    [ValidateSet('validate', 'run-site', 'run-all')]
    [string] $Action = 'validate',
    [string] $SiteId,
    [string] $WorkspaceRoot,
    [string] $RunId,
    [string] $ToolPath,
    [string] $CandidateBaseUrl = 'https://example.test/mig/',
    [switch] $AllowThirdPartyBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repo 'eng/verification/1.1.0/migration-sites.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

function Get-Hash([string] $Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Cannot hash missing file: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextHash([string] $Text) {
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-TreeHash([string] $Root) {
    $full = [IO.Path]::GetFullPath($Root)
    if (!(Test-Path -LiteralPath $full -PathType Container)) { throw "Cannot hash missing directory: $full" }
    $entries = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $full -Recurse -File -Force | Sort-Object FullName)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Cannot hash a reparse point: $($file.FullName)" }
        $relative = [IO.Path]::GetRelativePath($full, $file.FullName).Replace('\', '/')
        $null = $entries.Add("$relative|$($file.Length)|$(Get-Hash $file.FullName)")
    }
    return Get-TextHash (($entries -join "`n") + "`n")
}

function ConvertTo-DockerPath([string] $Path) {
    return [IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Write-Json([string] $Path, $Value) {
    $full = [IO.Path]::GetFullPath($Path)
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($full))
    [IO.File]::WriteAllText($full, ($Value | ConvertTo-Json -Depth 24))
}

function Invoke-LoggedProcess(
    [string] $FileName,
    [string[]] $Arguments,
    [string] $WorkingDirectory,
    [string] $StdoutPath,
    [string] $StderrPath,
    [int] $TimeoutSeconds = 7200,
    [hashtable] $Environment = @{}
) {
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($StdoutPath)))
    $info = [Diagnostics.ProcessStartInfo]::new($FileName)
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $info.ArgumentList.Add([string] $argument) }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[[string]$entry.Key] = [string]$entry.Value }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        $process.Kill($true)
        $process.WaitForExit()
    }
    $timer.Stop()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($StdoutPath, $stdout)
    [IO.File]::WriteAllText($StderrPath, $stderr)
    $exitCode = if ($timedOut) { $null } else { $process.ExitCode }
    $process.Dispose()
    return [ordered]@{
        exitCode = $exitCode
        timedOut = $timedOut
        elapsedMs = $timer.ElapsedMilliseconds
        stdoutFile = [IO.Path]::GetFileName($StdoutPath)
        stderrFile = [IO.Path]::GetFileName($StderrPath)
        stdoutSha256 = Get-Hash $StdoutPath
        stderrSha256 = Get-Hash $StderrPath
    }
}

function Convert-ToCommandText([string[]] $Command) {
    return ($Command | ForEach-Object { if ($_ -match '\s|"') { '"' + $_.Replace('"', '\"') + '"' } else { $_ } }) -join ' '
}

function Test-Manifest {
    if ($manifest.schemaVersion -ne '1.0') { throw "Unsupported migration corpus manifest schema '$($manifest.schemaVersion)'." }
    if (@($manifest.sites).Count -ne 3) { throw 'The fixed migration corpus must declare exactly three sites.' }
    if ([string]$manifest.container.nodeImage -notmatch '@sha256:[0-9a-f]{64}$') { throw 'The container node image must be digest-pinned.' }
    if ([string]$manifest.container.dotnetImage -notmatch '@sha256:[0-9a-f]{64}$') { throw 'The container dotnet image must be digest-pinned.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($site in $manifest.sites) {
        if (!$ids.Add([string]$site.id)) { throw "Duplicate site id '$($site.id)'." }
        if ([string]$site.repository -notmatch '^https://github\.com/') { throw "Site '$($site.id)' must use a public HTTPS GitHub source." }
        if ([string]$site.commit -notmatch '^[0-9a-f]{40}$') { throw "Site '$($site.id)' needs a full pinned commit SHA." }
        if ([string]$site.license -ne 'MIT' -or [string]$site.licenseFile -ne 'LICENSE') { throw "Site '$($site.id)' has no fixed license provenance." }
        $targetPath = [string]$site.targetPath
        $targetSegments = $targetPath.Split([char[]]@('/', '\'))
        if ([IO.Path]::IsPathRooted($targetPath) -or $targetSegments -contains '..') { throw "Site '$($site.id)' has an unsafe targetPath." }
        if (@($site.inputMappings).Count -eq 0 -or @($site.buildVariants).Count -eq 0) { throw "Site '$($site.id)' is missing input mappings or build variants." }
        if (@($site.lockFiles).Count -eq 0) { throw "Site '$($site.id)' declares no lockfiles." }
        foreach ($prepOut in @($site['preBuildOutputs'] | Where-Object { $_ })) {
            $prepStr = [string]$prepOut
            if ([IO.Path]::IsPathRooted($prepStr) -or $prepStr.Split([char[]]@('/', '\')) -contains '..') { throw "Site '$($site.id)' has an unsafe preBuildOutputs entry." }
        }
    }
    return [ordered]@{
        schemaVersion = '1.0'
        corpusId = $manifest.corpusId
        sourceSetKind = $manifest.provenance.kind
        sourceCount = @($manifest.sites).Count
        sources = @($manifest.sites | ForEach-Object {
            [ordered]@{ id = $_.id; repository = $_.repository; tag = $_.tag; commit = $_.commit; version = $_.version; license = $_.license; targetPath = $_.targetPath }
        })
        manifestSha256 = $manifestHash
        validated = $true
    }
}

function Add-Stage($Record, [string] $Id, [string] $Description, [string] $Command, [string] $Network,
    [string] $InputHash, [string] $OutputHash, $Result, [int[]] $AllowedExitCodes = @(0)) {
    $ok = !$Result.timedOut -and $AllowedExitCodes -contains [int]$Result.exitCode
    $null = $Record.stages.Add([ordered]@{
        id = $Id
        description = $Description
        status = if ($ok) { 'passed' } else { 'failed' }
        command = $Command
        network = $Network
        exitCode = $Result.exitCode
        timedOut = $Result.timedOut
        elapsedMs = $Result.elapsedMs
        inputSha256 = $InputHash
        outputSha256 = $OutputHash
        stdoutFile = $Result.stdoutFile
        stderrFile = $Result.stderrFile
        stdoutSha256 = $Result.stdoutSha256
        stderrSha256 = $Result.stderrSha256
    })
    Save-SiteRecord $Record
    if (!$ok) { throw "Stage '$Id' failed (exit $($Result.exitCode), timeout=$($Result.timedOut))." }
}

function Save-SiteRecord($Record) {
    Write-Json (Join-Path $Record.evidenceDirectory 'site-report.json') $Record
}

function Invoke-ContainerStage($Record, [string] $Id, [string] $Description, [string] $Network,
    [string] $SourcePath, [string] $RunPath, [string] $WorkingDirectory, [string[]] $Command,
    [string] $InputHash, [string] $OutputPath, [int[]] $AllowedExitCodes = @(0),
    [string] $SourceVolume = '', [string] $TmpVolume = '') {
    $logs = Join-Path $Record.evidenceDirectory 'logs'
    $stdoutPath = Join-Path $logs "$Id.stdout.txt"
    $stderrPath = Join-Path $logs "$Id.stderr.txt"
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @('run', '--rm', '--network', $Network, '--read-only', '--pids-limit', '512',
        '--security-opt', 'no-new-privileges', '--tmpfs', '/tmp:rw,nosuid,nodev,size=1g')) { $null = $arguments.Add($value) }
    if ($SourceVolume) {
        # Named Linux volume: Windows bind mounts cannot carry Linux exec bits,
        # so yarn/npm postinstall scripts fail on bind-mounted node_modules.
        $null = $arguments.Add('--volume')
        $null = $arguments.Add("${SourceVolume}:/source")
    }
    elseif ($SourcePath) {
        $null = $arguments.Add('--mount')
        $null = $arguments.Add("type=bind,source=$(ConvertTo-DockerPath $SourcePath),target=/source")
    }
    if ($TmpVolume) {
        # The tmpfs /tmp breaks yarn build-script execution (EACCES on spawned
        # helpers), so point TMPDIR at a volume-backed directory instead.
        $null = $arguments.Add('--volume')
        $null = $arguments.Add("${TmpVolume}:/wintmp")
    }
    $null = $arguments.Add('--mount')
    $null = $arguments.Add("type=bind,source=$(ConvertTo-DockerPath $RunPath),target=/run")
    $null = $arguments.Add('--workdir')
    $null = $arguments.Add($WorkingDirectory)
    foreach ($value in @(
        '--env', 'HOME=/run/home', '--env', 'USERPROFILE=/run/home', '--env', 'DOTNET_CLI_HOME=/run/dotnet-home',
        '--env', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '--env', 'DOTNET_NOLOGO=1', '--env', 'COREPACK_HOME=/opt/corepack',
        '--env', 'YARN_CACHE_FOLDER=/run/cache/yarn', '--env', 'npm_config_cache=/run/cache/npm',
        '--env', 'npm_config_userconfig=/dev/null', '--env', 'npm_config_registry=https://registry.npmjs.org',
        '--env', 'CI=1', '--env', 'GIT_TERMINAL_PROMPT=0', '--env', 'GIT_CONFIG_NOSYSTEM=1', '--env', 'GIT_CONFIG_GLOBAL=/dev/null',
        '--env', 'GITHUB_TOKEN=', '--env', 'GH_TOKEN=', '--env', 'NPM_TOKEN=', '--env', 'YARN_NPM_AUTH_TOKEN=',
        '--env', 'CROWDIN_PERSONAL_TOKEN=', '--env', 'NETLIFY_AUTH_TOKEN=')) { $null = $arguments.Add($value) }
    if ($TmpVolume) { $null = $arguments.Add('--env'); $null = $arguments.Add('TMPDIR=/wintmp') }
    $null = $arguments.Add([string]$manifest.container.image)
    foreach ($value in $Command) { $null = $arguments.Add([string]$value) }

    $result = Invoke-LoggedProcess 'docker' $arguments.ToArray() $repo $stdoutPath $stderrPath 14400
    $outputHash = if ($OutputPath -and (Test-Path -LiteralPath $OutputPath -PathType Container)) { Get-TreeHash $OutputPath }
        elseif ($OutputPath -and (Test-Path -LiteralPath $OutputPath -PathType Leaf)) { Get-Hash $OutputPath }
        else { $null }
    Add-Stage $Record $Id $Description (Convert-ToCommandText $Command) $Network $InputHash $outputHash $result $AllowedExitCodes
    return $result
}

function Get-GitValue([string] $Root, [string[]] $GitArguments) {
    # Windows checkouts need long-path support for deep fixture trees.
    $value = & git -C $Root -c core.longpaths=true @GitArguments
    if ($LASTEXITCODE -ne 0) { throw "git command failed: git -C '$Root' $($GitArguments -join ' ')" }
    return ($value | Out-String).Trim()
}

function Get-DependencyStateHash([string] $Root) {
    $possibleMarkers = @(
        (Join-Path $Root '.yarn/install-state.gz'),
        (Join-Path $Root '.yarn-state.yml'),
        (Join-Path $Root 'node_modules/.yarn-state.yml'),
        (Join-Path $Root 'node_modules/.package-lock.json'),
        (Join-Path $Root 'node_modules/.yarn-integrity'),
        (Join-Path $Root 'website/node_modules/.yarn-state.yml'),
        (Join-Path $Root 'website/node_modules/.package-lock.json'),
        (Join-Path $Root 'website/node_modules/.yarn-integrity')
    )
    $markers = @($possibleMarkers | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object)
    if ($markers.Count -eq 0) { return $null }
    $items = foreach ($file in $markers) { "$( [IO.Path]::GetRelativePath($Root, $file).Replace('\', '/') )|$((Get-Item -LiteralPath $file).Length)|$(Get-Hash $file)" }
    return Get-TextHash ($items -join "`n")
}

function Invoke-VolumeCommand($Record, [string] $Volume, [string] $Script, [string] $LogId) {
    $logs = Join-Path $Record.evidenceDirectory 'logs'
    $stdoutPath = Join-Path $logs "$LogId.stdout.txt"
    $stderrPath = Join-Path $logs "$LogId.stderr.txt"
    $result = Invoke-LoggedProcess 'docker' @('run', '--rm', '--network', 'none', '--read-only', '--pids-limit', '512',
        '--security-opt', 'no-new-privileges', '--tmpfs', '/tmp:rw,nosuid,nodev,size=1g',
        '--volume', "${Volume}:/source", '--workdir', '/',
        [string]$manifest.container.image, 'sh', '-c', $Script) $repo $stdoutPath $stderrPath 3600
    if ($result.timedOut -or [int]$result.exitCode -ne 0) { throw "Volume helper '$LogId' failed (exit $($result.exitCode))." }
    return Get-Content -LiteralPath $stdoutPath -Raw
}

function Get-VolumeDependencyHash($Record, [string] $Volume, [string] $LogId) {
    $scriptText = 'for m in .yarn/install-state.gz .yarn-state.yml node_modules/.yarn-state.yml node_modules/.package-lock.json node_modules/.yarn-integrity website/node_modules/.yarn-state.yml website/node_modules/.package-lock.json website/node_modules/.yarn-integrity; do if [ -f "/source/$m" ]; then echo "$m|$(stat -c%s "/source/$m")|$(sha256sum "/source/$m" | cut -d" " -f1)"; fi; done | sort'
    $text = Invoke-VolumeCommand $Record $Volume $scriptText $LogId
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return Get-TextHash ($text.Trim() + "`n")
}

function Get-VolumeTreeHash($Record, [string] $Volume, [string] $Subpath, [string] $LogId) {
    # Symlinks are excluded: volume trees hold install/build outputs where only
    # regular files are compared. Newlines in file names are not supported,
    # matching the host tree-hash limitation.
    if ($Subpath.Contains("'")) { throw "Volume subpath with a quote is not supported: $Subpath" }
    $shPath = "'$Subpath'"
    $listing = Invoke-VolumeCommand $Record $Volume "cd /source && find $shPath -type f -printf '%P|%s\n' | LC_ALL=C sort" "$LogId-listing"
    $digests = Invoke-VolumeCommand $Record $Volume "cd /source && cd $shPath && find . -type f -exec sha256sum {} + | LC_ALL=C sort -k 2" "$LogId-digests"
    $lengths = @{}
    foreach ($line in $listing.Split("`n")) {
        $trimmed = $line.Trim()
        if (!$trimmed) { continue }
        $separator = $trimmed.LastIndexOf('|')
        if ($separator -lt 0) { throw "Unexpected volume tree listing line for '$LogId'." }
        $lengths[$trimmed.Substring(0, $separator)] = $trimmed.Substring($separator + 1)
    }
    $entries = [Collections.Generic.List[string]]::new()
    foreach ($line in $digests.Split("`n")) {
        $trimmed = $line.Trim()
        if (!$trimmed) { continue }
        $match = [Regex]::Match($trimmed, '^([0-9a-f]{64})\s+(.+)$')
        if (!$match.Success) { throw "Unexpected volume digest line for '$LogId'." }
        $digestPath = $match.Groups[2].Value
        if ($digestPath.StartsWith('./')) { $digestPath = $digestPath.Substring(2) }
        if (!$lengths.ContainsKey($digestPath)) { throw "Unexpected volume digest line for '$LogId'." }
        $null = $entries.Add("$digestPath|$($lengths[$digestPath])|$($match.Groups[1].Value)")
    }
    $sorted = @($entries | Sort-Object)
    return Get-TextHash (($sorted -join "`n") + "`n")
}

function Copy-ManifestInputs($Site, [string] $SourceRoot, [string] $Destination, [string] $RunDirectory, $Record) {
    if (Test-Path -LiteralPath $Destination) { throw "Mapped source already exists: $Destination" }
    $null = New-Item -ItemType Directory -Path $Destination
    $files = [Collections.Generic.List[object]]::new()
    $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($mapping in $Site.inputMappings) {
        $source = Join-Path $SourceRoot ([string]$mapping.source)
        $target = Join-Path $Destination ([string]$mapping.target)
        if (!(Test-Path -LiteralPath $source)) {
            if ([bool]$mapping.required) { throw "Required mapped source input is missing: $($mapping.source)" }
            $null = $files.Add([ordered]@{ source = $mapping.source; target = $mapping.target; present = $false; reason = 'Optional mapping is absent at the pinned commit.' })
            continue
        }
        $isDirectory = Test-Path -LiteralPath $source -PathType Container
        $items = if ($isDirectory) { Get-ChildItem -LiteralPath $source -Recurse -File -Force | Sort-Object FullName } else { @(Get-Item -LiteralPath $source -Force) }
        foreach ($item in $items) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Mapped content contains a reparse point: $($item.FullName)" }
            $suffix = if ($isDirectory) { [IO.Path]::GetRelativePath($source, $item.FullName) } else { '' }
            $targetFile = [IO.Path]::GetFullPath($(if ($suffix) { Join-Path $target $suffix } else { $target }))
            $relativeTarget = [IO.Path]::GetRelativePath($Destination, $targetFile).Replace('\', '/')
            if (!$targets.Add($relativeTarget)) { throw "Two migration input mappings collide at '$relativeTarget'." }
            $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($targetFile))
            [IO.File]::Copy($item.FullName, $targetFile, $false)
            $null = $files.Add([ordered]@{ source = [IO.Path]::GetRelativePath($SourceRoot, $item.FullName).Replace('\', '/'); target = $relativeTarget; bytes = $item.Length; sha256 = Get-Hash $item.FullName })
        }
    }
    $migrationSourceHash = Get-TreeHash $Destination
    $mergedOraclePath = Join-Path $RunDirectory 'routes/route-oracle.json'
    $mappingManifest = [ordered]@{
        schemaVersion = '1.0'; corpusId = $manifest.corpusId; siteId = $Site.id
        sourceCommit = $Record.source.commit; sourceTreeSha256 = $Record.source.treeSha; targetPath = $Site.targetPath
        sourceVersion = $Site.sourceVersion; license = $Site.license; migrationSourceTreeSha256 = $migrationSourceHash
        routeOracleSha256 = if (Test-Path -LiteralPath $mergedOraclePath -PathType Leaf) { Get-Hash $mergedOraclePath } else { $null }
        mappings = @($files); excludedSitePaths = @($Site.excludedSitePaths)
    }
    Write-Json (Join-Path $RunDirectory 'migration-source-manifest.json') $mappingManifest
    $Record.migrationSource = [ordered]@{ treeSha256 = $migrationSourceHash; fileCount = $files.Count }
    Save-SiteRecord $Record
    $null = $Record.stages.Add([ordered]@{ id = 'stage-migration-source'; description = 'Copy only fixed input mappings into the migration analyzer root.'; status = 'passed'; command = 'copy manifest inputMappings'; network = 'none'; exitCode = 0; inputSha256 = $Record.source.treeSha; outputSha256 = $migrationSourceHash })
    Save-SiteRecord $Record
    return $mappingManifest
}

function New-ManualPatchReport($MigrationReport, $Site) {
    $fingerprints = @{}
    foreach ($file in $MigrationReport.verdicts) { $fingerprints[[string]$file.file] = [string]$file.sourceFingerprint }
    $manualIssues = @($MigrationReport.verdicts | ForEach-Object {
        $verdict = $_
        foreach ($issue in $_.issues) {
            if ($null -eq $issue.manualStep) { continue }
            [ordered]@{
                sourcePath = $verdict.file; sourceHash = $verdict.sourceFingerprint
                before = [ordered]@{ startLine = $issue.line; endLine = $issue.line; diagnosticId = $issue.id; message = $issue.message }
                after = $null; reason = $issue.manualStep; functionalChange = $null; status = 'not-applied'
                rerunCondition = "Re-run against source fingerprint $($verdict.sourceFingerprint) after a human edit."
            }
        }
    })
    return [ordered]@{
        schemaVersion = '1.0'; siteId = $Site.id; sourceCommit = $Site.commit
        appliedPatches = @()
        manualChanges = @($MigrationReport.componentChanges)
        unappliedManualActions = $manualIssues
        note = 'This run did not apply manual patches; each item remains an explicit site-owner task.'
    }
}

function Invoke-OneSite($Site, [string] $ToolPublish) {
    $workspace = $script:WorkspaceRoot
    $evidenceDirectory = Join-Path $script:EvidenceRoot ([string]$Site.id)
    $runDirectory = Join-Path $workspace ("runs/{0}/{1}" -f $script:RunId, $Site.id)
    $sourceRoot = Join-Path $workspace ("sources/{0}" -f $Site.id)
    $null = New-Item -ItemType Directory -Force -Path $runDirectory, (Join-Path $evidenceDirectory 'logs'), (Join-Path $runDirectory 'home'), (Join-Path $runDirectory 'cache/yarn'), (Join-Path $runDirectory 'cache/npm'), (Join-Path $runDirectory 'cache/dotnet')
    $script:SiteRecord = [ordered]@{
        schemaVersion = '1.0'; corpusId = $manifest.corpusId; runId = $script:RunId; siteId = $Site.id; state = 'in-progress'
        manifestSha256 = $manifestHash; evidenceDirectory = $evidenceDirectory; runDirectory = $runDirectory
        workspaceDirectory = $workspace; stages = [Collections.Generic.List[object]]::new()
    }
    Save-SiteRecord $script:SiteRecord

    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        # Full history: original site builds read git metadata (last-update,
        # sitemap dates), which a filtered partial clone cannot serve offline.
        $fetch = Invoke-LoggedProcess 'git' @('-c', 'core.longpaths=true', '-c', 'credential.helper=', 'clone', '--single-branch', '--branch', [string]$Site.tag, [string]$Site.repository, $sourceRoot) $workspace `
            (Join-Path $evidenceDirectory 'logs/fetch.stdout.txt') (Join-Path $evidenceDirectory 'logs/fetch.stderr.txt') 14400 @{ GIT_CONFIG_NOSYSTEM='1'; GIT_CONFIG_GLOBAL='NUL'; GIT_TERMINAL_PROMPT='0' }
        if ($fetch.exitCode -ne 0) { throw "Public pinned source fetch failed for '$($Site.id)'." }
    }
    $commit = Get-GitValue $sourceRoot @('rev-parse', 'HEAD')
    if ($commit -ne $Site.commit) { throw "Source checkout is at $commit, expected pinned commit $($Site.commit)." }
    if ((Get-GitValue $sourceRoot @('status', '--porcelain', '--untracked-files=normal')).Length -ne 0) { throw "Pinned source checkout is dirty and was not changed: $sourceRoot" }
    $rootTree = Get-GitValue $sourceRoot @('rev-parse', 'HEAD^{tree}')
    $siteTree = Get-GitValue $sourceRoot @('rev-parse', "HEAD:$($Site.targetPath)")
    foreach ($lockFile in $Site.lockFiles) { if (!(Test-Path -LiteralPath (Join-Path $sourceRoot $lockFile) -PathType Leaf)) { throw "Lockfile missing at pin: $lockFile" } }
    $source = [ordered]@{
        repository = $Site.repository; tag = $Site.tag; commit = $commit; treeSha = $rootTree; targetPath = $Site.targetPath; targetTreeSha = $siteTree
        version = $Site.version; license = $Site.license; licenseFile = $Site.licenseFile; licenseFileSha256 = Get-Hash (Join-Path $sourceRoot $Site.licenseFile)
        packageManager = $Site.packageManager; lockFiles = @($Site.lockFiles | ForEach-Object { [ordered]@{ path = $_; sha256 = Get-Hash (Join-Path $sourceRoot $_) } })
    }
    $script:SiteRecord.source = $source
    Save-SiteRecord $script:SiteRecord

    $buildSource = Join-Path $runDirectory 'source'
    if (!(Test-Path -LiteralPath $buildSource -PathType Container)) {
        $cloneBuild = Invoke-LoggedProcess 'git' @('-c', 'core.longpaths=true', '-c', 'credential.helper=', 'clone', '--local', '--no-hardlinks', '--no-checkout', $sourceRoot, $buildSource) $workspace `
            (Join-Path $evidenceDirectory 'logs/build-checkout.stdout.txt') (Join-Path $evidenceDirectory 'logs/build-checkout.stderr.txt') 3600 @{ GIT_CONFIG_NOSYSTEM='1'; GIT_CONFIG_GLOBAL='NUL'; GIT_TERMINAL_PROMPT='0' }
        if ($cloneBuild.exitCode -ne 0) { throw "Independent source-build checkout failed for '$($Site.id)'." }
        $checkout = Invoke-LoggedProcess 'git' @('-c', 'core.longpaths=true', '-C', $buildSource, 'checkout', '--detach', $Site.commit) $workspace `
            (Join-Path $evidenceDirectory 'logs/build-checkout-pin.stdout.txt') (Join-Path $evidenceDirectory 'logs/build-checkout-pin.stderr.txt') 600 @{ GIT_CONFIG_NOSYSTEM='1'; GIT_CONFIG_GLOBAL='NUL'; GIT_TERMINAL_PROMPT='0' }
        if ($checkout.exitCode -ne 0) { throw "Pinned source-build checkout failed for '$($Site.id)'." }
    }
    $buildTree = Get-GitValue $buildSource @('rev-parse', 'HEAD^{tree}')
    if ((Get-GitValue $buildSource @('rev-parse', 'HEAD')) -ne $Site.commit) { throw "Build checkout for '$($Site.id)' does not match the pinned commit." }
    $null = $script:SiteRecord.stages.Add([ordered]@{ id = 'prepare-build-checkout'; description = 'Create a separate source tree for original build artifacts.'; status = 'passed'; command = "git clone --local --no-hardlinks <pinned-source> $($Site.commit)"; network = 'none'; exitCode = 0; inputSha256 = $rootTree; outputSha256 = $buildTree })
    Save-SiteRecord $script:SiteRecord

    $volumeName = (("v110-21-{0}-{1}" -f $script:RunId, $Site.id).ToLowerInvariant() -replace '[^a-z0-9_.-]', '-')
    $tmpVolumeName = ("$volumeName-tmp")
    $script:SiteRecord.sourceVolume = $volumeName
    $script:SiteRecord.tmpVolume = $tmpVolumeName
    Save-SiteRecord $script:SiteRecord
    foreach ($vol in @($volumeName, $tmpVolumeName)) {
        $staleVolume = (& docker volume inspect --format '{{.Name}}' $vol 2>$null | Out-String).Trim()
        if ($staleVolume -eq $vol) { & docker volume rm $vol *> $null }
        $null = & docker volume create $vol
        if ($LASTEXITCODE -ne 0) { throw "Could not create isolated volume '$vol'." }
    }
    $populateArgs = @('run', '--rm', '--network', 'none', '--read-only', '--pids-limit', '512',
        '--security-opt', 'no-new-privileges', '--tmpfs', '/tmp:rw,nosuid,nodev,size=1g',
        '--mount', "type=bind,source=$(ConvertTo-DockerPath $buildSource),target=/seed,readonly",
        '--volume', "${volumeName}:/source", '--workdir', '/',
        [string]$manifest.container.image, 'sh', '-c', 'cp -a /seed/. /source/')
    $populateResult = Invoke-LoggedProcess 'docker' $populateArgs $repo `
        (Join-Path $evidenceDirectory 'logs/populate-volume.stdout.txt') `
        (Join-Path $evidenceDirectory 'logs/populate-volume.stderr.txt') 3600
    $populatedHash = Get-VolumeTreeHash $script:SiteRecord $volumeName '.' 'populate-volume-tree'
    Add-Stage $script:SiteRecord 'populate-source-volume' 'Copy the pinned build checkout into an isolated Linux volume (bind mounts cannot carry Linux exec bits).' `
        'docker run --volume <site-volume>:/source sh -c ''cp -a /seed/. /source/''' 'none' $buildTree $populatedHash $populateResult @(0)

    $toolPublishHash = Get-TreeHash $ToolPublish
    $runTool = Join-Path $runDirectory 'tool'
    if (!(Test-Path -LiteralPath $runTool -PathType Container)) { $null = New-Item -ItemType Directory -Path $runTool }
    $null = Copy-Item -Path (Join-Path $ToolPublish '*') -Destination $runTool -Recurse -Force
    foreach ($helper in @('Collect-MigrationRoutes.mjs', 'Merge-MigrationRouteOracles.mjs')) {
        $null = Copy-Item -LiteralPath (Join-Path $repo "eng/$helper") -Destination (Join-Path $runDirectory $helper) -Force
    }

    $installCommands = @($Site.installCommands)
    for ($installIndex = 0; $installIndex -lt $installCommands.Count; $installIndex++) {
        $install = $installCommands[$installIndex]
        $lockHash = Get-TextHash (($source.lockFiles | ForEach-Object { "$($_.path)=$($_.sha256)" }) -join "`n")
        $cwd = if ($install.cwd) { "/source/$($install.cwd)" } else { '/source' }
        $installId = "dependency-restore-$($script:SiteRecord.stages.Count)"
        $null = Invoke-ContainerStage $script:SiteRecord $installId 'Restore the exact source lockfiles in an isolated container; no host profile or credentials are mounted.' 'bridge' $null $runDirectory $cwd $install.command $lockHash $null @(0) -SourceVolume $volumeName -TmpVolume $tmpVolumeName
        $dependencyStateHash = Get-VolumeDependencyHash $script:SiteRecord $volumeName "depstate-$installId"
        $lastStage = $script:SiteRecord.stages[$script:SiteRecord.stages.Count - 1]
        $lastStage.dependencyStateSha256 = $dependencyStateHash
        Save-SiteRecord $script:SiteRecord
    }

    foreach ($preBuild in @($Site['preBuildCommands'] | Where-Object { $_ })) {
        $preBuildId = "source-prebuild-$($script:SiteRecord.stages.Count)"
        $null = Invoke-ContainerStage $script:SiteRecord $preBuildId 'Run the declared source preparation command; only public fetches, no credentials.' 'bridge' $null $runDirectory '/source' $preBuild $buildTree $null @(0) -SourceVolume $volumeName -TmpVolume $tmpVolumeName
    }
    $prepOutputs = @($Site['preBuildOutputs'] | Where-Object { $_ })
    if ($prepOutputs.Count -gt 0) {
        $prepEntries = [Collections.Generic.List[string]]::new()
        foreach ($rel in $prepOutputs) {
            $relNorm = ([string]$rel).Replace('\', '/')
            $lineSh = "if [ -f '/source/$relNorm' ]; then echo `"$relNorm|`$(stat -c%s '/source/$relNorm')|`$(sha256sum '/source/$relNorm' | cut -d' ' -f1)`"; else echo `"$relNorm|MISSING`"; fi"
            $line = (Invoke-VolumeCommand $script:SiteRecord $volumeName $lineSh 'prepout-hash').Trim()
            if ($line.EndsWith('|MISSING')) { throw "Declared preparation output is missing in the isolated volume: $relNorm" }
            $null = $prepEntries.Add($line)
        }
        $prepSorted = @($prepEntries | Sort-Object)
        $null = $script:SiteRecord.stages.Add([ordered]@{
            id = 'source-prebuild-outputs'; description = 'Record hashes of declared preparation outputs (e.g. fetched supporter data); preparation output is unreviewed upstream data, not an equivalence claim.'
            status = 'passed'; command = 'hash declared preBuildOutputs in the isolated volume'; network = 'none'; exitCode = 0
            inputSha256 = $buildTree; outputSha256 = Get-TextHash (($prepSorted -join "`n") + "`n"); outputs = $prepSorted
        })
        Save-SiteRecord $script:SiteRecord
    }

    $routeOracleInputs = [Collections.Generic.List[object]]::new()
    foreach ($variant in $Site.buildVariants) {
        foreach ($command in $variant.commands) {
            $buildSubpath = ([string]$variant.buildOutput).Replace('\', '/')
            $id = "original-build-$($variant.locale)-$($script:SiteRecord.stages.Count)"
            $inputHash = Get-TextHash "sourceTree=$buildTree`nlocks=$(Get-VolumeDependencyHash $script:SiteRecord $volumeName "depstate-$id")`ncommand=$(ConvertTo-Json -InputObject $command -Compress)"
            $null = Invoke-ContainerStage $script:SiteRecord $id "Build the original site for locale '$($variant.locale)' with only public dependency fetches; no credentials are present." 'bridge' $null $runDirectory '/source' $command $inputHash $null @(0) -SourceVolume $volumeName -TmpVolume $tmpVolumeName
            $volumeBuildHash = Get-VolumeTreeHash $script:SiteRecord $volumeName $buildSubpath "build-tree-$id"
            $lastBuild = $script:SiteRecord.stages[$script:SiteRecord.stages.Count - 1]
            $lastBuild.outputSha256 = $volumeBuildHash
            Save-SiteRecord $script:SiteRecord
            $tracked = Get-GitValue $buildSource @('status', '--porcelain', '--untracked-files=no')
            if ($tracked.Length -gt 0) { throw "Original build modified tracked source files for '$($Site.id)': $tracked" }

            $localeToken = ([string]$variant.locale) -replace '[^A-Za-z0-9_-]', '_'
            $routeOracle = Join-Path $runDirectory "routes/$localeToken/route-oracle.json"
            $routeSourceMap = Join-Path $runDirectory "routes/$localeToken/route-source-map.json"
            $collectorConfigPath = Join-Path $runDirectory "routes/$localeToken/collector-config.json"
            Write-Json $collectorConfigPath ([ordered]@{
                siteId = $Site.id; repositoryRoot = '/source'; sitePath = $Site.targetPath; buildOutput = $variant.buildOutput
                siteUrl = $Site.siteUrl; basePath = $Site.basePath; sourceVersion = $Site.sourceVersion; buildLocale = $variant.locale
                defaultLocale = $Site.defaultLocale; locales = @($Site.locales); documentRoutePrefixes = @($Site.documentRoutePrefixes)
                blogRoutePrefixes = @($Site.blogRoutePrefixes); inputMappings = @($Site.inputMappings)
                routeOracleOutput = "/run/routes/$localeToken/route-oracle.json"; routeSourceMapOutput = "/run/routes/$localeToken/route-source-map.json"
            })
            $generatedSubpath = ([string]$Site.targetPath).Replace('\', '/') + '/.docusaurus'
            $routeInputHash = Get-TextHash "build=$volumeBuildHash`ngenerated=$(Get-VolumeTreeHash $script:SiteRecord $volumeName $generatedSubpath "generated-tree-$id")`ncollector=$(Get-Hash (Join-Path $runDirectory 'Collect-MigrationRoutes.mjs'))"
            $null = Invoke-ContainerStage $script:SiteRecord "collect-routes-$localeToken" "Collect route paths and static source metadata from original locale build '$($variant.locale)'." 'none' $null $runDirectory '/run' @('node', '/run/Collect-MigrationRoutes.mjs', "/run/routes/$localeToken/collector-config.json") $routeInputHash $routeOracle @(0) -SourceVolume $volumeName -TmpVolume $tmpVolumeName
            $lastCollect = $script:SiteRecord.stages[$script:SiteRecord.stages.Count - 1]
            $lastCollect.sourceMapSha256 = Get-Hash $routeSourceMap
            Save-SiteRecord $script:SiteRecord
            $null = $routeOracleInputs.Add([ordered]@{ locale = $variant.locale; routeOraclePath = "/run/routes/$localeToken/route-oracle.json"; sourceMapPath = "/run/routes/$localeToken/route-source-map.json" })
        }
    }

    $mergeConfigPath = Join-Path $runDirectory 'routes/merge-config.json'
    Write-Json $mergeConfigPath ([ordered]@{ siteId = $Site.id; sourceVersion = $Site.sourceVersion; basePath = $Site.basePath; inputs = @($routeOracleInputs); routeOracleOutput = '/run/routes/route-oracle.json'; routeSourceMapOutput = '/run/routes/route-source-map.json' })
    $mergedOracle = Join-Path $runDirectory 'routes/route-oracle.json'
    $mergeHash = Get-TextHash ($routeOracleInputs | ConvertTo-Json -Compress -Depth 8)
    $null = Invoke-ContainerStage $script:SiteRecord 'merge-route-oracles' 'Merge locale route snapshots and reject conflicting route metadata.' 'none' $null $runDirectory '/run' @('node', '/run/Merge-MigrationRouteOracles.mjs', '/run/routes/merge-config.json') $mergeHash $mergedOracle @(0) -TmpVolume $tmpVolumeName

    $migrationSource = Join-Path $runDirectory 'migration-source'
    $mappingManifest = Copy-ManifestInputs $Site $sourceRoot $migrationSource $runDirectory $script:SiteRecord
    $sourceHash = $mappingManifest.migrationSourceTreeSha256
    $converted = Join-Path $runDirectory 'converted'
    $migrationStdout = Join-Path $evidenceDirectory 'logs/migration-convert.stdout.txt'
    $migrationStderr = Join-Path $evidenceDirectory 'logs/migration-convert.stderr.txt'
    $toolPath = $script:ToolPath
    $migrationArgs = @($toolPath, 'migrate', 'docusaurus', $migrationSource, '--output', $converted, '--expected-routes', $mergedOracle, '--base-url', $script:CandidateBaseUrl, '--compare-normalized-pages')
    $migration = Invoke-LoggedProcess 'dotnet' $migrationArgs $repo $migrationStdout $migrationStderr 14400
    if ($migration.timedOut -or @(0,3) -notcontains [int]$migration.exitCode) { throw "LithoSharp migration CLI failed for '$($Site.id)' with exit $($migration.exitCode)." }
    $migrationReportPath = Join-Path $runDirectory 'migration-report.json'
    [IO.File]::WriteAllText($migrationReportPath, (Get-Content -LiteralPath $migrationStdout -Raw))
    $migrationJson = Get-Content -LiteralPath $migrationReportPath -Raw | ConvertFrom-Json -AsHashtable
    $conversionHash = Get-TreeHash $converted
    $null = $script:SiteRecord.stages.Add([ordered]@{
        id = 'migration-convert'; description = 'Analyze and convert the fixed mapped source with the V110-20 route oracle.'; status = 'passed'
        command = 'lithosharp migrate docusaurus <migration-source> --output <converted> --expected-routes <oracle> --compare-normalized-pages'
        network = 'none'; exitCode = $migration.exitCode; migrationExitCode = $migrationJson.exitCode; timedOut = $migration.timedOut; elapsedMs = $migration.elapsedMs
        inputSha256 = Get-TextHash "source=$sourceHash`noracle=$(Get-Hash $mergedOracle)"; outputSha256 = $conversionHash
        stdoutFile = 'migration-convert.stdout.txt'; stderrFile = 'migration-convert.stderr.txt'; stdoutSha256 = $migration.stdoutSha256; stderrSha256 = $migration.stderrSha256
    })
    Save-SiteRecord $script:SiteRecord

    $manualPatchReport = New-ManualPatchReport $migrationJson $Site
    $manualPatchPath = Join-Path $runDirectory 'manual-patches.json'
    Write-Json $manualPatchPath $manualPatchReport
    $null = $script:SiteRecord.stages.Add([ordered]@{ id = 'manual-patches'; description = 'Record every non-automatic action, source fingerprint, span and functional difference; no patch is silently accepted.'; status = 'passed'; command = 'write unapplied manual-action report'; network = 'none'; exitCode = 0; inputSha256 = Get-Hash $migrationReportPath; outputSha256 = Get-Hash $manualPatchPath })
    Save-SiteRecord $script:SiteRecord

    $siteTool = Join-Path $runDirectory 'tool'
    $worker = Join-Path $siteTool 'worker'
    if (!(Test-Path -LiteralPath (Join-Path $worker 'package-lock.json'))) { throw 'MigrationReplay output lacks the bundled MDX worker lock.' }
    $workerLockHash = Get-Hash (Join-Path $worker 'package-lock.json')
    $null = Invoke-ContainerStage $script:SiteRecord 'candidate-worker-restore' 'Restore first-party worker packages from npm lockfile with lifecycle scripts disabled and no credentials.' 'bridge' $null $runDirectory '/run/tool/worker' @('npm', 'ci', '--no-audit', '--no-fund', '--ignore-scripts') $workerLockHash $null @(0) -TmpVolume $tmpVolumeName
    $workerState = Get-DependencyStateHash $worker
    $lastWorker = $script:SiteRecord.stages[$script:SiteRecord.stages.Count - 1]
    $lastWorker.outputSha256 = $workerState
    Save-SiteRecord $script:SiteRecord

    $candidateConfig = [ordered]@{
        action = 'candidate-build'; siteId = $Site.id; sourceDirectory = '/run/migration-source'; routeOracle = '/run/routes/route-oracle.json'; routeSourceMap = '/run/routes/route-source-map.json'
        fullReportPath = '/run/reports/full-migration-report.json'; manualPatchReportPath = '/run/manual-patches.json'; convertedDirectory = '/run/converted'
        candidateSourceDirectory = '/run/candidate-source'; candidateOutputDirectory = '/run/candidate-output'; candidateReportPath = '/run/reports/candidate-build.json'
        serveReportPath = '/run/reports/serve-check.json'; baseUrl = $script:CandidateBaseUrl; defaultLocale = $Site.defaultLocale; workerDirectory = '/run/tool/worker'
        buildTimestamp = '2026-09-29T00:00:00+00:00'; candidatePageLimit = [int]$Site.candidatePageLimit
    }
    $candidateConfigPath = Join-Path $runDirectory 'candidate-build.json'
    Write-Json $candidateConfigPath $candidateConfig
    $candidateInputHash = Get-TextHash "conversion=$conversionHash`noracle=$(Get-Hash $mergedOracle)`nreplay=$(Get-Hash (Join-Path $siteTool 'MigrationReplay.dll'))`nworkerLock=$workerLockHash"
    $candidateBuild = Join-Path $runDirectory 'reports/candidate-build.json'
    $null = Invoke-ContainerStage $script:SiteRecord 'candidate-build' 'Build the bounded clean-page candidate offline; MDX and user modules remain inside Docker.' 'none' $null $runDirectory '/run' @('dotnet', '/run/tool/MigrationReplay.dll', '/run/candidate-build.json') $candidateInputHash $candidateBuild @(0) -TmpVolume $tmpVolumeName
    $candidateBuildJson = Get-Content -LiteralPath $candidateBuild -Raw | ConvertFrom-Json -AsHashtable
    $serveConfig = [ordered]@{}
    foreach ($entry in $candidateConfig.GetEnumerator()) { $serveConfig[$entry.Key] = $entry.Value }
    $serveConfig.action = 'serve-check'
    Write-Json (Join-Path $runDirectory 'candidate-serve.json') $serveConfig
    $serveReport = Join-Path $runDirectory 'reports/serve-check.json'
    $null = Invoke-ContainerStage $script:SiteRecord 'candidate-serve-check' 'Serve candidate pages over loopback and test navigation/assets/search offline.' 'none' $null $runDirectory '/run' @('dotnet', '/run/tool/MigrationReplay.dll', '/run/candidate-serve.json') (Get-Hash $candidateBuild) $serveReport @(0) -TmpVolume $tmpVolumeName
    $serveReportJson = Get-Content -LiteralPath $serveReport -Raw | ConvertFrom-Json -AsHashtable
    $passed = $candidateBuildJson.state -eq 'passed' -and $candidateBuildJson.candidatePageSet.status -eq 'match' -and $serveReportJson.operations.status -eq 'passed'
    $script:SiteRecord.state = if ($passed) { 'verified-scoped' } else { 'failed' }
    $script:SiteRecord.summary = [ordered]@{
        sourceVersion = $Site.version; sourceRouteCount = $migrationJson.routes.comparison.sourceRouteCount
        rawExactStatus = $migrationJson.routes.comparison.rawStatus; migrationExitCode = $migrationJson.exitCode
        candidatePageSetStatus = $candidateBuildJson.candidatePageSet.status; selectedDocumentPages = @($candidateBuildJson.selectedPages).Count
        candidateBuildDiagnostics = $candidateBuildJson.candidateBuild.diagnosticCount; candidateGeneratedRoutes = $candidateBuildJson.candidateBuild.generatedRouteCount
        serveStatus = $serveReportJson.operations.status; fullSiteEquivalenceClaimed = $false
    }
    $script:SiteRecord.reports = [ordered]@{
        routeOracle = 'routes/route-oracle.json'; routeSourceMap = 'routes/route-source-map.json'; migrationSourceManifest = 'migration-source-manifest.json'
        migrationReport = 'migration-report.json'; manualPatches = 'manual-patches.json'; candidateBuild = 'reports/candidate-build.json'; serveCheck = 'reports/serve-check.json'
    }
    Save-SiteRecord $script:SiteRecord
    if (!$passed) { throw "The declared candidate route/build/serve scope failed for '$($Site.id)'." }
    & docker volume rm $volumeName $tmpVolumeName *> $null
    $script:SiteRecord.sourceVolumeRemoved = ($LASTEXITCODE -eq 0)
    Save-SiteRecord $script:SiteRecord
    return $script:SiteRecord
}

function Run-Corpus([string[]] $SiteIds) {
    if (!$script:AllowThirdPartyBuild) { throw 'Use -AllowThirdPartyBuild only after selecting the approved isolated Docker execution path.' }
    if (!$script:WorkspaceRoot) { throw '-WorkspaceRoot must point outside the repository.' }
    if (!$script:RunId) { throw '-RunId is required so evidence is append-only.' }
    $workspace = [IO.Path]::GetFullPath($script:WorkspaceRoot)
    $repoRelative = [IO.Path]::GetRelativePath($repo, $workspace)
    if (![IO.Path]::IsPathRooted($repoRelative) -and $repoRelative -ne '..' -and !$repoRelative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal))
        { throw 'WorkspaceRoot must be outside the repository.' }
    if ($workspace.Contains(',')) { throw 'WorkspaceRoot cannot contain a comma because Docker bind-mount syntax uses commas.' }
    $null = New-Item -ItemType Directory -Force -Path $workspace
    $script:WorkspaceRoot = $workspace
    $evidenceRoot = Join-Path $repo (".local/verification/1.1.0/v110-21-{0}" -f $script:RunId)
    $null = New-Item -ItemType Directory -Force -Path $evidenceRoot
    $script:EvidenceRoot = $evidenceRoot
    if ([string]::IsNullOrWhiteSpace($script:ToolPath)) {
        $script:ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll'
    }
    $script:ToolPath = [IO.Path]::GetFullPath($script:ToolPath)
    if (!(Test-Path -LiteralPath $script:ToolPath -PathType Leaf)) {
        $toolBuild = Invoke-LoggedProcess 'dotnet' @('build', 'src/LithoSharp.Tool/LithoSharp.Tool.csproj', '--no-restore', '-c', 'Release', '-v', 'minimal') $repo `
            (Join-Path $evidenceRoot 'tool-build.stdout.txt') (Join-Path $evidenceRoot 'tool-build.stderr.txt') 1800
        if ($toolBuild.exitCode -ne 0 -or !(Test-Path -LiteralPath $script:ToolPath -PathType Leaf)) { throw 'The Release LithoSharp tool is required before running migration replay.' }
    }
    $existingImageId = (& docker image inspect --format '{{.Id}}' $manifest.container.image 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $existingImageId) {
        $dockerImageId = $existingImageId
    }
    else {
        $containerBuild = Invoke-LoggedProcess 'docker' @('build', '--pull=false', '--no-cache', '--file', (Join-Path $repo 'eng/migration-corpus/Containerfile'), '--tag', [string]$manifest.container.image, (Join-Path $repo 'eng/migration-corpus')) $repo `
            (Join-Path $evidenceRoot 'container-build.stdout.txt') (Join-Path $evidenceRoot 'container-build.stderr.txt') 1800
        if ($containerBuild.exitCode -ne 0) { throw 'Could not build the pinned no-credential migration container.' }
        $dockerImageId = (& docker image inspect --format '{{.Id}}' $manifest.container.image | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect migration runner image.' }
    }
    $imageRecord = [ordered]@{ image = $manifest.container.image; imageId = $dockerImageId; nodeVersion = $manifest.container.nodeVersion; dotnetSdkVersion = $manifest.container.dotnetSdkVersion }
    $script:ContainerImageId = $dockerImageId

    $toolPublish = Join-Path $workspace 'tools/MigrationReplay'
    if (Test-Path -LiteralPath $toolPublish) { Remove-Item -LiteralPath $toolPublish -Recurse -Force }
    $publish = Invoke-LoggedProcess 'dotnet' @('publish', 'eng/MigrationReplay/MigrationReplay.csproj', '-c', 'Release', '-o', $toolPublish) $repo `
        (Join-Path $evidenceRoot 'migration-replay-publish.stdout.txt') (Join-Path $evidenceRoot 'migration-replay-publish.stderr.txt') 3600
    if ($publish.exitCode -ne 0) { throw 'Publishing the first-party replay helper failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $toolPublish 'MigrationReplay.dll'))) { throw 'MigrationReplay publish output is incomplete.' }
    $containerRuntime = [ordered]@{ image = $imageRecord; migrationReplaySha256 = Get-TreeHash $toolPublish; buildExitCode = $publish.exitCode }

    $results = [Collections.Generic.List[object]]::new()
    foreach ($site in $manifest.sites) {
        if ($SiteIds -notcontains [string]$site.id) { continue }
        try { $null = $results.Add((Invoke-OneSite $site $toolPublish)) }
        catch {
            $failureMessage = [string]$_.Exception.Message
            $null = $results.Add([ordered]@{ siteId = $site.id; state = 'failed'; failure = $failureMessage })
            try {
                $failedReportPath = Join-Path $evidenceRoot ([string]$site.id + '/site-report.json')
                if (Test-Path -LiteralPath $failedReportPath -PathType Leaf) {
                    $failedRecord = Get-Content -LiteralPath $failedReportPath -Raw | ConvertFrom-Json -AsHashtable
                    $failedRecord.state = 'failed'
                    $failedRecord.failure = $failureMessage
                    Write-Json $failedReportPath $failedRecord
                }
            } catch { }
        }
    }
    $result = [ordered]@{
        schemaVersion = '1.0'; planTask = 'V110-21'; runId = $script:RunId; corpusId = $manifest.corpusId
        state = if (@($results | Where-Object { $_.state -ne 'verified-scoped' }).Count -eq 0) { 'verified-scoped' } else { 'failed-or-unverified' }
        fullSiteEquivalenceClaimed = $false; manifestSha256 = $manifestHash; containerRuntime = $containerRuntime; sites = @($results)
    }
    Write-Json (Join-Path $evidenceRoot 'result.json') $result
    Write-Host ("Migration corpus {0}: {1}; sites={2}" -f $script:RunId, $result.state, $results.Count)
    foreach ($siteResult in $results) { Write-Host ("  {0}: {1}" -f $siteResult.siteId, $siteResult.state) }
    if ($result.state -ne 'verified-scoped') { throw 'At least one of the pinned source/build/candidate/serve flows did not pass.' }
    return $result
}

$script:WorkspaceRoot = $WorkspaceRoot
$script:RunId = $RunId
$script:ToolPath = $ToolPath
$script:CandidateBaseUrl = $CandidateBaseUrl
$script:AllowThirdPartyBuild = [bool]$AllowThirdPartyBuild

if ($Action -eq 'validate') {
    $validation = Test-Manifest
    Write-Json (Join-Path $repo '.local/verification/1.1.0/migration-sites-validation.json') $validation
    Write-Host ("Manifest valid: {0} pinned sources; sha256={1}" -f $validation.sourceCount, $validation.manifestSha256)
    return
}

if ($Action -eq 'run-site') {
    if (!$SiteId) { throw '-SiteId is required for -Action run-site.' }
    $site = $manifest.sites | Where-Object { $_.id -eq $SiteId } | Select-Object -First 1
    if ($null -eq $site) { throw "Unknown migration corpus site '$SiteId'." }
    $null = Run-Corpus @($SiteId)
    return
}

$null = Run-Corpus @($manifest.sites | ForEach-Object { [string]$_.id })
