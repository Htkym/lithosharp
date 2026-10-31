$ErrorActionPreference = 'Stop'
$out = Join-Path $env:RUNNER_TEMP 'control-format-output'
New-Item -ItemType Directory -Force $out | Out-Null
$file = 'tests/LithoSharp.Tests/CompilerDiagnosticPreflightTests.cs'
Copy-Item $file (Join-Path $out 'before.cs')
try {
 dotnet restore tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj --locked-mode
 if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
 dotnet format style tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj --diagnostics IDE2001 --include $file --no-restore
 if ($LASTEXITCODE -ne 0) { throw 'style failed' }
 dotnet format whitespace tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj --include $file --no-restore
 if ($LASTEXITCODE -ne 0) { throw 'whitespace failed' }
 Copy-Item $file (Join-Path $out 'after.cs')
 git diff -- $file | Set-Content -Encoding utf8 (Join-Path $out 'one-file.patch')
 dotnet run --project scripts/FormatDiagnostic/FormatTokenCheck.csproj -- (Join-Path $out 'before.cs') (Join-Path $out 'after.cs') (Join-Path $out 'diagnostic.json')
 if ($LASTEXITCODE -ne 0) { throw 'diagnostic failed' }
} finally { git status --short | Set-Content (Join-Path $out 'status.txt') }
