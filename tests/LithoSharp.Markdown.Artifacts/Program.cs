using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.Markdown;

if (args.Length != 3) throw new ArgumentException("pair manifest, actual Generator DLL, result JSON required");
using var manifest = JsonDocument.Parse(File.ReadAllText(args[0]));
var m = manifest.RootElement;
string Field(string name) => m.GetProperty(name).GetString()!;
var dependency = m.GetProperty("dependencies")[0];
var version = Field("parserVersion");
var yamlAssembly = dependency.GetProperty("assemblyFullName").GetString()!;
var yamlInformation = dependency.GetProperty("informationalVersion").GetString()!;
var generator = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
var sourceYamlReference = generator.GetReferencedAssemblies().Single(a => a.Name == "YamlDotNet");
var sourceYaml = AssemblyLoadContext.GetLoadContext(generator)!.LoadFromAssemblyName(sourceYamlReference);
var sourceYamlInformation = sourceYaml.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
void Check(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }
Check(generator.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName == ".NETStandard,Version=v2.0",
    "Source host must be the actual netstandard2.0 Generator.");
Check(!generator.GetReferencedAssemblies().Any(a => a.Name == "LithoSharp.Markdown" || a.Name == "LithoSharp"),
    "Source host cannot reference a runtime/site facade.");
Check(!generator.GetTypes().Any(t => t.IsPublic && t.Namespace == "LithoSharp.Markdown"),
    "Source host contains a public Markdown facade.");
Check(!generator.GetCustomAttributes<InternalsVisibleToAttribute>().Any(),
    "Runtime friend metadata leaked into source host.");
Check(sourceYamlReference.FullName == yamlAssembly && sourceYaml.FullName == yamlAssembly && sourceYamlInformation == yamlInformation,
    "Source host reference/loaded YAML metadata differs from pair manifest.");
var adapter = generator.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter", true)!;
var parse = adapter.GetMethod("Parse", staticMembers)
    ?? throw new InvalidOperationException("Source symbol did not compile the MD-04 Parse body.");
var optionsType = generator.GetType("LithoSharp.Content.Compilation.MdOptions", true)!;
var options = optionsType.GetConstructors(members).Single().Invoke(
    new object[] { 1048576, 131072, 200, 16777216, Field("profileId"), 1 });
object SourceParse(string raw, string expected = "", string assembly = "")
    => parse.Invoke(null, new object?[] { raw, "pair-scope", "same.md", null, options,
        expected.Length == 0 ? version : expected, Field("contractVersion"), Field("profileId"),
        dependency.GetProperty("version").GetString()!, assembly.Length == 0 ? yamlAssembly : assembly,
        yamlInformation, CancellationToken.None })!;
bool Rejects(string expected, string assembly)
{
    try { SourceParse("# guard\n", expected, assembly); return false; }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { return true; }
}
Check(Rejects("1/" + new string('0', 64), ""), "Source host accepted a mismatched parser stamp.");
Check(Rejects("", "YamlDotNet, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"),
    "Source host accepted mismatched loaded dependency metadata.");
// Same projection for internal source facts and public runtime DTOs. Compare all common fact properties,
// including nested YAML, ranges, source segments and diagnostics; omit only raw ownership/envelope constants.
object? Normalize(object? value)
{
    if (value is null || value is string || value.GetType().IsPrimitive || value is decimal) return value;
    if (value.GetType().IsEnum) return value.ToString();
    if (value is IEnumerable sequence) return sequence.Cast<object?>().Select(Normalize).ToArray();
    var properties = value.GetType().GetProperties(members)
        .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0
            && p.Name is not ("RawText" or "ContractVersion" or "ProfileId"))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    Check(properties.Length > 0, "Unsupported fact type in package comparison: " + value.GetType().FullName);
    return properties.ToDictionary(p => p.Name, p => Normalize(p.GetValue(value)), StringComparer.Ordinal);
}
var fixtures = new[]
{
    "# A &amp; 😀\r\n\r\n[link](target.md)\r\n",
    "\uFEFF---\r\ntitle: x\r\n---\r\n## Y\r\n\r\n~~~csharp\r\nx\r\n~~~\r\n",
    "# Same\n# Same\n\n[link][missing]\n"
};
var snapshots = new List<object>();
foreach (var raw in fixtures)
{
    var runtime = MarkdownParser.Parse(raw, "pair-scope", "same.md");
    var source = SourceParse(raw);
    Check(runtime.ParserVersion == version && runtime.ContractVersion == Field("contractVersion")
        && runtime.ProfileId == Field("profileId"), "Runtime metadata differs from pair manifest.");
    var a = JsonSerializer.Serialize(Normalize(runtime));
    var b = JsonSerializer.Serialize(Normalize(source));
    Check(a == b, "Fixed runtime/source fact mismatch:\n" + a + "\n" + b);
    snapshots.Add(new { utf16Length = raw.Length, sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a))).ToLowerInvariant() });
}
var yaml = typeof(YamlDotNet.Core.Parser).Assembly;
Check(yaml.FullName == yamlAssembly && yaml.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion == yamlInformation,
    "Runtime loaded YAML metadata differs from pair.");
var runtimeAssembly = typeof(MarkdownParser).Assembly;
var report = new
{
    task = "MD-05", status = "PASS", componentVersion = Field("componentVersion"), parserVersion = version,
    canonicalSourceHash = Field("canonicalSourceHash"),
    runtimeAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimeAssembly.Location))).ToLowerInvariant(),
    actualGenerator = generator.Location, generatorSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(generator.Location))).ToLowerInvariant(),
    sourceParseCompiled = true, noSourceFacade = true, noSourceFriendMetadata = true,
    expectedStampRejected = true, expectedDependencyRejected = true,
    yamlAssembly = yaml.FullName, yamlInformation,
    sourceYamlAssembly = sourceYaml.FullName, sourceYamlInformation, fixtures = snapshots,
    actualRoslynHost = "NOT_RUN; IN-01", bothRepoMatrix = "NOT_RUN; IN-01"
};
var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(args[2], output + "\n");
Console.WriteLine(output);
