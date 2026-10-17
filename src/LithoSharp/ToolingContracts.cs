namespace LithoSharp;

public enum ToolingContractMaturity
{
    /// <summary>Additive-only within 1.x; removals require deprecation and the next major version.</summary>
    Stable = 0,
    /// <summary>May change in a 1.x minor version; consumers must not assume availability.</summary>
    Experimental = 1,
}

public sealed class ToolingContract
{
    internal ToolingContract(string name, ToolingContractMaturity maturity, string schemaVersion, string description)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
    }

    public string Name { get; }

    public ToolingContractMaturity Maturity { get; }

    public string SchemaVersion { get; }

    public string Description { get; }
}

public sealed class ToolingCapability
{
    internal ToolingCapability(string name, ToolingContractMaturity maturity, string schemaVersion, string description,
        IReadOnlyList<string>? scope = null)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
        Scope = scope ?? [];
    }

    public string Name { get; }

    public ToolingContractMaturity Maturity { get; }

    public string SchemaVersion { get; }

    public string Description { get; }

    public IReadOnlyList<string> Scope { get; }
}

/// <summary>Ignore unknown fields and accept additive minor-schema changes; compatibility requires equal major versions.</summary>
public static class ToolingContracts
{
    /// <summary>All 1.x tooling contracts share this schema version today.</summary>
    public const string CurrentSchemaVersion = "1.0";

    public static ToolingContract DiagnosticCodes { get; } = new(
        "DiagnosticCodes", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Diagnostic codes and source positions from T01. Codes keep their meaning in 1.x.");

    public static ToolingContract JsonEnvelopes { get; } = new(
        "JsonEnvelopes", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Machine-readable CLI envelopes and exit codes from T04. Fields grow additively.");

    public static ToolingContract Inspection { get; } = new(
        "Inspection", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Document snapshots, workspace lifetime, front matter schemas and navigation from T02, T03 and T07, T08.");

    public static ToolingContract DevServer { get; } = new(
        "DevServer", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Machine control events with startup, rebuild and shutdown from T05. Event types grow additively.");

    public static ToolingContract MigrationReport { get; } = new(
        "MigrationReport", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Migration verdicts, route comparison scope, functional-change classifications, aggregates and fix candidates from T09/T20. Fields grow additively; fingerprint rules stay stable.");

    public static IReadOnlyList<ToolingContract> All { get; } =
        [DiagnosticCodes, JsonEnvelopes, Inspection, DevServer, MigrationReport];
}

public static class ToolingCompatibility
{
    public static bool IsCompatible(string actual, string supported = ToolingContracts.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(supported);
        return TryMajor(actual, out var actualMajor) && TryMajor(supported, out var supportedMajor)
            && actualMajor == supportedMajor;
    }

    /// <summary>Returns the original version when compatible; malformed or different-major versions throw InvalidOperationException.</summary>
    public static string CheckSchemaVersion(string actual, string supported = ToolingContracts.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(supported);
        if (!IsCompatible(actual, supported))
        {
            throw new InvalidOperationException(
                $"Unsupported tooling schema version '{actual}'; this client supports schema version '{supported}'.");
        }
        return actual;
    }

    private static bool TryMajor(string version, out int major)
    {
        major = 0;
        if (version.Length == 0 || version.Any(character => !char.IsAsciiDigit(character) && character != '.'))
            return false;
        var dot = version.IndexOf('.');
        if (dot < 0) return int.TryParse(version, out major);
        if (!Version.TryParse(version, out var parsed)) return false;
        major = parsed.Major;
        return true;
    }
}
