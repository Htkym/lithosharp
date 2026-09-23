<#
.SYNOPSIS
Records reproducible verification evidence for the LithoSharp 1.1.0 plan (V110-*).

.DESCRIPTION
The runner never normalizes an expectation. It has no switch that rewrites an
expected file, and it does not decide success from the last line of a log: every
step records its own command array, working directory, exit code, timeout state,
duration, stdout/stderr files and hashes.

Actions:
  run       Execute the steps of a command file and write checks.json/result.json
            under <EvidenceRoot>/<RunId>/.
  manifest  Hash a directory tree into a manifest (path, bytes, sha256) so that
            another run can compare byte-level output.
  compare   Compare an expected manifest with an actual manifest per category
            (files, routes, diagnostics, dom, exitCode) and fail on differences.
  self-test Verify that compare detects a one-byte output change, a missing
            route, a shifted diagnostic position, a changed dom entry and a
            changed exit code, and that an identical comparison neither fails
            nor rewrites the expected file.

.EXAMPLE
& ./eng/Invoke-V110Verification.ps1 -Action run -RunId v110-01 -CommandFile eng/verification/1.1.0/commands/v110-01-baseline.json

.EXAMPLE
& ./eng/Invoke-V110Verification.ps1 -Action self-test -RunId v110-01-self-test
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('run', 'manifest', 'compare', 'self-test')] [string] $Action,

    [string] $RunId,
    [string] $CommandFile,
    [string] $EvidenceRoot,
    [string] $Root,
    [string] $Output,
    [string] $Expected,
    [string] $Actual,
    [string[]] $ExcludePatterns,
    [int] $DefaultTimeoutSeconds = 3600,
    [switch] $PassThru
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path $repo '.local/verification/1.1.0' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)

# Derived or machine-specific trees are excluded from manifests by default. The
# exclusions are recorded with the manifest so a comparison never hides a
# difference by silently dropping a path.
$defaultExclusionPattern = '(^|/)(node_modules|bin|obj|\.git|\.vs|\.lithosharp|TestResults|artifacts|\.tmp)(/|$)'
$defaultExclusionReason = 'derived build output, restored dependencies or per-machine state; not part of the compared artifact'

function Get-Sha256([string] $Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Cannot hash missing file: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TextSha256([string] $Text) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}

function Get-ToolVersion([string] $Exe, [string[]] $Arguments) {
    try {
        $value = (& $Exe @Arguments 2>$null | Select-Object -First 1)
        if ($null -ne $value) { return ([string]$value).Trim() }
    }
    catch { }
    return $null
}

function Get-MachineEnvironment {
    return [ordered]@{
        os             = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture   = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        dotnetSdk      = Get-ToolVersion 'dotnet' @('--version')
        node           = Get-ToolVersion 'node' @('--version')
        powershell     = $PSVersionTable.PSVersion.ToString()
    }
}

function Get-RepoState {
    Push-Location $repo
    try {
        $commit = (& git rev-parse HEAD).Trim()
        $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
        $status = (& git status --porcelain=v1 --untracked-files=normal) -join "`n"
        $diff = (& git diff HEAD) -join "`n"
    }
    finally { Pop-Location }
    return [ordered]@{
        commit            = $commit
        branch            = $branch
        clean             = ($status.Length -eq 0)
        statusSha256      = (Get-TextSha256 $status)
        diffSha256        = (Get-TextSha256 $diff)
        dirtyDiffHash     = (Get-TextSha256 ($status + "`n---diff---`n" + $diff))
    }
}

function Get-TreeHash([string] $Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full -PathType Leaf) { return (Get-Sha256 $full) }
    if (!(Test-Path -LiteralPath $full -PathType Container)) { throw "Cannot hash missing path: $full" }
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $full -Recurse -File -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($full, $file.FullName).Replace('\', '/')
        if ($relative -match $defaultExclusionPattern) { continue }
        $lines.Add("$relative=$(Get-Sha256 $file.FullName)")
    }
    return (Get-TextSha256 (($lines -join "`n") + "`n"))
}

function New-TreeManifest([string] $Path, [string[]] $Exclude) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!(Test-Path -LiteralPath $full -PathType Container)) { throw "Manifest root must be a directory: $full" }
    $files = [Collections.Generic.List[object]]::new()
    $excluded = [Collections.Generic.List[string]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $full -Recurse -File -Force | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($full, $file.FullName).Replace('\', '/')
        $isExcluded = $relative -match $defaultExclusionPattern
        if (!$isExcluded -and $Exclude) {
            foreach ($pattern in $Exclude) { if ($relative -match $pattern) { $isExcluded = $true; break } }
        }
        if ($isExcluded) { $excluded.Add($relative); continue }
        $files.Add([ordered]@{ path = $relative; bytes = $file.Length; sha256 = (Get-Sha256 $file.FullName) })
    }
    return [ordered]@{
        schemaVersion     = '1.0'
        root              = $full
        fileCount         = $files.Count
        files             = @($files)
        excludedCount     = $excluded.Count
        excluded          = @($excluded | Select-Object -First 50)
        exclusionPatterns = @($defaultExclusionPattern) + @($Exclude)
        exclusionReason   = $defaultExclusionReason
    }
}

function Get-ManifestProperty($Manifest, [string] $Name, $Default) {
    if ($null -eq $Manifest) { return $Default }
    if ($Manifest -is [Collections.IDictionary]) {
        if ($Manifest.Contains($Name)) { return $Manifest[$Name] }
        return $Default
    }
    $property = $Manifest.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function ConvertTo-CategoryKeys($Items) {
    $keys = [Collections.Generic.List[string]]::new()
    foreach ($item in @($Items)) {
        if ($null -eq $item) { continue }
        if ($item -is [string]) { $keys.Add($item); continue }
        $parts = [Collections.Generic.List[string]]::new()
        foreach ($name in 'id', 'severity', 'line', 'column', 'message') {
            $value = Get-ManifestProperty $item $name $null
            if ($null -ne $value) { $parts.Add("$name=$value") }
        }
        $keys.Add(($parts -join '|'))
    }
    $keys.Sort()
    return $keys
}

function Compare-Manifest($ExpectedManifest, $ActualManifest) {
    $differences = [Collections.Generic.List[object]]::new()

    $expectedFiles = @{}
    foreach ($file in @(Get-ManifestProperty $ExpectedManifest 'files' @())) { $expectedFiles[[string](Get-ManifestProperty $file 'path' '')] = [string](Get-ManifestProperty $file 'sha256' '') }
    $actualFiles = @{}
    foreach ($file in @(Get-ManifestProperty $ActualManifest 'files' @())) { $actualFiles[[string](Get-ManifestProperty $file 'path' '')] = [string](Get-ManifestProperty $file 'sha256' '') }
    foreach ($path in ($expectedFiles.Keys | Sort-Object)) {
        if (!$actualFiles.ContainsKey($path)) { $differences.Add([ordered]@{ category = 'files'; kind = 'missing'; path = $path }) }
        elseif ($expectedFiles[$path] -ne $actualFiles[$path]) {
            $differences.Add([ordered]@{ category = 'files'; kind = 'changed'; path = $path; expected = $expectedFiles[$path]; actual = $actualFiles[$path] })
        }
    }
    foreach ($path in ($actualFiles.Keys | Sort-Object)) {
        if (!$expectedFiles.ContainsKey($path)) { $differences.Add([ordered]@{ category = 'files'; kind = 'extra'; path = $path }) }
    }

    foreach ($category in 'routes', 'diagnostics', 'dom') {
        $expectedKeys = ConvertTo-CategoryKeys (Get-ManifestProperty $ExpectedManifest $category @())
        $actualKeys = ConvertTo-CategoryKeys (Get-ManifestProperty $ActualManifest $category @())
        foreach ($key in $expectedKeys) {
            if ($actualKeys -notcontains $key) { $differences.Add([ordered]@{ category = $category; kind = 'missing'; value = $key }) }
        }
        foreach ($key in $actualKeys) {
            if ($expectedKeys -notcontains $key) { $differences.Add([ordered]@{ category = $category; kind = 'extra'; value = $key }) }
        }
    }

    $expectedExit = Get-ManifestProperty $ExpectedManifest 'exitCode' $null
    $actualExit = Get-ManifestProperty $ActualManifest 'exitCode' $null
    if ($null -ne $expectedExit -or $null -ne $actualExit) {
        if ($expectedExit -ne $actualExit) {
            $differences.Add([ordered]@{ category = 'exitCode'; kind = 'changed'; expected = $expectedExit; actual = $actualExit })
        }
    }

    $byCategory = [ordered]@{}
    foreach ($category in 'files', 'routes', 'diagnostics', 'dom', 'exitCode') {
        $byCategory[$category] = @($differences | Where-Object { $_.category -eq $category }).Count
    }
    return [ordered]@{
        identical       = ($differences.Count -eq 0)
        differenceCount = $differences.Count
        byCategory      = $byCategory
        differences     = @($differences | Select-Object -First 100)
    }
}

function Write-CompareReport($Report) {
    Write-Host ("compare: identical={0} differences={1}" -f $Report.identical, $Report.differenceCount)
    foreach ($category in $Report.byCategory.Keys) {
        if ($Report.byCategory[$category] -gt 0) { Write-Host ("  {0}: {1}" -f $category, $Report.byCategory[$category]) }
    }
    foreach ($difference in $Report.differences) {
        Write-Host ("  [{0}] {1} {2}" -f $difference.category, $difference.kind, ($difference | ConvertTo-Json -Compress))
    }
}

function Invoke-Step($Step, [string] $RunDirectory, [int] $DefaultTimeout) {
    $id = [string](Get-ManifestProperty $Step 'id' '')
    if (!$id) { throw 'A command-file step requires an id.' }
    $command = @(Get-ManifestProperty $Step 'command' @())
    if ($command.Count -eq 0) { throw "Step '$id' requires a command array." }
    $stepCwd = [string](Get-ManifestProperty $Step 'cwd' '.')
    if (![IO.Path]::IsPathRooted($stepCwd)) { $stepCwd = [IO.Path]::GetFullPath((Join-Path $repo $stepCwd)) }
    if (!(Test-Path -LiteralPath $stepCwd -PathType Container)) { throw "Step '$id' working directory does not exist: $stepCwd" }
    $timeout = [int](Get-ManifestProperty $Step 'timeoutSeconds' $DefaultTimeout)
    $allowed = @(Get-ManifestProperty $Step 'allowedExitCodes' @(0)) | ForEach-Object { [int]$_ }
    $logDirectory = Join-Path $RunDirectory 'logs'
    $null = New-Item -ItemType Directory -Force -Path $logDirectory

    $startInfo = [Diagnostics.ProcessStartInfo]::new([string]$command[0])
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    for ($index = 1; $index -lt $command.Count; $index++) { $startInfo.ArgumentList.Add([string]$command[$index]) }
    $stepEnvironment = Get-ManifestProperty $Step 'env' $null
    $envOverrides = [Collections.Generic.List[string]]::new()
    if ($null -ne $stepEnvironment) {
        foreach ($property in $stepEnvironment.PSObject.Properties) {
            $startInfo.Environment[[string]$property.Name] = [string]$property.Value
            $envOverrides.Add([string]$property.Name)
        }
    }
    $envFingerprint = [ordered]@{
        pathSha256     = Get-TextSha256 ([string]$startInfo.Environment['PATH'])
        dotnetRoot     = [string]$startInfo.Environment['DOTNET_ROOT']
        nodeOptions    = [string]$startInfo.Environment['NODE_OPTIONS']
        variableCount  = $startInfo.Environment.Count
        overrides      = @($envOverrides)
    }

    $startedAt = (Get-Date).ToUniversalTime().ToString('o')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $status = 'passed'
    $exitCode = $null
    try {
        $finished = $process.WaitForExit($timeout * 1000)
        if (!$finished) {
            $status = 'timeout'
            $process.Kill($true)
            $process.WaitForExit()
        }
        else { $exitCode = $process.ExitCode }
    }
    finally {
        $clock.Stop()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $stdoutFile = Join-Path $logDirectory "$id.stdout.txt"
        $stderrFile = Join-Path $logDirectory "$id.stderr.txt"
        [IO.File]::WriteAllText($stdoutFile, $stdout)
        [IO.File]::WriteAllText($stderrFile, $stderr)
        $process.Dispose()
    }
    if ($status -eq 'passed' -and $allowed -notcontains $exitCode) { $status = 'failed' }

    $fixtures = [Collections.Generic.List[object]]::new()
    foreach ($fixture in @(Get-ManifestProperty $Step 'fixtures' @())) {
        $path = [string]$fixture
        $full = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repo $path }
        $fixtures.Add([ordered]@{ path = $path; treeSha256 = (Get-TreeHash $full) })
    }
    $outputs = [Collections.Generic.List[object]]::new()
    foreach ($output in @(Get-ManifestProperty $Step 'outputs' @())) {
        $path = [string]$output
        $full = if ([IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repo $path }
        $present = Test-Path -LiteralPath $full
        $outputs.Add([ordered]@{
            path       = $path
            present    = $present
            treeSha256 = if ($present) { Get-TreeHash $full } else { $null }
        })
    }

    return [ordered]@{
        id               = $id
        status           = $status
        command          = @($command)
        cwd              = $stepCwd
        allowedExitCodes = @($allowed)
        exitCode         = $exitCode
        timeoutSeconds   = $timeout
        startedAt        = $startedAt
        finishedAt       = (Get-Date).ToUniversalTime().ToString('o')
        elapsedMs        = $clock.ElapsedMilliseconds
        stdoutFile       = $stdoutFile
        stderrFile       = $stderrFile
        stdoutSha256     = Get-Sha256 $stdoutFile
        stderrSha256     = Get-Sha256 $stderrFile
        fixtures         = @($fixtures)
        outputs          = @($outputs)
        envFingerprint   = $envFingerprint
        notes            = [string](Get-ManifestProperty $Step 'notes' '')
    }
}

function Invoke-Run([string] $RunId, [string] $CommandFile) {
    if (!$RunId) { throw '-RunId is required for -Action run.' }
    if (!$CommandFile) { throw '-CommandFile is required for -Action run.' }
    if (![IO.Path]::IsPathRooted($CommandFile)) { $CommandFile = Join-Path $repo $CommandFile }
    if (!(Test-Path -LiteralPath $CommandFile -PathType Leaf)) { throw "Command file not found: $CommandFile" }
    $definition = Get-Content -LiteralPath $CommandFile -Raw | ConvertFrom-Json
    $runDirectory = Join-Path $EvidenceRoot $RunId
    $null = New-Item -ItemType Directory -Force -Path $runDirectory

    $steps = [Collections.Generic.List[object]]::new()
    $runStarted = (Get-Date).ToUniversalTime().ToString('o')
    foreach ($step in @($definition.steps)) { $steps.Add((Invoke-Step $step $runDirectory $DefaultTimeoutSeconds)) }
    $runFinished = (Get-Date).ToUniversalTime().ToString('o')

    $checks = [ordered]@{
        schemaVersion = '1.0'
        action        = 'run'
        taskId        = [string]$definition.taskId
        runId         = $RunId
        commandFile   = [IO.Path]::GetRelativePath($repo, $CommandFile).Replace('\', '/')
        commandFileSha256 = Get-Sha256 $CommandFile
        repoState     = Get-RepoState
        environment   = Get-MachineEnvironment
        evidenceDirectory = [IO.Path]::GetRelativePath($repo, $runDirectory).Replace('\', '/')
        startedAt     = $runStarted
        finishedAt    = $runFinished
        steps         = @($steps)
    }
    $failed = @($steps | Where-Object { $_.status -ne 'passed' })
    $result = [ordered]@{
        schemaVersion = '1.0'
        planVersion   = '1.1.0'
        taskId        = [string]$definition.taskId
        runId         = $RunId
        state         = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
        failedSteps   = @($failed | ForEach-Object { $_.id })
        checksFile    = Join-Path $runDirectory 'checks.json'
    }

    [IO.File]::WriteAllText((Join-Path $runDirectory 'checks.json'), ($checks | ConvertTo-Json -Depth 12))
    [IO.File]::WriteAllText((Join-Path $runDirectory 'result.json'), ($result | ConvertTo-Json -Depth 6))
    Write-Host ("run {0}: {1} ({2} steps, {3} failed)" -f $RunId, $result.state, $steps.Count, $failed.Count)
    foreach ($step in $steps) {
        Write-Host ("  {0}: {1} exit={2} {3}ms" -f $step.id, $step.status, $step.exitCode, $step.elapsedMs)
    }
    if ($PassThru) { return $checks }
    if ($failed.Count -gt 0) { exit 1 }
}

function Invoke-Manifest([string] $Root, [string] $Output) {
    if (!$Root) { throw '-Root is required for -Action manifest.' }
    if (!$Output) { throw '-Output is required for -Action manifest.' }
    $manifest = New-TreeManifest $Root $ExcludePatterns
    if (![IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $repo $Output }
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Output))
    [IO.File]::WriteAllText($Output, ($manifest | ConvertTo-Json -Depth 8))
    Write-Host ("manifest: {0} files ({1} excluded) -> {2}" -f $manifest.fileCount, $manifest.excludedCount, $Output)
    if ($PassThru) { return $manifest }
}

function Invoke-Compare([string] $Expected, [string] $Actual, [string] $Output) {
    if (!$Expected -or !$Actual) { throw '-Expected and -Actual are required for -Action compare.' }
    foreach ($path in $Expected, $Actual) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Manifest not found: $path" } }
    $expectedHashBefore = Get-Sha256 $Expected
    $report = Compare-Manifest (Get-Content -LiteralPath $Expected -Raw | ConvertFrom-Json) (Get-Content -LiteralPath $Actual -Raw | ConvertFrom-Json)
    $report.expected = [IO.Path]::GetFullPath($Expected)
    $report.actual = [IO.Path]::GetFullPath($Actual)
    $report.expectedSha256Before = $expectedHashBefore
    $report.expectedSha256After = Get-Sha256 $Expected
    Write-CompareReport $report
    if ($report.expectedSha256Before -ne $report.expectedSha256After) { throw 'The compare action modified the expected manifest.' }
    if ($Output) {
        if (![IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $repo $Output }
        $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Output))
        [IO.File]::WriteAllText($Output, ($report | ConvertTo-Json -Depth 8))
    }
    if ($PassThru) { return $report }
    if (!$report.identical) { exit 1 }
}

function Invoke-SelfTest([string] $RunId) {
    if (!$RunId) { $RunId = 'self-test-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') }
    $root = Join-Path $EvidenceRoot $RunId
    $case = Join-Path $root 'case'
    if (Test-Path -LiteralPath $case) { Remove-Item -LiteralPath $case -Recurse -Force }
    $null = New-Item -ItemType Directory -Force -Path (Join-Path $case 'expected/docs'), (Join-Path $case 'actual/docs')
    [IO.File]::WriteAllText((Join-Path $case 'expected/index.html'), "<html><body>alpha</body></html>`n")
    [IO.File]::WriteAllText((Join-Path $case 'expected/docs/a.html'), "<html><body>docs</body></html>`n")
    [IO.File]::WriteAllText((Join-Path $case 'actual/index.html'), "<html><body>alpha</body></html>`n")
    [IO.File]::WriteAllText((Join-Path $case 'actual/docs/a.html'), "<html><body>docs</body></html>`n")

    $expectedManifest = New-TreeManifest (Join-Path $case 'expected')
    $actualManifest = New-TreeManifest (Join-Path $case 'actual')
    $expectedManifest.routes = @('/', '/docs/')
    $actualManifest.routes = @('/', '/docs/')
    $expectedManifest.diagnostics = @([ordered]@{ id = 'LIT001'; severity = 'warning'; line = 3; column = 5 })
    $actualManifest.diagnostics = @([ordered]@{ id = 'LIT001'; severity = 'warning'; line = 3; column = 5 })
    $expectedManifest.dom = @('main>h1=Title', 'nav>a=Docs')
    $actualManifest.dom = @('main>h1=Title', 'nav>a=Docs')
    $expectedManifest.exitCode = 0
    $actualManifest.exitCode = 0

    function Save-Case([string] $Name, $ExpectedValue, $ActualValue) {
        $caseDirectory = Join-Path $root $Name
        $null = New-Item -ItemType Directory -Force -Path $caseDirectory
        $expectedPath = Join-Path $caseDirectory 'expected.json'
        $actualPath = Join-Path $caseDirectory 'actual.json'
        [IO.File]::WriteAllText($expectedPath, ($ExpectedValue | ConvertTo-Json -Depth 8))
        [IO.File]::WriteAllText($actualPath, ($ActualValue | ConvertTo-Json -Depth 8))
        return @{ name = $Name; expected = $expectedPath; actual = $actualPath }
    }

    $cases = [Collections.Generic.List[object]]::new()

    $actualManifest.files = @($actualManifest.files | ForEach-Object {
        if ($_.path -eq 'index.html') { [ordered]@{ path = $_.path; bytes = $_.bytes; sha256 = ('0' * 64) } } else { $_ }
    })
    $cases.Add((Save-Case 'output-one-byte' $expectedManifest $actualManifest))
    $actualManifest.files = @($expectedManifest.files | ForEach-Object { $_ })

    $actualManifest.routes = @('/')
    $cases.Add((Save-Case 'route-missing' $expectedManifest $actualManifest))
    $actualManifest.routes = @('/', '/docs/')

    $actualManifest.diagnostics = @([ordered]@{ id = 'LIT001'; severity = 'warning'; line = 3; column = 6 })
    $cases.Add((Save-Case 'diagnostic-position' $expectedManifest $actualManifest))
    $actualManifest.diagnostics = @([ordered]@{ id = 'LIT001'; severity = 'warning'; line = 3; column = 5 })

    $actualManifest.dom = @('main>h1=Title', 'nav>a=Documentation')
    $cases.Add((Save-Case 'dom-change' $expectedManifest $actualManifest))
    $actualManifest.dom = @('main>h1=Title', 'nav>a=Docs')

    $actualManifest.exitCode = 1
    $cases.Add((Save-Case 'exit-code' $expectedManifest $actualManifest))
    $actualManifest.exitCode = 0

    $identical = Save-Case 'identical' $expectedManifest $actualManifest

    $selfTest = [ordered]@{ schemaVersion = '1.0'; action = 'self-test'; runId = $RunId; cases = @() }
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($item in $cases) {
        $expectedJson = Get-Content -LiteralPath $item.expected -Raw | ConvertFrom-Json
        $actualJson = Get-Content -LiteralPath $item.actual -Raw | ConvertFrom-Json
        $report = Compare-Manifest $expectedJson $actualJson
        $detected = !$report.identical
        $selfTest.cases += [ordered]@{ name = $item.name; expectedVerdict = 'different'; detected = $detected; differenceCount = $report.differenceCount; byCategory = $report.byCategory }
        Write-Host ("self-test {0}: detected={1} differences={2}" -f $item.name, $detected, $report.differenceCount)
        if (!$detected) { $failures.Add("$($item.name) was not detected") }
    }
    $identicalReport = Compare-Manifest (Get-Content -LiteralPath $identical.expected -Raw | ConvertFrom-Json) (Get-Content -LiteralPath $identical.actual -Raw | ConvertFrom-Json)
    Write-Host ("self-test identical: identical={0} (expected true)" -f $identicalReport.identical)
    if (!$identicalReport.identical) { $failures.Add('identical manifests were reported as different') }

    # Determinism of the manifest itself: the same tree must hash to the same value twice.
    $firstHash = Get-TreeHash (Join-Path $case 'expected')
    $secondHash = Get-TreeHash (Join-Path $case 'expected')
    $selfTest.manifestStable = ($firstHash -eq $secondHash)
    Write-Host ("self-test manifest determinism: stable={0}" -f $selfTest.manifestStable)
    if (!$selfTest.manifestStable) { $failures.Add('tree hash is not stable across two runs') }

    # The runner must not expose a way to rewrite the expectation.
    $forbidden = @((Get-Command $PSCommandPath).Parameters.Keys | Where-Object { $_ -match '(?i)update|accept|approve|overwrite' })
    $selfTest.expectedRewriteParameters = @($forbidden)
    Write-Host ("self-test expected-rewrite parameters: {0}" -f ($(if ($forbidden.Count) { $forbidden -join ',' } else { 'none' })))
    if ($forbidden.Count -gt 0) { $failures.Add("unexpected expectation-rewriting parameter: $($forbidden -join ',')") }

    $selfTest.state = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
    $selfTest.failures = @($failures)
    [IO.File]::WriteAllText((Join-Path $root 'self-test.json'), ($selfTest | ConvertTo-Json -Depth 8))
    Write-Host ("self-test {0}: {1} ({2} cases)" -f $RunId, $selfTest.state, $selfTest.cases.Count)
    if ($PassThru) { return $selfTest }
    if ($failures.Count -gt 0) { exit 1 }
}

switch ($Action) {
    'run' { Invoke-Run $RunId $CommandFile }
    'manifest' { Invoke-Manifest $Root $Output }
    'compare' { Invoke-Compare $Expected $Actual $Output }
    'self-test' { Invoke-SelfTest $RunId }
}
