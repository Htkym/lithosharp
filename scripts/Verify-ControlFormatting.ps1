$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$out = Join-Path $env:RUNNER_TEMP 'control-format-output'
$baseline = Join-Path $env:RUNNER_TEMP 'control-format-baseline'
New-Item -ItemType Directory -Force $out, $baseline | Out-Null
$base = '1c1b54c86ef75511f8af30d7f3d3fc445d084909'
git fetch --no-tags --depth=1 origin $base
if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch fixed baseline' }
$tracked = @(git ls-tree -r --name-only $base | Where-Object { $_ -like '*.cs' })
foreach ($file in $tracked) {
    $destination = Join-Path $baseline $file
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    git show "${base}:$file" > $destination
    if ($LASTEXITCODE -ne 0) { throw "Unable to read baseline: $file" }
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
$compilerFixture = 'tests/LithoSharp.Tests/CompilerDiagnosticPreflightTests.cs'
$compilerProject = 'tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj'
$excluded = @('src/LithoSharp/HtmlParsing/HtmlCharacterReferences.g.cs', 'tests/fixtures/', 'tests/LithoSharp.Tests/Fixtures/', 'templates/')
foreach ($project in $projects) {
    if (-not (Test-Path $project)) { throw "Missing project: $project" }
    $lock = Join-Path (Split-Path $project) 'packages.lock.json'
    if (Test-Path $lock) {
        dotnet restore $project --locked-mode
    } else {
        dotnet restore $project
    }
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
Check-Tokens 'all-source-token-check'
foreach ($project in $projects) {
    $projectExcluded = @($excluded)
    if ($project -ne $compilerProject) { $projectExcluded += $compilerFixture }
    dotnet format style $project --diagnostics IDE2001 --exclude @projectExcluded --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "IDE2001 verification failed: $project" }
}
foreach ($project in $projects) {
    $projectExcluded = @($excluded)
    if ($project -ne $compilerProject) { $projectExcluded += $compilerFixture }
    dotnet format whitespace $project --exclude @projectExcluded --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw "Whitespace verification failed: $project" }
}
git diff --check $base -- '*.cs'
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }
git diff --binary --output="$out/control-format.patch" $base -- '*.cs' ':!scripts/FormatTokenCheck/*' ':!scripts/FormatDiagnostic/*'
if ($LASTEXITCODE -ne 0) { throw 'Patch generation failed' }
[ordered]@{ sourceCommit = $env:GITHUB_SHA; baseline = $base; sdk = (dotnet --version); completed = $true; scope = 'verification only; 24 projects; conditional compiler fixture verified only in Tool.Tests'; projects = $projects; excludedPaths = $excluded } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'summary.json')
