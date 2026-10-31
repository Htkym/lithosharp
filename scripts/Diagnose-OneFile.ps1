$ErrorActionPreference = 'Stop'
$out = Join-Path $env:RUNNER_TEMP 'control-format-output'
New-Item -ItemType Directory -Force $out | Out-Null
$file = 'tests/LithoSharp.Tests/CompilerDiagnosticPreflightTests.cs'
Copy-Item $file (Join-Path $out 'before.cs')
try {
 $projects = @('tests/LithoSharp.Tests/LithoSharp.Tests.csproj', 'tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj')
 foreach ($project in $projects) {
  dotnet restore $project --locked-mode
  if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
 }
 foreach ($project in $projects) {
  dotnet format style $project --diagnostics IDE2001 --include $file --no-restore
  if ($LASTEXITCODE -ne 0) { throw 'style failed' }
 }
 foreach ($project in $projects) {
  dotnet format whitespace $project --include $file --no-restore
  if ($LASTEXITCODE -ne 0) { throw 'whitespace failed' }
 }
 Copy-Item $file (Join-Path $out 'after.cs')
 git diff -- $file | Set-Content -Encoding utf8 (Join-Path $out 'one-file.patch')
 dotnet run --project scripts/FormatDiagnostic/FormatTokenCheck.csproj -- (Join-Path $out 'before.cs') (Join-Path $out 'after.cs') (Join-Path $out 'diagnostic.json')
 if ($LASTEXITCODE -ne 0) { throw 'diagnostic failed' }
 $base = Join-Path $env:RUNNER_TEMP 'one-file-baseline'
 $target = Join-Path $base $file
 New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
 Copy-Item (Join-Path $out 'before.cs') $target
 dotnet run --project scripts/FormatTokenCheck/FormatTokenCheck.csproj -- $base (Get-Location).Path (Join-Path $out 'strict-token-report.json')
 if ($LASTEXITCODE -ne 0) { throw 'strict diagnostic reproduced failure' }

} finally { git status --short | Set-Content (Join-Path $out 'status.txt') }
