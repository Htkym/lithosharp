[CmdletBinding()]
param(
    [int] $Port = 0,
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repo ('.tmp/deployment-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $fixture 'site'
$content = Join-Path $project 'content'
$dist = Join-Path $fixture 'dist'
$null = New-Item -ItemType Directory -Path $content
$worker = (Join-Path $repo 'src/LithoSharp.Mdx/worker').Replace('\', '/')
$litho = (Join-Path $repo 'src/LithoSharp/LithoSharp.csproj').Replace('\', '/')
$mdx = (Join-Path $repo 'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj').Replace('\', '/')
[IO.File]::WriteAllText((Join-Path $project 'DeploySite.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$litho" />
    <ProjectReference Include="$mdx" />
  </ItemGroup>
</Project>
"@)
[IO.File]::WriteAllText((Join-Path $content 'a.mdx'), "---`ntitle: Page A`n---`n`n# Page A`n`nFirst words.`n")
[IO.File]::WriteAllText((Join-Path $content 'b.mdx'), "---`ntitle: Page B`n---`n`n# Page B`n`nSecond words.`n")
[IO.File]::WriteAllText((Join-Path $project 'Program.cs'), @"
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
var docs = new DocumentationSite(new(@"$project", "$worker") { Cacheable = true }) { Browser = new() };
docs.AddCollection(new("guide", [new("current", "en", @"$content", "guide")]) { UseMdx = true });
var output = @"$dist";
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    new SiteSettings { Title = "Deploy", BaseUrl = "https://example.test/sub/" }, [], output, true,
    new() { Template = new DocsSiteTemplate() },
    new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
Console.WriteLine("Generated " + result.GeneratedFiles.Count + " files.");
await docs.DisposeAsync();
"@)
$server = $null
try {
    dotnet run --project (Join-Path $project 'DeploySite.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Sub-path site generation failed.' }
    if ($Port -eq 0) {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $probe.Start()
        $Port = $probe.LocalEndpoint.Port
        $probe.Stop()
    }
    $start = [Diagnostics.ProcessStartInfo]::new('node')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Join-Path $repo 'tests/fixtures/mdx-browser'
    $start.ArgumentList.Add('serve.mjs')
    $start.ArgumentList.Add($dist)
    $start.ArgumentList.Add([string] $Port)
    $start.Environment['LS_BASE_PATH'] = '/sub'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $server = [Diagnostics.Process]::Start($start)
    $origin = "http://127.0.0.1:$Port"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri "$origin/sub/guide/a/" -NoProxy -SkipHttpErrorCheck -TimeoutSec 5
            if ([int] $response.StatusCode -eq 200) { break }
        }
        catch { }
        if ($server.HasExited) { throw "Sub-path server exited early with code $($server.ExitCode)." }
        if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) { throw 'Sub-path server did not respond in time.' }
        Start-Sleep -Milliseconds 500
    }
    Push-Location (Join-Path $repo 'tests/fixtures/mdx-browser')
    try {
        node run-subpath.mjs $origin
        if ($LASTEXITCODE -ne 0) { throw 'Sub-path browser checks failed.' }
    }
    finally { Pop-Location }
    Write-Host "Deployment checks passed (sub-path, versioned assets, trailing slash, navigation, 404). Fixture: $fixture"
}
finally {
    if ($server -ne $null -and !$server.HasExited) { $server.Kill($true); $server.WaitForExit(15000) | Out-Null }
}
