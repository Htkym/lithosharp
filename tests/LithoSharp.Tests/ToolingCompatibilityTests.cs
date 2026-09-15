using System.Text.Json;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

/// <summary>
/// T10: tooling contracts are classified, schema versions negotiate explicitly,
/// and unknown fields stay ignored so 1.x can grow additively.
/// </summary>
public sealed class ToolingCompatibilityTests
{
    [Test]
    [Arguments("1.")]
    [Arguments("1.invalid")]
    [Arguments("1.-2")]
    [Arguments("1.0junk")]
    [Arguments("+1.0")]
    public async Task InvalidVersionSuffixIsRejected(string version)
    {
        await Assert.That(ToolingCompatibility.IsCompatible(version)).IsFalse();
        await Assert.That(ToolingCompatibility.IsCompatible("1.0", version)).IsFalse();
        await Assert.That(() => ToolingCompatibility.CheckSchemaVersion(version)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ContractsAreClassified()
    {
        var byName = ToolingContracts.All.ToDictionary(contract => contract.Name, StringComparer.Ordinal);

        await Assert.That(byName.Keys).IsEquivalentTo(
            ["DiagnosticCodes", "JsonEnvelopes", "Inspection", "DevServer", "MigrationReport"]);
        foreach (var contract in ToolingContracts.All)
        {
            await Assert.That(contract.Maturity).IsEqualTo(ToolingContractMaturity.Stable);
            await Assert.That(contract.SchemaVersion).IsEqualTo("1.0");
            await Assert.That(string.IsNullOrWhiteSpace(contract.Description)).IsFalse();
        }
        await Assert.That(ToolingContracts.MigrationReport.SchemaVersion).IsEqualTo(
            ReportSchemaVersion());

        static string ReportSchemaVersion()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "site");
            Directory.CreateDirectory(Path.Combine(directory, "docs"));
            File.WriteAllText(Path.Combine(directory, "docs", "intro.md"), "---\ntitle: Intro\n---\n\nBody.\n");
            try
            {
                return DocusaurusMigrationReport.Analyze(directory).SchemaVersion;
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
            }
        }
    }

    [Test]
    public async Task SchemaVersionNegotiation()
    {
        await Assert.That(ToolingCompatibility.IsCompatible("1.0")).IsTrue();
        await Assert.That(ToolingCompatibility.IsCompatible("1.10")).IsTrue();
        await Assert.That(ToolingCompatibility.IsCompatible("1.0", "1.1")).IsTrue();
        await Assert.That(ToolingCompatibility.IsCompatible("2.0")).IsFalse();
        await Assert.That(ToolingCompatibility.IsCompatible("0.9")).IsFalse();
        await Assert.That(ToolingCompatibility.IsCompatible("latest")).IsFalse();
        await Assert.That(ToolingCompatibility.IsCompatible(string.Empty)).IsFalse();

        await Assert.That(ToolingCompatibility.CheckSchemaVersion("1.0")).IsEqualTo("1.0");
        await Assert.That(ToolingCompatibility.CheckSchemaVersion("1.4")).IsEqualTo("1.4");
        var failed = false;
        try
        {
            ToolingCompatibility.CheckSchemaVersion("2.0");
        }
        catch (InvalidOperationException exception)
        {
            failed = true;
            await Assert.That(exception.Message.Contains("2.0", StringComparison.Ordinal)).IsTrue();
            await Assert.That(exception.Message.Contains(ToolingContracts.CurrentSchemaVersion, StringComparison.Ordinal)).IsTrue();
        }
        await Assert.That(failed).IsTrue();
    }

    [Test]
    public async Task UnknownFieldsAreIgnored()
    {
        const string json = """
            {"variants":[{"version":"current","locale":"en","inputDirectory":"docs","suggestedRoutePrefix":"docs","futureFlag":true}],
             "versionedSidebars":{"docs":"sidebars.json","extra":"1"},"blogAuthors":[{"id":"ada","name":"Ada","nickname":"x"}],
             "manualSteps":[],"unsupportedNotes":[],
             "futureField":123,"nested":{"x":1},"variantsExtra":null}
            """;
        var manifest = JsonSerializer.Deserialize<DocusaurusMigrationManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        await Assert.That(manifest).IsNotNull();
        await Assert.That(manifest!.Variants.Count).IsEqualTo(1);
        await Assert.That(manifest.Variants[0].InputDirectory).IsEqualTo("docs");
        await Assert.That(manifest.BlogAuthors.Count).IsEqualTo(1);
        await Assert.That(manifest.BlogAuthors[0].Name).IsEqualTo("Ada");
        await Assert.That(manifest.ManualSteps.Count).IsEqualTo(0);
    }
}
