[CmdletBinding()]
param()
# Select the staged pair, props, matrix, parser stamp and lock files before running acceptance.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest = Join-Path $PSScriptRoot 'component-pair.json'
$pair = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path $repo "artifacts/markdown-pair-consumers/$runId"
$packageCache = Join-Path ([IO.Path]::GetTempPath()) "fp-$runId"
$null = New-Item -ItemType Directory -Path $evidence, $packageCache
$result = [ordered]@{ state = 'RUNNING'; componentVersion = $pair.componentVersion; parserVersion = $pair.parserVersion; startedAt = [DateTimeOffset]::UtcNow.ToString('O') }
$resultPath = Join-Path $evidence 'consumers.json'
[IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json))
$previousPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $packageCache
Push-Location $repo
try {
    & (Join-Path $repo 'eng/Test-VersionMatrix.ps1')
    $identity = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MarkdownYamlIdentity.cs') -Raw
    if (!$identity.Contains('ParserVersion = "' + $pair.parserVersion + '"')) { throw 'The expected parser stamp differs from the selected pair.' }
    & node (Join-Path $PSScriptRoot 'Verify-FixedPair.mjs') verify-fixed (Join-Path $PSScriptRoot 'feed')
    if ($LASTEXITCODE -ne 0) { throw 'Fixed pair publication/byte verification failed.' }
    foreach ($id in @('Syntamark', 'Syntamark.Source')) {
        & (Join-Path $repo 'eng/Validate-Package.ps1') -PackageDirectory (Join-Path $PSScriptRoot 'feed') -PackageId $id -ExpectedVersion $pair.componentVersion
    }
    & dotnet build src/LithoSharp.Generators/LithoSharp.Generators.csproj -c Release -p:RestoreLockedMode=true --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Selected Source did not build in the actual Generator.' }
    $generator = Join-Path $repo 'src/LithoSharp.Generators/bin/Release/netstandard2.0/LithoSharp.Generators.dll'
    # This existing canary is outside the solution and compares the actual Generator with the restored runtime.
    & dotnet run --project tests/LithoSharp.Markdown.Artifacts -c Release "-p:SyntamarkArtifactVersion=$($pair.componentVersion)" -p:RestorePackagesWithLockFile=false -- $manifest $generator (Join-Path $evidence 'artifacts.json')
    if ($LASTEXITCODE -ne 0) { throw 'Selected runtime/Source metadata, facts or expected-stamp guards differ.' }
    $loaded = Get-Content -LiteralPath (Join-Path $evidence 'artifacts.json') -Raw | ConvertFrom-Json
    if ($loaded.runtimeAssemblySha256 -cne $pair.artifacts.runtime.assemblySha256) { throw 'The consumer loaded a runtime DLL outside the selected archive.' }
    & dotnet run --project tests/LithoSharp.Markdown.Portability -c Release -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Selected runtime/portable/Generator consumers differ.' }
    $result.state = 'PASS'
} catch {
    $result.state = 'FAIL'
    $result['error'] = $_.Exception.Message
    throw
} finally {
    Pop-Location
    $env:NUGET_PACKAGES = $previousPackages
    $result['completedAt'] = [DateTimeOffset]::UtcNow.ToString('O')
    [IO.File]::WriteAllText($resultPath, ($result | ConvertTo-Json))
}
Write-Host "Actual fixed-pair consumers passed for $($pair.componentVersion). Packaged compiler/template acceptance remains a separate release check."
