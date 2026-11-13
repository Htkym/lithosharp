[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $CandidateVersion,
    [Parameter(Mandatory)] [string] $WorkerDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packages = [IO.Path]::GetFullPath($PackageDirectory, $repo)
$worker = [IO.Path]::GetFullPath($WorkerDirectory, $repo)
$output = [IO.Path]::GetFullPath($OutputDirectory, $repo)
$pair = Get-Content -LiteralPath (Join-Path $repo 'eng/markdown/component-pair.json') -Raw | ConvertFrom-Json
if ($CandidateVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Use one exact candidate version.' }
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be new.' }
foreach ($id in @('LithoSharp', 'LithoSharp.Mdx')) {
    if (!(Test-Path -LiteralPath (Join-Path $packages "$id.$CandidateVersion.nupkg") -PathType Leaf)) { throw "Missing candidate package: $id" }
}
if (!(Test-Path -LiteralPath (Join-Path $worker 'node_modules/esbuild') -PathType Container)) { throw 'Restore the locked worker before building delivery fixtures.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('ldel-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$project = Join-Path $work 'p'
$content = Join-Path $work 'input'
$null = New-Item -ItemType Directory -Path $project, $content
$longest = (Join-Path $project 'obj/Release/net10.0/DeliveryBrowserFixture.AssemblyInfoInputs.cache').Length
foreach ($packageFile in @((Get-Item -LiteralPath (Join-Path $packages "LithoSharp.$CandidateVersion.nupkg")),
    (Get-Item -LiteralPath (Join-Path $packages "LithoSharp.Mdx.$CandidateVersion.nupkg")),
    (Get-Item -LiteralPath (Join-Path $repo "eng/markdown/feed/Syntamark.$($pair.componentVersion).nupkg")))) {
    $archive = [IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
    try {
        foreach ($entry in $archive.Entries) {
            $expanded = Join-Path $work ('n/' + $packageFile.BaseName + '/' + $entry.FullName)
            $longest = [Math]::Max($longest, $expanded.Length)
        }
    }
    finally { $archive.Dispose() }
}
Write-Host "Estimated longest fixture build/package path: $longest characters."
if ($longest -ge 240) { throw "Choose a shorter temporary root before building (estimated path: $longest)." }
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination (Join-Path $work 'global.json')
[IO.File]::WriteAllText((Join-Path $project 'Delivery.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="LithoSharp" Version="[$CandidateVersion]"/><PackageReference Include="LithoSharp.Mdx" Version="[$CandidateVersion]"/></ItemGroup>
</Project>
"@)
$feed = [Security.SecurityElement]::Escape((Join-Path $repo 'eng/markdown/feed'))
$packageSource = [Security.SecurityElement]::Escape($packages)
$config = Join-Path $work 'NuGet.Config'
[IO.File]::WriteAllText($config, "<configuration><packageSources><clear/><add key=`"candidate`" value=`"$packageSource`"/><add key=`"markdown`" value=`"$feed`"/><add key=`"nuget`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>")
[IO.File]::WriteAllText((Join-Path $content 'a.mdx'), @'
---
title: Page A
---
import Counter from './Counter.jsx'
import Tabs from '@theme/Tabs'
import TabItem from '@theme/TabItem'

# Page A

HydraulicConstellation is the fixed search canary.

<Counter initial={3}/>

<Tabs groupId="delivery-language">
<TabItem value="en" label="English">English content</TabItem>
<TabItem value="ja" label="Japanese">Japanese content</TabItem>
</Tabs>

[Next page](./b.mdx)
'@)
[IO.File]::WriteAllText((Join-Path $content 'b.mdx'), @'
---
title: Page B
---
import Counter from './Counter.jsx'

# Page B

<Island component={Counter} props={{initial: 10}} schema={{type:'object',properties:{initial:{type:'integer'}},additionalProperties:false}} strategy="load"/>

[Previous page](./a.mdx)
'@)
[IO.File]::WriteAllText((Join-Path $content 'Counter.jsx'), "import {useState} from 'react';export default function Counter({initial}){const [n,set]=useState(initial);return <button onClick={()=>set(n+1)}>Count {n}</button>}")
[IO.File]::WriteAllText((Join-Path $project 'Program.cs'), @'
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

foreach (var (folder, prefix) in new[] { ("root", "/"), ("sub", "/sub/") })
{
    await using var docs = new DocumentationSite(new(args[0], args[1]) { Cacheable = true, Hydration = "selective" })
    { Browser = new() { Navigation = true, Search = true, Offline = false } };
    docs.AddCollection(new("guide", [new("current", "en", args[0], "guide")]) { UseMdx = true });
    await new SiteGenerator().GenerateWithOptionsAsync(
        new SiteSettings { Title = "Delivery", BaseUrl = "https://example.test" + prefix, Language = "en", TimeZone = "UTC" },
        [], Path.Combine(args[2], folder), true,
        new() { Template = new DocsSiteTemplate { EnableSearch = true, EnableHeaderSearch = true },
            Theme = new() { UseExternalThemeRestoration = true }, FaviconSourceDirectory = Path.Combine(args[0], "no-favicons") },
        new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
}
'@)
$projectFile = Join-Path $project 'Delivery.csproj'
dotnet restore $projectFile --configfile $config --packages (Join-Path $work 'n') --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Candidate delivery fixture restore failed: $work" }
$assets = Get-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$resolved = [ordered]@{}
$hashes = [ordered]@{}
foreach ($id in @('LithoSharp', 'LithoSharp.Mdx', 'Syntamark')) {
    $expected = if ($id -eq 'Syntamark') { $pair.componentVersion } else { $CandidateVersion }
    $key = "$id/$expected"
    if (!$assets.libraries.ContainsKey($key) -or $assets.libraries[$key].type -cne 'package') { throw "Expected package consumer: $key" }
    $resolved[$id] = $expected
    $packagePath = if ($id -eq 'Syntamark') { Join-Path $repo ("eng/markdown/feed/$id.$expected.nupkg") } else { Join-Path $packages "$id.$expected.nupkg" }
    $hashes[$id] = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
dotnet run --project $projectFile --configuration Release --no-restore -- $content $worker $output
if ($LASTEXITCODE -ne 0) { throw "Candidate delivery fixture generation failed: $work" }
$receipt = [ordered]@{ schemaVersion = 1; source = 'candidate-packages'; packages = $resolved; packageHashes = $hashes;
    workerCompilerHash = (Get-FileHash -LiteralPath (Join-Path $worker 'compiler.mjs') -Algorithm SHA256).Hash.ToLowerInvariant();
    workerLockHash = (Get-FileHash -LiteralPath (Join-Path $worker 'package-lock.json') -Algorithm SHA256).Hash.ToLowerInvariant(); fixtureWorkDirectory = $work;
    placements = @(@{ name = 'root'; directory = 'root'; basePath = '/' }, @{ name = 'sub'; directory = 'sub'; basePath = '/sub/' }) }
[IO.File]::WriteAllText((Join-Path $output 'fixture.json'), ($receipt | ConvertTo-Json -Depth 8) + "`n")
Write-Host "Candidate delivery fixture: $output"
