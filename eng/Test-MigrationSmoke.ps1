[CmdletBinding()]
param(
    [string] $ToolPath,
    [string] $FixtureName = 'minimal'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ToolPath) { $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }
$source = Join-Path $repo "tests/fixtures/docusaurus/$FixtureName/source"
$expectedRoutes = Join-Path $repo "tests/fixtures/docusaurus/$FixtureName/expected-routes.json"
foreach ($path in @($source, $expectedRoutes)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing migration smoke input: $path" }
}
$fixture = Join-Path $repo ('.tmp/migration-smoke-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $fixture 'converted'
$null = New-Item -ItemType Directory -Path $fixture
try {
    & dotnet $ToolPath migrate docusaurus $source --output $output --expected-routes $expectedRoutes --base-url https://example.test/mig/
    if ($LASTEXITCODE -ne 0) { throw "Migration smoke failed with exit code $LASTEXITCODE." }
    $manifest = Join-Path $output 'migration-manifest.json'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Migration smoke produced no manifest.' }
    $converted = @(Get-ChildItem -LiteralPath $output -Recurse -File)
    if ($converted.Count -eq 0) { throw 'Migration smoke converted no files.' }
    Write-Host ("Migration smoke passed on $FixtureName ($($converted.Count) files). Fixture: $fixture")
}
catch {
    Write-Host "Migration smoke fixture kept at: $fixture"
    throw
}
