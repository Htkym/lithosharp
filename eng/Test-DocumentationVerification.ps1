[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $repo ('.tmp/documentation-verification-' + [Guid]::NewGuid().ToString('N'))
$example = Join-Path $root 'example'
$runner = Join-Path $root 'runner'
$null = New-Item -ItemType Directory -Path $example, $runner
$config = Join-Path $root 'NuGet.Config'
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
[IO.File]::WriteAllText((Join-Path $example 'Example.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="TUnit" Version="1.65.0"/></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $example 'Example.cs'), @'
/// <summary>A checked documentation example.</summary>
public static class Arithmetic
{
    /// <summary>Adds two integers.</summary>
    public static int Add(int left, int right) { return left + right; }
}
/// <summary>Checks the example imported by documentation.</summary>
public class ExampleTests
{
    /// <summary>The published example must remain correct.</summary>
    [TUnit.Core.Test]
    public void AdditionWorks()
    {
        if (Arithmetic.Add(2, 3) != 5) throw new System.InvalidOperationException("Incorrect example");
    }
}
'@)
[IO.File]::WriteAllText((Join-Path $root 'Example.slnx'), '<Solution><Project Path="example/Example.csproj"/></Solution>')
$core = [Security.SecurityElement]::Escape((Join-Path $repo 'src/LithoSharp/LithoSharp.csproj'))
[IO.File]::WriteAllText((Join-Path $runner 'Runner.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include=`"$core`"/></ItemGroup></Project>")
[IO.File]::WriteAllText((Join-Path $runner 'Program.cs'), @'
using LithoSharp.Documentation;
var root = args[0];
var project = Path.Combine(root, "example", "Example.csproj");
var xml = (await DocumentationVerification.BuildApiAsync(Path.Combine(root, "Example.slnx"))).Single(file => Path.GetFileName(file) == "Example.xml");
if (!(await File.ReadAllTextAsync(xml)).Contains("M:Arithmetic.Add")) throw new Exception("API XML was not generated.");
await DocumentationVerification.VerifyExamplesAsync(project);
var source = Path.Combine(root, "example", "Example.cs");
await File.WriteAllTextAsync(source, (await File.ReadAllTextAsync(source)).Replace("return left + right;", "return left - right;"));
try { await DocumentationVerification.VerifyExamplesAsync(project); }
catch (IOException) { Console.WriteLine("API XML and checked examples passed; incorrect example was rejected."); return; }
throw new Exception("Incorrect example unexpectedly passed.");
'@)
foreach ($project in @((Join-Path $example 'Example.csproj'), (Join-Path $runner 'Runner.csproj'))) {
    dotnet restore $project --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Documentation verification fixture restore failed.' }
}
dotnet run --project (Join-Path $runner 'Runner.csproj') --configuration Release --no-restore -- $root
if ($LASTEXITCODE -ne 0) { throw "Documentation verification failed. Fixture: $root" }
