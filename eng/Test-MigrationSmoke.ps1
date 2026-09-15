[CmdletBinding()]
param(
    [string] $ToolPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ToolPath) { $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }

function Invoke-Migrate([string] $Name, [int] $ExpectedExit) {
    $source = Join-Path $repo "tests/fixtures/docusaurus/$Name/source"
    $expectedRoutes = Join-Path $repo "tests/fixtures/docusaurus/$Name/expected-routes.json"
    foreach ($path in @($source, $expectedRoutes)) {
        if (!(Test-Path -LiteralPath $path)) { throw "Missing migration smoke input: $path" }
    }
    $fixture = Join-Path $repo ('.tmp/migration-smoke-' + [Guid]::NewGuid().ToString('N'))
    $output = Join-Path $fixture 'converted'
    $null = New-Item -ItemType Directory -Path $fixture
    try {
        $json = & dotnet $ToolPath migrate docusaurus $source --output $output --expected-routes $expectedRoutes --base-url https://example.test/mig/ | Out-String
        if ($LASTEXITCODE -ne $ExpectedExit) { throw "Migration smoke on $Name exited $LASTEXITCODE, expected $ExpectedExit.`n$json" }
        $report = $json | ConvertFrom-Json
        if ([int] $report.exitCode -ne $ExpectedExit) { throw "Migration smoke on $Name reported exitCode $($report.exitCode), expected $ExpectedExit." }
        return @{ Json = $report; Output = $output; Fixture = $fixture }
    }
    catch {
        Write-Host "Migration smoke fixture kept at: $fixture"
        throw
    }
}

$clean = Invoke-Migrate 'minimal' 0
$converted = @(Get-ChildItem -LiteralPath $clean.Output -Recurse -File)
if ($converted.Count -eq 0) { throw 'Migration smoke converted no files.' }
$manifest = Join-Path $clean.Output 'migration-manifest.json'
if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Migration smoke produced no manifest.' }
Write-Host ("Migration smoke passed on minimal ({0} files)." -f $converted.Count)

$warned = Invoke-Migrate 'full-site' 3
$verdicts = @{}
foreach ($entry in $warned.Json.verdicts) { $verdicts[$entry.file] = $entry.verdict }
if ($verdicts['src/legacy/Old.jsx'] -ne 'Unsupported') { throw 'Migration smoke did not flag the unsupported import.' }
$issueIds = @($warned.Json.verdicts | ForEach-Object { $_.issues } | ForEach-Object { $_ } | ForEach-Object { $_.id })
if (!($issueIds -contains 'LSMIG003')) { throw 'Migration smoke omitted the unsupported-import diagnostic.' }
if (@($warned.Json.manifest.unsupported).Count -eq 0) { throw 'Migration smoke omitted manifest unsupported notes.' }
if (@($warned.Json.routes.missing).Count -ne 0 -or @($warned.Json.routes.extra).Count -ne 0) { throw 'Migration smoke reported unexpected route changes.' }
Write-Host 'Migration smoke passed on full-site (exit 3 with structured warnings).'
# The child command intentionally returned 3; the verification script succeeded.
$global:LASTEXITCODE = 0
