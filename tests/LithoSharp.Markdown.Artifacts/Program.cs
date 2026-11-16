using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Syntamark;

if (args.Length != 3)
    throw new ArgumentException("pair manifest, actual Generator DLL, result JSON required");
using var manifest = JsonDocument.Parse(File.ReadAllText(args[0]));
var m = manifest.RootElement;
string Field(string name) => m.GetProperty(name).GetString()!;
var version = Field("parserVersion");
var yamlProfile = Field("yamlProfileId");
var generator = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
void Check(bool valid, string reason)
{
    if (!valid)
        throw new InvalidOperationException(reason);
}
Check(generator.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName == ".NETStandard,Version=v2.0",
    "Source host must be the actual netstandard2.0 Generator.");
Check(Field("contractVersion") == "2.0" && yamlProfile == "native-yaml/1" && m.GetProperty("dependencies").GetArrayLength() == 0,
    "Pair manifest must select contract 2.0 and the dependency-free native YAML profile.");
Check(!generator.GetReferencedAssemblies().Any(a => a.Name == "Syntamark" || a.Name == "LithoSharp"),
    "Source host cannot reference a runtime/site facade.");
Check(!generator.GetTypes().Any(t => t.IsPublic && t.Namespace == "Syntamark"),
    "Source host contains a public Markdown facade.");
Check(!generator.GetCustomAttributes<InternalsVisibleToAttribute>().Any(),
    "Runtime friend metadata leaked into source host.");
Check(!generator.GetReferencedAssemblies().Any(a => a.Name == "YamlDotNet"),
    "Source host cannot reference YamlDotNet.");
var adapter = generator.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter", true)!;
var parse = adapter.GetMethod("Parse", staticMembers)
    ?? throw new InvalidOperationException("Source symbol did not compile the MD-04 Parse body.");
var optionsType = generator.GetType("Syntamark.Compilation.MdOptions", true)!;
var options = optionsType.GetConstructors(members).Single(constructor => constructor.GetParameters().Length == 6).Invoke(
    new object[] { 1048576, 131072, 200, 16777216, Field("profileId"), 1 });
object SourceParse(string raw, string expected = "", string expectedYamlProfile = "")
    => parse.Invoke(null, new object?[] { raw, "pair-scope", "same.md", null, options,
        expected.Length == 0 ? version : expected, Field("contractVersion"), Field("profileId"),
        expectedYamlProfile.Length == 0 ? yamlProfile : expectedYamlProfile, CancellationToken.None })!;
bool Rejects(string expected, string expectedYamlProfile)
{
    try
    {
        SourceParse("# guard\n", expected, expectedYamlProfile);
        return false;
    }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
    {
        return true;
    }
}
Check(Rejects("1/" + new string('0', 64), ""), "Source host accepted a mismatched parser stamp.");
Check(Rejects("", "native-yaml/0"), "Source host accepted a mismatched native YAML profile.");
// Same projection for internal source facts and public runtime DTOs. Compare all common fact properties,
// including nested YAML, ranges, source segments and diagnostics; envelope constants are checked separately.
object? Normalize(object? value)
{
    if (value is null || value is string || value.GetType().IsPrimitive || value is decimal)
        return value;
    if (value.GetType().IsEnum)
        return value.ToString();
    if (value is IEnumerable sequence)
        return sequence.Cast<object?>().Select(Normalize).ToArray();
    var properties = value.GetType().GetProperties(members)
        .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0
            && p.Name is not ("ContractVersion" or "ProfileId"))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    Check(properties.Length > 0, "Unsupported fact type in package comparison: " + value.GetType().FullName);
    var result = properties.ToDictionary(p => p.Name, p => Normalize(p.GetValue(value)), StringComparer.Ordinal);
    if (value is MarkdownFrontMatter frontMatter)
        result["YamlSyntax"] = Normalize(frontMatter.GetYamlSyntax());
    return result;
}
var fixtures = new (string Name, string Raw)[]
{
    ("body", "# A &amp; 😀\r\n\r\n[link](target.md)\r\n"),
    ("frontmatter", "\uFEFF---\r\ntitle: x\r\n---\r\n## Y\r\n\r\n~~~csharp\r\nx\r\n~~~\r\n"),
    ("unresolved-reference", "# Same\n# Same\n\n[link][missing]\n"),
    ("native-yaml", "\uFEFF---\r\nplain: null\r\nquoted: 'null'\r\nescaped: \"A\\n😀\"\r\nflow: [true, 42, 3.5, \"true\"]\r\nnested: {url: https://example.test/docs/, actor: '@alice'}\r\nliteral: |-\r\n  first\r\n  second\r\nfolded: >-\r\n  one\r\n  two\r\n---\r\n# YAML\r\n"),
    ("native-yaml-duplicate", "---\ntitle: first\ntitle: second\n---\nBody"),
    ("native-yaml-unsupported", "---\ntitle: !str text\n---\nBody"),
    ("native-yaml-incomplete", "---\ntitle: [\n---\nBody")
};
var snapshots = new List<object>();
foreach (var (name, raw) in fixtures)
{
    var runtime = MarkdownParser.Parse(raw, "pair-scope", "same.md");
    var source = SourceParse(raw);
    Check(runtime.ParserVersion == version && runtime.ContractVersion == Field("contractVersion")
        && runtime.ProfileId == Field("profileId") && runtime.YamlProfileId == yamlProfile, "Runtime metadata differs from pair manifest.");
    Check((string)source.GetType().GetProperty("ContractVersion", members)!.GetValue(source)! == Field("contractVersion")
        && (string)source.GetType().GetProperty("ProfileId", members)!.GetValue(source)! == Field("profileId")
        && (string)source.GetType().GetProperty("YamlProfileId", members)!.GetValue(source)! == yamlProfile,
        "Source metadata differs from pair manifest.");
    var a = JsonSerializer.Serialize(Normalize(runtime));
    var b = JsonSerializer.Serialize(Normalize(source));
    Check(a == b, name + ": fixed runtime/source fact mismatch:\n" + a + "\n" + b);
    var yaml = runtime.FrontMatter.GetYamlSyntax();
    if (name == "native-yaml")
    {
        Check(yaml is { Status: MarkdownParseStatus.Complete, Root.IsComplete: true }
            && yaml.ProfileId == yamlProfile && yaml.ParserVersion == version, "Native YAML facts are unavailable or have mismatched metadata.");
        var values = yaml!.Root!.Entries.ToDictionary(entry => entry.Key.Value!, entry => entry.Value, StringComparer.Ordinal);
        Check(values["plain"].ScalarKind == YamlScalarKind.Null
            && values["quoted"].ScalarKind == YamlScalarKind.String && values["quoted"].ScalarStyle == YamlScalarStyle.SingleQuoted
            && values["quoted"].Value == "null", "Quoted and plain null must retain different meanings.");
        Check(values["escaped"].ScalarStyle == YamlScalarStyle.DoubleQuoted && values["escaped"].Value == "A\n😀"
            && values["escaped"].GetRawText() == "\"A\\n😀\"", "Decoded YAML scalar and raw UTF16 span differ.");
        Check(values["flow"].Children.Select(child => child.ScalarKind).SequenceEqual(new[] { YamlScalarKind.Boolean,
            YamlScalarKind.Integer, YamlScalarKind.Float, YamlScalarKind.String }), "Flow scalar kinds differ.");
        Check(values["literal"].ScalarStyle == YamlScalarStyle.Literal && values["literal"].Value == "first\nsecond"
            && values["folded"].ScalarStyle == YamlScalarStyle.Folded && values["folded"].Value == "one two", "Block scalar style or decoding differs.");
    }
    if (name.StartsWith("native-yaml-", StringComparison.Ordinal))
        Check(yaml is { Status: MarkdownParseStatus.Partial } && yaml.Diagnostics.Count != 0,
            "Native YAML rejection must retain diagnostics and partial status.");
    snapshots.Add(new { name, utf16Length = raw.Length, yamlStatus = yaml?.Status.ToString(), yamlDiagnosticCount = yaml?.Diagnostics.Count,
        sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a))).ToLowerInvariant() });
}
var runtimeAssembly = typeof(MarkdownParser).Assembly;
Check(!runtimeAssembly.GetReferencedAssemblies().Any(a => a.Name == "YamlDotNet"), "Runtime cannot reference YamlDotNet.");
var report = new
{
    task = "MD-05",
    status = "PASS",
    componentVersion = Field("componentVersion"),
    parserVersion = version,
    canonicalSourceHash = Field("canonicalSourceHash"),
    runtimeAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimeAssembly.Location))).ToLowerInvariant(),
    actualGenerator = generator.Location,
    generatorSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(generator.Location))).ToLowerInvariant(),
    sourceParseCompiled = true,
    noSourceFacade = true,
    noSourceFriendMetadata = true,
    expectedStampRejected = true,
    expectedYamlProfileRejected = true,
    yamlProfileId = yamlProfile,
    noYamlDotNetReferences = true,
    fixtures = snapshots,
    actualRoslynHost = "NOT_RUN; IN-01",
    bothRepoMatrix = "NOT_RUN; IN-01"
};
var output = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(args[2], output + "\n");
Console.WriteLine(output);
