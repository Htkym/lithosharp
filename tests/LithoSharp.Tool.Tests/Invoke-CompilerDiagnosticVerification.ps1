[CmdletBinding()]
param(
    [ValidateSet('Discover', 'Adapter', 'Consumer')][string] $Mode = 'Adapter',
    [Parameter(Mandatory)][string] $ToolDirectory,
    [Parameter(Mandatory)][string] $WorkDirectory,
    [string] $Configuration = 'Release',
    [string] $DotNet = 'dotnet',
    [string] $VerificationScript,
    [string] $CorePackage,
    [string] $AnalyzerPackage,
    [string] $CoreSha256,
    [string] $AnalyzerSha256
)
$ErrorActionPreference = 'Stop'
$dotNetPath = (Get-Command $DotNet -ErrorAction Stop).Source
$toolRoot = (Resolve-Path -LiteralPath $ToolDirectory).Path
$tool = Join-Path $toolRoot 'LithoSharp.Tool.dll'
$hostCore = Join-Path $PSScriptRoot "bin/$Configuration/net10.0/LithoSharp.dll"
foreach ($file in @($tool, $hostCore, (Join-Path $toolRoot 'LithoSharp.dll'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required built input is missing: $file" }
}
if ((Get-FileHash -LiteralPath $hostCore).Hash -ne (Get-FileHash -LiteralPath (Join-Path $toolRoot 'LithoSharp.dll')).Hash) {
    throw 'Build this host with CompilerDiagnosticToolDirectory pointing at the same Tool directory.'
}
$work = [IO.Path]::GetFullPath($WorkDirectory)
$consumer = Join-Path $work 'consumer'
if ($Mode -eq 'Consumer') {
    if (Test-Path -LiteralPath $consumer) { throw 'Consumer mode requires a fresh work directory; it never repeats an existing consumer compile.' }
    $inputs = @(
        @{ Path = $CorePackage; Hash = $CoreSha256; Id = 'LithoSharp' },
        @{ Path = $AnalyzerPackage; Hash = $AnalyzerSha256; Id = 'LithoSharp.Analyzers' }
    )
    foreach ($inputPackage in $inputs) {
        if (-not $inputPackage.Path -or $inputPackage.Hash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Consumer mode requires both package paths and reviewed SHA256 values.' }
        $inputPackage.Path = (Resolve-Path -LiteralPath $inputPackage.Path).Path
        if ((Get-FileHash -LiteralPath $inputPackage.Path).Hash -ne $inputPackage.Hash) { throw "Package hash differs: $($inputPackage.Id)" }
        $zip = [IO.Compression.ZipFile]::OpenRead($inputPackage.Path)
        try {
            $specs = @($zip.Entries | Where-Object { $_.FullName -like '*.nuspec' })
            if ($specs.Count -ne 1) { throw 'Expected one package manifest.' }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($spec.package.metadata.id -ne $inputPackage.Id) { throw 'Package identity differs.' }
            $inputPackage.Version = [string] $spec.package.metadata.version
        } finally { $zip.Dispose() }
    }
    $site = Join-Path $consumer 'site'
    $feed = Join-Path $consumer 'feed'
    New-Item -ItemType Directory -Path $site, $feed -Force | Out-Null
    foreach ($inputPackage in $inputs) { Copy-Item -LiteralPath $inputPackage.Path -Destination $feed }
    $project = [xml] '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableNETAnalyzers>false</EnableNETAnalyzers><NuGetAudit>false</NuGetAudit><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup/></Project>'
    foreach ($inputPackage in $inputs) {
        $reference = $project.CreateElement('PackageReference')
        $reference.SetAttribute('Include', $inputPackage.Id)
        $reference.SetAttribute('Version', "[$($inputPackage.Version)]")
        if ($inputPackage.Id -eq 'LithoSharp.Analyzers') { $reference.SetAttribute('PrivateAssets', 'all') }
        $project.SelectSingleNode('/Project/ItemGroup').AppendChild($reference) | Out-Null
    }
    $project.Save((Join-Path $site 'Consumer.csproj'))
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $consumer
    $config = [xml] '<configuration><packageSources><clear/></packageSources><packageSourceMapping><clear/></packageSourceMapping></configuration>'
    foreach ($source in @(@('accepted-packages', $feed), @('markdown-fixed', (Join-Path $repo 'eng/markdown/feed')), @('nuget.org', 'https://api.nuget.org/v3/index.json'))) {
        $add = $config.CreateElement('add'); $add.SetAttribute('key', $source[0]); $add.SetAttribute('value', $source[1])
        $config.configuration.packageSources.AppendChild($add) | Out-Null
    }
    $config.Save((Join-Path $consumer 'NuGet.Config'))
}
$evidence = Join-Path $work 'evidence'
$temp = Join-Path $work 'tmp'
New-Item -ItemType Directory -Path $evidence, $temp -Force | Out-Null
$environment = @{
    COMPILER_DIAGNOSTIC_TOOL = $tool
    COMPILER_DIAGNOSTIC_DOTNET = $dotNetPath
    COMPILER_DIAGNOSTIC_EVIDENCE = $evidence
    COMPILER_DIAGNOSTIC_CONSUMER = $consumer
    PATH = (Split-Path -Parent $dotNetPath) + [IO.Path]::PathSeparator + $env:PATH
    TEMP = $temp
    TMP = $temp
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    MSBUILDDISABLENODEREUSE = '1'
}
$saved = @{}
try {
    foreach ($name in $environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    $adapter = 'CompilerSarifAdapterPreservesUsableLocationsAndSuppression'
    $actual = 'ActualCompileFailureKeepsRelatedPositionsThroughPreflightFormats'
    $selection = switch ($Mode) { 'Adapter' { $adapter } 'Consumer' { $actual } 'Discover' { "($adapter)|($actual)" } }
    $arguments = @('--project', (Join-Path $PSScriptRoot 'LithoSharp.Tool.Tests.csproj'), '--no-build', '--no-restore', '-c', $Configuration,
        '--treenode-filter', "/*/LithoSharp.Tests/CompilerDiagnosticPreflightTests/$selection")
    if ($Mode -eq 'Discover') {
        & $dotNetPath test @arguments --list-tests
    } else {
        $arguments += @('--report-trx', '--results-directory', (Join-Path $work 'trx'), '--fail-fast')
        if ($VerificationScript) { & $VerificationScript -Action test -Arguments $arguments }
        else { & $dotNetPath test @arguments }
    }
    $result = $LASTEXITCODE
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
exit $result
