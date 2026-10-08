[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [ValidateNotNullOrEmpty()] [string] $CandidateVersion = '1.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$markdownPair = Get-Content -LiteralPath (Join-Path $repo 'docs/development/md05-artifact-pair.json') -Raw | ConvertFrom-Json
$packages = [IO.Path]::GetFullPath($PackageDirectory)
foreach ($id in @('LithoSharp', 'LithoSharp.Mdx', 'LithoSharp.Tool', 'LithoSharp.ProjectTemplates')) {
    $package = Join-Path $packages "$id.$CandidateVersion.nupkg"
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "Candidate package not found: $package" }
}

function Assert-CandidatePackages([string] $ProjectDirectory, [string[]] $Required) {
    $assetsPath = Join-Path $ProjectDirectory 'obj/project.assets.json'
    if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw "Resolved package assets not found: $assetsPath" }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $resolved = @{}
    foreach ($name in $assets.libraries.Keys) {
        $parts = $name.Split('/')
        if ($parts[0] -notmatch '^LithoSharp(?:\.|$)' -or $parts[0] -eq 'LithoSharp.FixtureExtension') { continue }
        $expectedVersion = if ($parts[0] -ceq 'LithoSharp.Markdown') { $markdownPair.componentVersion } else { $CandidateVersion }
        if ($assets.libraries[$name].type -ne 'package' -or $parts[1] -cne $expectedVersion) {
            throw "Expected candidate $expectedVersion, but resolved $name in $ProjectDirectory."
        }
        $resolved[$parts[0]] = $parts[1]
    }
    foreach ($id in $Required) {
        if (!$resolved.ContainsKey($id)) { throw "Required candidate package $id was not resolved in $ProjectDirectory." }
    }
    & (Join-Path $repo 'eng/markdown/Test-FixedRuntimeAssets.ps1') -AssetsPath $assetsPath
    Write-Host ("Resolved candidate packages: {0}" -f (($resolved.Keys | Sort-Object | ForEach-Object { "$_/$($resolved[$_])" }) -join ', '))
}
$fixture = Join-Path $repo ('.tmp/template-package-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
# Keep caller's validated shipping feed immutable. FixtureExtension belongs
# only to this unique test feed and never to release package artifacts.
$sourcePackages = $packages
$packages = Join-Path $fixture 'feed'
$null = New-Item -ItemType Directory -Path $packages
foreach ($id in @('LithoSharp', 'LithoSharp.Generators', 'LithoSharp.Images', 'LithoSharp.Tool', 'LithoSharp.ProjectTemplates', 'LithoSharp.Testing', 'LithoSharp.Mdx')) {
    $candidateFile = Join-Path $sourcePackages "$id.$CandidateVersion.nupkg"
    if (Test-Path -LiteralPath $candidateFile -PathType Leaf) {
        Copy-Item -LiteralPath $candidateFile -Destination $packages
    }
}
& (Join-Path $repo 'eng/markdown/Copy-FixedRuntime.ps1') -PackageDirectory $packages
$oldPackages = $env:NUGET_PACKAGES
$oldHome = $env:DOTNET_CLI_HOME
$oldTimestamp = $env:SOURCE_DATE_EPOCH
$oldCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$oldLithoSharpVersion = $env:LithoSharpVersion
try {
    # Keep both the template hive and package cache separate from the user's installations.
    $env:NUGET_PACKAGES = Join-Path $fixture 'packages'
    $env:DOTNET_CLI_HOME = Join-Path $fixture 'cli-home'
    $env:SOURCE_DATE_EPOCH = '1767225600'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    # MSBuild imports this scoped property in both direct builds and CLI child builds.
    $env:LithoSharpVersion = $CandidateVersion
    $hive = Join-Path $fixture 'template-hive'
    $toolDirectory = Join-Path $fixture 'tool'
    $config = Join-Path $fixture 'NuGet.Config'
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    [IO.File]::WriteAllText($config, @"
<configuration>
  <packageSources><clear/><add key="local" value="$escapedPackages"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="LithoSharp*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@)
    dotnet tool install LithoSharp.Tool --version $CandidateVersion --tool-path $toolDirectory --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Tool package installation failed.' }
    dotnet new install (Join-Path $packages "LithoSharp.ProjectTemplates.$CandidateVersion.nupkg") --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0) { throw 'Template package installation failed.' }
    $tool = Join-Path $toolDirectory $(if ($IsWindows) { 'lithosharp.exe' } else { 'lithosharp' })
    foreach ($kind in @('docs', 'blog', 'empty', 'mdx')) {
        $project = Join-Path $fixture $kind
        & $tool new $kind "Test$kind" -o $project --debug:custom-hive $hive
        if ($LASTEXITCODE -ne 0) { throw "New $kind failed." }
        $siteProject = Join-Path $project "Test$kind.csproj"
        $siteXml = [xml][IO.File]::ReadAllText($siteProject)
        $versionProperty = $siteXml.SelectSingleNode('/Project/PropertyGroup/LithoSharpVersion')
        if ($null -eq $versionProperty -or $versionProperty.InnerText -ne '1.1.0' -or $versionProperty.Condition -ne "'`$(LithoSharpVersion)' == ''") {
            throw "$kind template does not declare the overridable stable version 1.1.0."
        }
        $required = @('LithoSharp')
        if ($kind -eq 'mdx') { $required += 'LithoSharp.Mdx' }
        foreach ($reference in $siteXml.SelectNodes('/Project/ItemGroup/PackageReference')) {
            if ($reference.Include -match '^LithoSharp(?:\.|$)' -and $reference.Version -cne '$(LithoSharpVersion)') {
                throw "$kind template does not use its LithoSharpVersion property for $($reference.Include)."
            }
        }
        if ($kind -eq 'mdx') {
            $extension = Join-Path $fixture 'extension'
            $null = New-Item -ItemType Directory -Path $extension
            $extensionXml = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><PackageId>LithoSharp.FixtureExtension</PackageId><Version>1.0.0</Version></PropertyGroup><ItemGroup><PackageReference Include="LithoSharp" Version="' + [Security.SecurityElement]::Escape($CandidateVersion) + '" /></ItemGroup></Project>'
            [IO.File]::WriteAllText((Join-Path $extension 'Extension.csproj'), $extensionXml)
            [IO.File]::WriteAllText((Join-Path $extension 'ExternalContent.cs'), @'
using LithoSharp;
using LithoSharp.Build;
public sealed class ExternalContent : ISiteBuildExtension {
 public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default) =>
  Task.FromResult(new SiteBuildContribution { Assets = [new SiteGeneratedAsset("fixture:external", "external-extension.txt", System.Text.Encoding.UTF8.GetBytes("external C# package"))] });
}
'@)
            dotnet pack $extension -c Release -o $packages
            if ($LASTEXITCODE -ne 0) { throw 'External C# extension packaging failed.' }
            [IO.File]::WriteAllText($siteProject, ([IO.File]::ReadAllText($siteProject)).Replace('</Project>', '<ItemGroup><PackageReference Include="LithoSharp.FixtureExtension" Version="1.0.0" /></ItemGroup></Project>'))
            $factory = Join-Path $project 'MdxSiteFactory.cs'
            [IO.File]::WriteAllText($factory, ([IO.File]::ReadAllText($factory)).Replace('Extensions = [docs]', 'Extensions = [docs, new ExternalContent()]'))
            $component = Join-Path $fixture 'component'
            $null = New-Item -ItemType Directory -Path $component
            [IO.File]::WriteAllText((Join-Path $component 'package.json'), '{"name":"fixture-counter","version":"1.0.0","type":"module","main":"index.jsx"}')
            [IO.File]::WriteAllText((Join-Path $component 'index.jsx'), "import {useState} from 'react';export function ExternalCounter(){const [n,set]=useState(7);return <button onClick={()=>set(n+1)}>External package {n}</button>}")
            Push-Location $component
            try { npm pack --ignore-scripts --pack-destination $project; if ($LASTEXITCODE -ne 0) { throw 'React package creation failed.' } }
            finally { Pop-Location }
            Push-Location $project
            try { npm install ./fixture-counter-1.0.0.tgz --ignore-scripts --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'Explicit React package restore failed.' } }
            finally { Pop-Location }
            [IO.File]::AppendAllText((Join-Path $project 'content/index.mdx'), "`nimport {ExternalCounter} from 'fixture-counter';`n`n<ExternalCounter />`n")
            dotnet build $project -c Release
            if ($LASTEXITCODE -ne 0) { throw 'MDX template compilation failed.' }
            & $tool restore-mdx (Join-Path $project 'bin/Release/net10.0/worker')
            if ($LASTEXITCODE -ne 0) { throw 'Explicit worker restore failed.' }
        }
        & $tool build $project -c Release
        if ($LASTEXITCODE -ne 0) { throw "Build $kind failed." }
        Assert-CandidatePackages $project $required
        $output = Join-Path $project 'dist'
        if (!(Test-Path -LiteralPath (Join-Path $output 'index.html'))) { throw "$kind produced no home page." }
        if ($kind -eq 'mdx') {
            if ([IO.File]::ReadAllText((Join-Path $output 'external-extension.txt')) -ne 'external C# package') { throw 'External C# extension was not loaded.' }
            $body = [IO.File]::ReadAllText((Join-Path $output 'guide/index/index.html'))
            if ($body -notmatch 'External package (<!-- -->)?7') { throw 'External React package was not rendered.' }
        }
        if ($kind -eq 'empty') {
            $html = Get-Content -LiteralPath (Join-Path $output 'index.html') -Raw
            if ($html -notmatch '<main><h1' -or $html -match '&lt;main&gt;') { throw 'Empty template did not render its C# layout.' }
        }
        $snapshot = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($output, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
        } | Sort-Object) -join "`n"
        Push-Location $project
        try {
            dotnet run -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw "Direct library execution of $kind failed." }
        }
        finally { Pop-Location }
        $direct = @(Get-ChildItem -LiteralPath $output -File -Recurse | ForEach-Object {
            [IO.Path]::GetRelativePath($output, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
        } | Sort-Object) -join "`n"
        if ($snapshot -cne $direct) { throw "$kind CLI and direct output differ." }
        & $tool check $project -c Release --format json
        if ($LASTEXITCODE -ne 0) { throw "Quality check of $kind failed." }
        $serveEvidence = Join-Path $fixture ("serve-" + $kind)
        & node (Join-Path $repo 'eng/Test-PackageTemplateServe.mjs') $tool $project $CandidateVersion $serveEvidence
        if ($LASTEXITCODE -ne 0) { throw "Package-only serve of $kind failed." }
    }
    Write-Host "Packaged tool and all four templates passed. Fixture: $fixture"
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:DOTNET_CLI_HOME = $oldHome
    $env:SOURCE_DATE_EPOCH = $oldTimestamp
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $oldCertificate
    $env:LithoSharpVersion = $oldLithoSharpVersion
}
