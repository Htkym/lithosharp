$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$out = Join-Path $env:RUNNER_TEMP 'control-format-output'
$baseline = Join-Path $env:RUNNER_TEMP 'control-format-baseline'
New-Item -ItemType Directory -Force $out, $baseline | Out-Null
$tracked = @(git ls-files -- '*.cs')
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'Unable to enumerate tracked C# sources' }
foreach ($file in $tracked) {
    $destination = Join-Path $baseline $file
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item $file $destination
}
$verifier = Join-Path $env:RUNNER_TEMP 'control-format-verifier'
Copy-Item 'scripts/FormatTokenCheck' $verifier -Recurse
$verifierProject = Join-Path $verifier 'FormatTokenCheck.csproj'
dotnet build $verifierProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Token verifier build failed' }
$verifierDll = Join-Path $verifier 'bin/Release/net10.0/FormatTokenCheck.dll'
function Check-Tokens([string] $name) {
    dotnet $verifierDll $baseline $root (Join-Path $out "$name.json")
    if ($LASTEXITCODE -ne 0) { throw "$name token check failed" }
}
[xml]$props = Get-Content Directory.Build.props -Raw
$env:SyntamarkArtifactVersion = [string]$props.Project.PropertyGroup.SyntamarkComponentVersion
$projects = @(
    'benchmarks/LithoSharp.MdxPerformance/LithoSharp.MdxPerformance.csproj',
    'benchmarks/LithoSharp.Performance/LithoSharp.Performance.csproj',
    'eng/MigrationReplay/MigrationReplay.csproj',
    'samples/LithoSharp.DocsSample/LithoSharp.DocsSample.csproj',
    'samples/LithoSharp.MdxSample/LithoSharp.MdxSample.csproj',
    'samples/LithoSharp.Sample/LithoSharp.Sample.csproj',
    'src/LithoSharp.Analyzers/LithoSharp.Analyzers.csproj',
    'src/LithoSharp.CodeFixes/LithoSharp.CodeFixes.csproj',
    'src/LithoSharp.Generators/LithoSharp.Generators.csproj',
    'src/LithoSharp.Images/LithoSharp.Images.csproj',
    'src/LithoSharp.LanguageServer/LithoSharp.LanguageServer.csproj',
    'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj',
    'src/LithoSharp.Testing/LithoSharp.Testing.csproj',
    'src/LithoSharp.Tool/LithoSharp.Tool.csproj',
    'src/LithoSharp/LithoSharp.csproj',
    'tests/LithoSharp.Analyzers.Tests/LithoSharp.Analyzers.Tests.csproj',
    'tests/LithoSharp.CodeFixes.Tests/LithoSharp.CodeFixes.Tests.csproj',
    'tests/LithoSharp.Html.Portability/LithoSharp.Html.Portability.csproj',
    'tests/LithoSharp.LanguageServer.Tests/LithoSharp.LanguageServer.Tests.csproj',
    'tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj',
    'tests/LithoSharp.Markdown.Artifacts/LithoSharp.Markdown.Artifacts.csproj',
    'tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj',
    'tests/LithoSharp.Tests/LithoSharp.Tests.csproj',
    'tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj'
)
$excluded = @('src/LithoSharp/HtmlParsing/HtmlCharacterReferences.g.cs', 'tests/fixtures/', 'tests/LithoSharp.Tests/Fixtures/', 'templates/')
foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "Missing project: $project" }
    $lock = Join-Path (Split-Path $project) 'packages.lock.json'
    if (Test-Path $lock) {
        dotnet restore $project --locked-mode
    } else {
        dotnet restore $project -p:RestorePackagesWithLockFile=false
    }
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
$pilot = 'src/LithoSharp/Quality/SrcsetUrlScanner.cs'
$pilotBefore = (Get-FileHash $pilot -Algorithm SHA256).Hash
dotnet format style 'src/LithoSharp/LithoSharp.csproj' --diagnostics IDE2001 --include $pilot --no-restore --verbosity normal
if ($LASTEXITCODE -ne 0) { throw 'IDE2001 pilot failed' }
if ($pilotBefore -eq (Get-FileHash $pilot -Algorithm SHA256).Hash) { throw 'Pilot did not change: IDE2001 effectiveness not established' }
Check-Tokens 'pilot-token-check'
foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "Missing solution project: $project" }
}
foreach ($project in $projects) {
    dotnet format style $project --diagnostics IDE2001 --include . --exclude @excluded --no-restore --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "IDE2001 formatting failed: $project" }
}
Check-Tokens 'all-source-token-check'
foreach ($project in $projects) {
    dotnet format style $project --diagnostics IDE2001 --include . --exclude @excluded --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "IDE2001 verification failed: $project" }
}
$changed = @(git diff --name-only)
if ($LASTEXITCODE -ne 0) { throw 'git diff failed' }
if (@($changed | Where-Object { $_ -notmatch '\.cs$' }).Count -gt 0) { throw 'Unexpected changed paths' }
foreach ($file in $changed) {
    foreach ($excludedPath in $excluded) {
        if ($file -eq $excludedPath -or $file.StartsWith($excludedPath, [StringComparison]::Ordinal)) { throw "Excluded source changed: $file" }
    }
}
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }
git diff --binary --output="$out/control-format.patch" -- '*.cs'
if ($LASTEXITCODE -ne 0) { throw 'Patch generation failed' }
[ordered]@{ sourceCommit = $env:GITHUB_SHA; sdk = (dotnet --version); completed = $true; changedFiles = $changed; scope = 'IDE2001 only; hand-written C# projects; fixture/template/generated exclusions in editorconfig'; projects = $projects; excludedPaths = $excluded; tests = 'Not run: this job produces a reviewed formatting proposal, not release acceptance' } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'summary.json')
"IDE2001 proposal: $($changed.Count) changed source files; token checks passed; no commit or push." >> $env:GITHUB_STEP_SUMMARY
