# Compiler diagnostic acceptance host

This development host loads a built Tool and its exact Core assembly. It is
outside `LithoSharp.slnx`, like the package-based analyzer tests. The two linked
tests are enabled only here with `COMPILER_DIAGNOSTIC_ACCEPTANCE`; ordinary
solution tests do not require private input paths or run a consumer compile.

Use the repository-pinned SDK and PowerShell 7. From the repository root, build
the Tool once if the reviewed Tool output is not already available, then build
this host. Paths below are relative to the checkout; an external reviewed Tool
directory can instead be passed with `-p:CompilerDiagnosticToolDirectory=...`.
Use that same directory in the runner.

```powershell
dotnet build src/LithoSharp.Tool/LithoSharp.Tool.csproj -c Release
dotnet restore tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj --locked-mode
dotnet build tests/LithoSharp.Tool.Tests/LithoSharp.Tool.Tests.csproj --no-restore -c Release
$tool = './src/LithoSharp.Tool/bin/Release/net10.0'
$work = Join-Path ([IO.Path]::GetTempPath()) ('compiler-diagnostics-' + [guid]::NewGuid().ToString('N'))
./tests/LithoSharp.Tool.Tests/Invoke-CompilerDiagnosticVerification.ps1 -Mode Discover -ToolDirectory $tool -WorkDirectory $work
./tests/LithoSharp.Tool.Tests/Invoke-CompilerDiagnosticVerification.ps1 -Mode Adapter -ToolDirectory $tool -WorkDirectory $work
```

Discover lists both methods without running them. Adapter runs only the light
SARIF/formatter check. The runner sets the four `COMPILER_DIAGNOSTIC_*` inputs,
keeps evidence/TRX under the selected work directory, and restores the process
environment afterward. `-VerificationScript` optionally selects an installed
RTK verification wrapper. Ordinary discovery can be checked without running
tests, after building the normal test project:

```powershell
dotnet test --project tests/LithoSharp.Tests/LithoSharp.Tests.csproj --no-build --no-restore -c Release --list-tests --treenode-filter '/*/LithoSharp.Tests/CompilerDiagnosticPreflightTests/*'
```

MTP reports zero discovered tests and exit code 8 for that normal-host filter;
that is the expected absence check, not a failed execution of either method.

Consumer is an explicit, separate operation. It creates a new isolated project
with exact versions from two supplied package manifests and checks their
reviewed SHA256 values before copying them into a private feed. Dependencies
come from that feed, the repository's immutable Markdown feed and nuget.org.
It never repacks or modifies the input packages. Supply full paths and reviewed
hashes, using a fresh work directory for the one intentional compiler failure:

```powershell
./tests/LithoSharp.Tool.Tests/Invoke-CompilerDiagnosticVerification.ps1 `
  -Mode Consumer -ToolDirectory $tool -WorkDirectory $freshWork `
  -CorePackage $coreNupkg -CoreSha256 $reviewedCoreHash `
  -AnalyzerPackage $analyzerNupkg -AnalyzerSha256 $reviewedAnalyzerHash
```

The fixture source, suppression, factory markers and retained output are owned
by the unchanged test body. Its `preflight-started.json` guard also prevents a
second intentional compile. Retain the original JSON/stderr/TRX and compare
SARIF through the same response formatter; do not rerun the consumer just to
obtain another format. When reusing reviewed acceptance evidence, run only
Discover/Adapter. The current accepted consumer inputs were Core
`2.0.0-rc.38677594188` (SHA256
`5dcf13e67b841fd1b48bd863781fb7f5b80c29cf8e45ceb36b6b07bad41cae46`)
and Analyzer `2.0.0-ra051203outputs.20261015.1` (SHA256
`eec01278416b7ff70c3e60b96a46f6664773d8da003eb35fbe92f32af45475a2`).
The packages are explicit external inputs, not distributed by this test host.
