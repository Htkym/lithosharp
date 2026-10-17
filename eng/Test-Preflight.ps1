[CmdletBinding()]
param([string] $ToolPath, [string] $FixtureRoot, [switch] $KeepFixture)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ToolPath) { $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!$FixtureRoot) { $FixtureRoot = Join-Path $repo '.tmp' }
$FixtureRoot = [IO.Path]::GetFullPath($FixtureRoot)
$fixture = Join-Path $FixtureRoot ('preflight-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$results = [Collections.Generic.List[object]]::new()
$passed = $false

function Assert-True([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Module-Id([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        $metadata.GetGuid($metadata.GetModuleDefinition().Mvid).ToString('D')
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Invoke-Preflight([string] $Name, [string[]] $Arguments, [int] $Exit, [string] $Format = 'json', [bool] $FactoryFailure = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $fixture
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PREFLIGHT_FIXTURE'] = $fixture
    $start.Environment['PREFLIGHT_FACTORY_FAILURE'] = [int]$FactoryFailure
    foreach ($argument in @($ToolPath, 'preflight') + $Arguments + @('--format', $Format)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Preflight fixture timed out: $Name"
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $fixture "$Name.stdout.json"), $out)
        [IO.File]::WriteAllText((Join-Path $fixture "$Name.stderr.txt"), $err)
        Assert-True ($process.ExitCode -eq $Exit) "$Name exit $($process.ExitCode), expected $Exit. $err"
        $json = $out | ConvertFrom-Json
        $results.Add(@{ name = $Name; exit = $process.ExitCode; format = $Format; report = $json })
        Assert-True ((Hash $published) -ceq $publishedHash) "$Name changed published bytes."
        if ($Exit -eq 2) { return $json }
        $meta = if ($Format -eq 'sarif') { $json.runs[0].properties } else { $json.preflight }
        Assert-True ($meta.PSObject.Properties.Name -contains 'coverage') "$Name omitted phase provenance metadata."
        Assert-True ($meta.coverage -ceq 'Partial') "$Name certified whole-site coverage."
        Assert-True (@($meta.ruleVersion).Count -eq 4) "$Name omitted implementation identities."
        foreach ($rule in $meta.ruleVersion) {
            $dll = Join-Path (Split-Path $ToolPath -Parent) ($rule.implementation + '.dll')
            Assert-True ($rule.moduleVersionId -ceq (Module-Id $dll)) "$Name reported a different implementation module."
            Assert-True (![string]::IsNullOrWhiteSpace($rule.informationalVersion)) "$Name omitted implementation version."
        }
        Assert-True (@($meta.unverifiedRuleSources).Count -eq 2) "$Name certified external rule sources."
        return $meta
    } finally { $process.Dispose() }
}

try {
    $published = Join-Path $fixture 'published.txt'
    [IO.File]::WriteAllText($published, 'retain published bytes')
    $publishedHash = Hash $published
    $valid = Join-Path $fixture 'valid.md'
    [IO.File]::WriteAllText($valid, "---`ntitle: 日本語`n---`n# 日本語`n", [Text.UTF8Encoding]::new($true))
    $validHash = Hash $valid
    $good = Invoke-Preflight 'source-json' @('--mode', 'static', '--input', $valid, '--input', $valid) 0
    Assert-True ($good.sourceCoverage -ceq 'Inspected' -and $good.sources.Count -eq 1 -and $good.sources[0].sourceHash -ceq $validHash) 'Selected raw BOM bytes or duplicate handling drifted.'
    $sarif = Invoke-Preflight 'source-sarif' @('--mode', 'static', '--input', $valid) 0 'sarif'
    Assert-True (($good | ConvertTo-Json -Depth 10 -Compress) -ceq ($sarif | ConvertTo-Json -Depth 10 -Compress)) 'JSON and SARIF provenance differ.'
    $bad = Join-Path $fixture 'bad.md'; [IO.File]::WriteAllText($bad, "---`n---`n# broken")
    $invalid = Join-Path $fixture 'invalid.md'; [IO.File]::WriteAllBytes($invalid, [byte[]]@(255, 254))
    $unsupported = Join-Path $fixture 'unsupported.txt'; [IO.File]::WriteAllText($unsupported, 'source')
    $missing = Join-Path $fixture 'missing.md'
    $mixed = Invoke-Preflight 'incomplete-inputs' @('--mode', 'static', '--input', $valid, '--input', $invalid, '--input', $unsupported, '--input', $missing) 1
    Assert-True ($mixed.sourceCoverage -ceq 'Incomplete' -and $mixed.sources[1].sourceHash -ceq (Hash $invalid) -and $mixed.sources[1].readState -ceq 'Read' -and $mixed.sources[1].analysisState -ceq 'NotRun') 'Decode failure was certified as inspection.'
    Assert-True ($mixed.sources[2].analysisState -ceq 'Unsupported' -and $mixed.sources[3].readState -ceq 'NotRead' -and $null -eq $mixed.sources[3].sourceHash) 'Unsupported or unread input was certified.'
    $sourceError = Invoke-Preflight 'source-error-sarif' @('--mode', 'static', '--input', $bad) 1 'sarif'
    Assert-True ($sourceError.sourceCoverage -ceq 'Inspected' -and $sourceError.sources[0].sourceHash -ceq (Hash $bad)) 'Inspected Error lost its provenance.'
    $mdx = Join-Path $fixture 'one.mdx'; [IO.File]::WriteAllText($mdx, '# syntax')
    $workerError = Invoke-Preflight 'worker-error' @('--mode', 'static', '--input', $mdx, '--worker-directory', (Join-Path $fixture 'no-worker'), '--node-executable', (Join-Path $fixture 'no-node')) 1
    Assert-True ($workerError.sources[0].readState -ceq 'Utf8' -and $workerError.sources[0].analysisState -ceq 'Failed' -and $workerError.sourceCoverage -ceq 'Incomplete') 'Worker failure was certified as inspection.'

    $core = [Security.SecurityElement]::Escape((Join-Path (Split-Path $ToolPath -Parent) 'LithoSharp.dll'))
    $site = Join-Path $fixture 'site'; $null = New-Item -ItemType Directory -Path $site
    $project = Join-Path $site 'Site.csproj'
    $rewritten = Join-Path $fixture 'rewritten.md'; [IO.File]::WriteAllText($rewritten, "---`ntitle: Before`n---`n# Before")
    $beforeHash = Hash $rewritten
    $inputXml = [Security.SecurityElement]::Escape($rewritten)
    [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Reference Include="LithoSharp"><HintPath>$core</HintPath></Reference></ItemGroup><Target Name="RewriteSelectedInput" BeforeTargets="CoreCompile"><WriteLinesToFile File="$inputXml" Lines="---&#xA;title: After&#xA;---&#xA;# After" Overwrite="true" Encoding="UTF-8" /></Target></Project>
"@)
    [IO.File]::WriteAllText((Join-Path $site 'Factory.cs'), @'
using LithoSharp; using LithoSharp.Configuration;
public sealed class Factory : ISiteFactory {
 public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default) {
  Console.WriteLine("trusted factory output");
  File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("PREFLIGHT_FIXTURE")!, "factory-calls.txt"), "factory\n");
  if (Environment.GetEnvironmentVariable("PREFLIGHT_FACTORY_FAILURE") == "1") throw new IOException("fixture factory failure");
  return Task.FromResult(new SiteDefinition(new SiteSettings(), []) { OutputDirectory = "output" });
 }
}
'@)
    $sourceStop = Invoke-Preflight 'trusted-source-error' @($project, '--mode', 'trusted', '--input', $bad) 1
    Assert-True ($sourceStop.stage -ceq 'source' -and !(Test-Path (Join-Path $site 'obj'))) 'Source Error advanced to compilation.'
    $brokenSite = Join-Path $fixture 'compiler-error'; $null = New-Item -ItemType Directory -Path $brokenSite
    $brokenProject = Join-Path $brokenSite 'Broken.csproj'
    [IO.File]::WriteAllText($brokenProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $brokenSite 'Broken.cs'), 'public class Broken { MissingType Value; }')
    $compiler = Invoke-Preflight 'compiler-error-sarif' @($brokenProject, '--mode', 'trusted', '--input', $valid) 1 'sarif'
    Assert-True ($compiler.stage -ceq 'compiler' -and $compiler.sources[0].sourceHash -ceq $validHash) 'Compiler failure lost the initial source observation.'
    $catalog = Invoke-Preflight 'catalog-reread' @($project, '--mode', 'trusted', '--input', $rewritten) 0
    Assert-True ($catalog.stage -ceq 'catalog' -and $catalog.sourceCoverage -ceq 'Inspected' -and $catalog.sources[0].sourceHash -ceq (Hash $rewritten) -and $catalog.sources[0].sourceHash -cne $beforeHash) 'Catalog certified the pre-compilation source hash.'
    $factory = Invoke-Preflight 'factory-error-sarif' @($project, '--mode', 'trusted', '--input', $rewritten) 1 'sarif' $true
    Assert-True ($factory.stage -ceq 'factory' -and $factory.sources[0].sourceHash -ceq (Hash $rewritten)) 'Factory failure lost the re-read provenance.'
    $noInputs = Invoke-Preflight 'catalog-no-inputs' @($project, '--mode', 'trusted') 0
    Assert-True ($noInputs.sourceCoverage -ceq 'NotRun' -and $noInputs.sources.Count -eq 0) 'No selected inputs were reported as inspected.'
    $usage = Invoke-Preflight 'usage' @('--mode', 'invalid') 2
    Assert-True ($usage.exitCode -eq 2 -and !$usage.success) 'Usage exit contract changed.'
    $passed = $true
    Write-Host "PASS preflight phase provenance, raw input bytes, re-read, JSON/SARIF and failure boundaries ($($results.Count) CLI invocations)."
} finally {
    @{ passed = $passed; toolPath = $ToolPath; fixture = $fixture; cases = $results } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $fixture 'result.json') -Encoding utf8NoBOM
    if ($passed -and !$KeepFixture) {
        $resolved = [IO.Path]::GetFullPath($fixture)
        if (!$resolved.StartsWith($FixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped its root.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { Write-Host "Preflight evidence: $fixture" }
}
