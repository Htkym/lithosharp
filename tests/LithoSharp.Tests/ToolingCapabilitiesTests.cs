using System.Text.Json;
using System.Text.Json.Nodes;

namespace LithoSharp.Tests;

/// <summary>
/// V110-03: the capability contract lets a client decide features without guessing
/// from version strings. The catalog, the wire report and the negotiation helpers are
/// additive inside schema major 1, and a missing required capability fails with an
/// explanation instead of being assumed.
/// </summary>
public sealed class ToolingCapabilitiesTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Test]
    public async Task CatalogIsClassified()
    {
        await Assert.That(ToolingCapabilities.All.Select(capability => capability.Name)).IsEquivalentTo(
            ["document-inspection", "versioned-snapshot", "serve-shutdown", "source-route-lookup"]);

        foreach (var capability in ToolingCapabilities.All)
        {
            await Assert.That(capability.Maturity).IsEqualTo(ToolingContractMaturity.Stable);
            await Assert.That(capability.SchemaVersion).IsEqualTo(ToolingContracts.CurrentSchemaVersion);
            await Assert.That(string.IsNullOrWhiteSpace(capability.Description)).IsFalse();
        }

        var inspection = ToolingCapabilities.All.Single(capability => capability.Name == ToolingCapabilities.DocumentInspection);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.LanguageMarkdown);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.LanguageMdx);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.StageSyntax);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.StageFrontMatter);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.StageProjectResolution);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.StageGeneratedOutput);
        await Assert.That(inspection.Scope).Contains(ToolingCapabilities.StageRuntime);
    }

    [Test]
    public async Task LookupAndNegotiation()
    {
        await Assert.That(ToolingCapabilities.TryGet(ToolingCapabilities.DocumentInspection, out var known)).IsTrue();
        await Assert.That(known).IsNotNull();
        await Assert.That(known!.Name).IsEqualTo(ToolingCapabilities.DocumentInspection);
        await Assert.That(ToolingCapabilities.TryGet("future-capability", out var unknown)).IsFalse();
        await Assert.That(unknown).IsNull();

        // Unknown advertised names are ignored, and a required name that the peer advertises counts as present.
        var missing = ToolingCapabilities.Missing(
            ["document-inspection", "future-capability"],
            ["future-capability", ToolingCapabilities.DocumentInspection, ToolingCapabilities.ServeShutdown]);
        await Assert.That(missing).IsEquivalentTo([ToolingCapabilities.ServeShutdown]);

        await Assert.That(ToolingCapabilities.Missing(
            [ToolingCapabilities.DocumentInspection], [ToolingCapabilities.DocumentInspection])).IsEmpty();

        var message = string.Empty;
        try
        {
            ToolingCapabilities.Require([ToolingCapabilities.DocumentInspection], [ToolingCapabilities.SourceRouteLookup]);
        }
        catch (InvalidOperationException exception)
        {
            message = exception.Message;
        }
        await Assert.That(message).Contains(ToolingCapabilities.SourceRouteLookup);
        await Assert.That(message).Contains("disable the feature");
        await Assert.That(message).Contains(ToolingCapabilities.DocumentInspection);

        // Every catalog capability satisfies a required set, so this must not throw.
        ToolingCapabilities.Require(
            ToolingCapabilities.All.Select(capability => capability.Name),
            [ToolingCapabilities.ServeShutdown]);
    }

    [Test]
    public async Task ReportMatchesTheWireContract()
    {
        var report = ToolingCapabilities.CreateReport(
            new ToolingIdentityInfo("LithoSharp.Tool", "1.0.0"),
            new ToolingIdentityInfo("LithoSharp", "1.0.0"));
        var json = JsonSerializer.Serialize(report, WireOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        await Assert.That(root.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(
            ["schemaVersion", "success", "exitCode", "tool", "core", "project", "contracts", "capabilities", "error"]);
        await Assert.That(root.GetProperty("schemaVersion").GetString()).IsEqualTo(ToolingContracts.CurrentSchemaVersion);
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("tool").GetProperty("name").GetString()).IsEqualTo("LithoSharp.Tool");
        await Assert.That(root.GetProperty("core").GetProperty("version").GetString()).IsEqualTo("1.0.0");

        var project = root.GetProperty("project");
        await Assert.That(project.GetProperty("resolved").GetBoolean()).IsFalse();
        await Assert.That(project.GetProperty("coreVersion").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(project.GetProperty("reason").GetString()).IsNotEmpty();

        await Assert.That(root.GetProperty("contracts").EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? string.Empty))
            .IsEquivalentTo(ToolingContracts.All.Select(contract => contract.Name));
        await Assert.That(root.GetProperty("capabilities").EnumerateArray().Select(item => item.GetProperty("name").GetString() ?? string.Empty))
            .IsEquivalentTo(ToolingCapabilities.All.Select(capability => capability.Name));
        foreach (var item in root.GetProperty("contracts").EnumerateArray())
        {
            await Assert.That(item.GetProperty("maturity").ValueKind).IsEqualTo(JsonValueKind.String);
            await Assert.That(item.GetProperty("schemaVersion").GetString()).IsEqualTo(ToolingContracts.CurrentSchemaVersion);
        }

        // The report is deserializable by a client that models the wire contract.
        var parsed = JsonSerializer.Deserialize<ToolingCapabilitiesReport>(json, WireOptions);
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Project.Resolved).IsFalse();
        await Assert.That(parsed.Capabilities.Count).IsEqualTo(ToolingCapabilities.All.Count);
        await Assert.That(parsed.Contracts.Count).IsEqualTo(ToolingContracts.All.Count);
        await Assert.That(ToolingCompatibility.CheckSchemaVersion(parsed.SchemaVersion)).IsEqualTo(parsed.SchemaVersion);
    }

    [Test]
    public async Task FailureReportCarriesTheError()
    {
        var report = ToolingCapabilities.CreateReport(
            new ToolingIdentityInfo("LithoSharp.Tool", "1.0.0"),
            new ToolingIdentityInfo("LithoSharp", "1.0.0"),
            error: "Unsupported format 'xml'.");
        var root = JsonDocument.Parse(JsonSerializer.Serialize(report, WireOptions)).RootElement;

        await Assert.That(root.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(root.GetProperty("exitCode").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("error").GetString()).IsEqualTo("Unsupported format 'xml'.");
    }

    [Test]
    public async Task OldConsumerIgnoresAdditiveFieldsAndCapabilities()
    {
        var report = ToolingCapabilities.CreateReport(
            new ToolingIdentityInfo("LithoSharp.Tool", "1.0.0"),
            new ToolingIdentityInfo("LithoSharp", "1.0.0"));
        var node = JsonNode.Parse(JsonSerializer.Serialize(report, WireOptions))!.AsObject();
        node["futureSection"] = JsonValue.Create(1);
        node["capabilities"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "future-capability",
            ["maturity"] = "Experimental",
            ["schemaVersion"] = "1.1",
            ["description"] = "Added after 1.0.",
            ["scope"] = new JsonArray("future-stage"),
        });

        var consumer = JsonSerializer.Deserialize<LegacyCapabilityReport>(node.ToJsonString(), WireOptions);
        await Assert.That(consumer).IsNotNull();
        await Assert.That(consumer!.SchemaVersion).IsEqualTo("1.0");
        await Assert.That(consumer.Success).IsTrue();
        await Assert.That(consumer.Contracts!.Length).IsEqualTo(ToolingContracts.All.Count);
        await Assert.That(consumer.Capabilities!.Length).IsEqualTo(ToolingCapabilities.All.Count + 1);

        // The 1.0 client only requires capabilities it knows and ignores the unknown one.
        var advertised = consumer.Capabilities.Select(capability => capability.Name ?? string.Empty);
        await Assert.That(ToolingCapabilities.Missing(advertised, [ToolingCapabilities.SourceRouteLookup])).IsEmpty();
    }

    [Test]
    public async Task NewClientRefusesSafelyWhenCapabilitiesAreAbsent()
    {
        // A 1.0-era CLI answers an unknown command with the failure envelope instead of a report.
        const string envelope = """
            {"schemaVersion":"1.0","success":false,"exitCode":2,"error":"Unknown command 'capabilities'."}
            """;
        var response = JsonSerializer.Deserialize<LegacyCapabilityReport>(envelope, WireOptions);
        await Assert.That(response).IsNotNull();
        await Assert.That(response!.Success).IsFalse();
        await Assert.That(response.ExitCode).IsEqualTo(2);
        await Assert.That(response.Capabilities).IsNull();

        var advertised = response.Capabilities?.Select(capability => capability.Name ?? string.Empty) ?? [];
        var missing = ToolingCapabilities.Missing(advertised, [ToolingCapabilities.DocumentInspection]);
        await Assert.That(missing).IsEquivalentTo([ToolingCapabilities.DocumentInspection]);

        var message = string.Empty;
        try
        {
            ToolingCapabilities.Require(advertised, [ToolingCapabilities.DocumentInspection]);
        }
        catch (InvalidOperationException exception)
        {
            message = exception.Message;
        }
        await Assert.That(message).Contains("document-inspection");
    }

    private sealed record LegacyCapabilityContract(string? Name, string? Maturity, string? SchemaVersion);

    /// <summary>A 1.0-era consumer model: it knows only the original fields and ignores the rest.</summary>
    private sealed record LegacyCapabilityReport(
        string? SchemaVersion,
        bool Success,
        int ExitCode,
        LegacyCapabilityContract[]? Contracts,
        LegacyCapabilityContract[]? Capabilities);
}
