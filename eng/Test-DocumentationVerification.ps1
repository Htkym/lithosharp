[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path ([IO.Path]::GetTempPath()) ('dv16-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$example = Join-Path $root 'example'
$runner = Join-Path $root 'runner'
$null = New-Item -ItemType Directory -Path $example, $runner
# Verified reads require the physical path, including macOS /var -> /private/var.
$segments = [Collections.Generic.Stack[string]]::new()
for ($directory = [IO.DirectoryInfo]::new($root); $null -ne $directory.Parent; $directory = $directory.Parent) {
    $segments.Push($directory.Name)
}
$physicalRoot = [IO.Path]::GetPathRoot($root)
foreach ($segment in $segments) {
    $directory = [IO.DirectoryInfo]::new((Join-Path $physicalRoot $segment))
    $target = $directory.ResolveLinkTarget($true)
    $physicalRoot = if ($target) { $target.FullName } else { $directory.FullName }
}
$root = $physicalRoot
$example = Join-Path $root 'example'
$runner = Join-Path $root 'runner'
$config = Join-Path $root 'NuGet.Config'
$nuget = [xml](Get-Content -LiteralPath (Join-Path $repo 'NuGet.Config') -Raw)
($nuget.configuration.packageSources.add | Where-Object key -eq 'markdown-fixed').value = Join-Path $repo 'eng/markdown/feed'
$nuget.Save($config)
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination (Join-Path $root 'global.json')
[IO.File]::WriteAllText((Join-Path $example 'Example.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="TUnit" Version="1.81.16"/></ItemGroup></Project>')
[IO.File]::WriteAllText((Join-Path $example 'Example.cs'), @'
namespace VerificationFixture;

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

    /// <summary>Provides a real discovered test whose outcome must remain skipped.</summary>
    [TUnit.Core.Test]
    [TUnit.Core.Skip("This acceptance fixture deliberately skips the selected test.")]
    public void DisabledExample()
    {
        throw new System.InvalidOperationException("A skipped test must not run.");
    }
}
'@)
[IO.File]::WriteAllText((Join-Path $root 'Example.slnx'), '<Solution><Project Path="example/Example.csproj"/></Solution>')
$core = [Security.SecurityElement]::Escape((Join-Path $repo 'src/LithoSharp/LithoSharp.csproj'))
[IO.File]::WriteAllText((Join-Path $runner 'Runner.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include=`"$core`"/></ItemGroup></Project>")
[IO.File]::WriteAllText((Join-Path $runner 'Program.cs'), @'
using LithoSharp.Documentation;
using LithoSharp.Content;
var root = args[0];
var project = Path.Combine(root, "example", "Example.csproj");
var xml = (await DocumentationVerification.BuildApiAsync(Path.Combine(root, "Example.slnx"))).Single(file => Path.GetFileName(file) == "Example.xml");
if (!(await File.ReadAllTextAsync(xml)).Contains("M:VerificationFixture.Arithmetic.Add")) throw new Exception("API XML was not generated.");
await DocumentationVerification.VerifyExamplesAsync(project);
var source = Path.Combine(root, "example", "Example.cs");
var document = Path.Combine(root, "guide.md");
await File.WriteAllTextAsync(document, "# Example\n\n```csharp snippet=\"addition\" source=\"example/Example.cs\"\n```\n");
var declarations = new[] { new DocumentSnippet("addition", "guide.md", "example/Example.csproj", "/*/*/ExampleTests/AdditionWorks") };
var inputs = new[] { ContentDependency.FromFile("example/Example.cs") };
var passed = (await DocumentationVerification.VerifySnippetsAsync(root, declarations, inputs, "Release")).Single();
if (passed.State != DocumentSnippetVerificationState.Success || passed.ExecutedAt is null || passed.Location?.Line != 3)
    throw new Exception($"Explicit snippet execution was not attached to its source fence. State={passed.State}; line={passed.Location?.Line}; executedAt={passed.ExecutedAt:O}. {passed.Message}");
await File.WriteAllTextAsync(source, (await File.ReadAllTextAsync(source)).Replace("return left + right;", "return left - right;"));
var stale = (await DocumentationVerification.InspectSnippetsAsync(root, declarations, inputs, "Release", [passed])).Single();
if (stale.State != DocumentSnippetVerificationState.NotRun || stale.Fingerprint == passed.Fingerprint)
    throw new Exception("Changed code retained a previous successful verification.");
var broken = (await DocumentationVerification.VerifySnippetsAsync(root, declarations, inputs, "Release")).Single();
if (broken.State != DocumentSnippetVerificationState.Failure || broken.Diagnostic?.Location?.Line != 3)
    throw new Exception("Incorrect example unexpectedly passed or lost its document position.");
await File.WriteAllTextAsync(source, (await File.ReadAllTextAsync(source)).Replace("return left - right;", "return left + right;"));
var fixedResult = (await DocumentationVerification.VerifySnippetsAsync(root, declarations, inputs, "Release")).Single();
if (fixedResult.State != DocumentSnippetVerificationState.Success)
    throw new Exception("Corrected example did not recover after explicit execution.");
var onlySkipped = (await DocumentationVerification.VerifySnippetsAsync(root,
    [new DocumentSnippet("addition", "guide.md", "example/Example.csproj", "/*/*/ExampleTests/DisabledExample")], inputs, "Release")).Single();
if (onlySkipped.State != DocumentSnippetVerificationState.Skipped || onlySkipped.ExecutedAt is null)
    throw new Exception("An all-skipped test selection was not recorded as skipped.");
var empty = (await DocumentationVerification.VerifySnippetsAsync(root,
    [new DocumentSnippet("addition", "guide.md", "example/Example.csproj", "/*/*/ExampleTests/DoesNotExist")], inputs, "Release")).Single();
if (empty.State is not (DocumentSnippetVerificationState.NotRun or DocumentSnippetVerificationState.Failure))
    throw new Exception("An empty test selection was incorrectly recorded as executed success or skip.");
Console.WriteLine("API XML and explicit snippets passed; stale/broken examples failed and recovered; skipped/empty selections stayed distinct from success.");
'@)
$plannedPaths = @(
    (Join-Path $example 'obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs'),
    (Join-Path $runner 'obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs')
)
$longestPath = $plannedPaths | Sort-Object Length -Descending | Select-Object -First 1
Write-Host "Documentation fixture: $root; longest planned fixture path: $($longestPath.Length) characters."
foreach ($project in @((Join-Path $example 'Example.csproj'), (Join-Path $runner 'Runner.csproj'))) {
    dotnet restore $project --configfile $config
    if ($LASTEXITCODE -ne 0) { throw 'Documentation verification fixture restore failed.' }
}
dotnet run --project (Join-Path $runner 'Runner.csproj') --configuration Release --no-restore -- $root
if ($LASTEXITCODE -ne 0) { throw "Documentation verification failed. Fixture: $root" }
