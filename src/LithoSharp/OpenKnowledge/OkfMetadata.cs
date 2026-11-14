using System.Globalization;
using System.Text.RegularExpressions;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

public enum OkfTrustTier { Unverified, NoRecords, Indeterminate, MachineConfirmed, HumanReviewed }
public enum OkfFreshness { NoDeadline, Fresh, Stale, Invalid }
public enum OkfDiagnosticCategory { Structure, Quality, Unsupported, ResourceLimit, Io, Canceled, PublicationPolicy }
public enum OkfConformance { Conformant, NonConformant, NotAssessed }

/// <summary>A recorded assertion; an actor name does not establish identity or truth.</summary>
public sealed class OkfVerificationEvent
{
    internal OkfVerificationEvent(YamlNode raw, string? actor, DateTimeOffset? at, bool timestampIsValid)
        => (Raw, Actor, At, IsValid) = (raw, actor, at, !string.IsNullOrWhiteSpace(actor) && timestampIsValid);
    public YamlNode Raw { get; }
    public string? Actor { get; }
    /// <summary>The exact DateTimeOffset projection, when representable; Raw retains the original timestamp precision.</summary>
    public DateTimeOffset? At { get; }
    public bool IsValid { get; }
}

public sealed class OkfSource
{
    internal OkfSource(YamlNode raw, YamlNode? commonUsageWindow)
    {
        Raw = raw;
        Id = OkfMetadata.String(OkfMetadata.Field(raw, "id"));
        Resource = OkfMetadata.String(OkfMetadata.Field(raw, "resource"));
        Title = OkfMetadata.String(OkfMetadata.Field(raw, "title"));
        UsageWindow = OkfMetadata.Field(raw, "usage_window") ?? commonUsageWindow;
    }
    public YamlNode Raw { get; }
    public string? Id { get; }
    public string? Resource { get; }
    public string? Title { get; }
    public YamlNode? UsageWindow { get; }
}

/// <summary>Noncoercing known views over the original ordered YAML tree.</summary>
public sealed class OkfMetadata
{
    internal OkfMetadata(YamlNode raw, DateTimeOffset asOf, Action<string, string, YamlNode?> warn)
    {
        Raw = raw;
        Type = String(GetField("type"));
        Title = OptionalString("title");
        Description = OptionalString("description");
        Resource = OptionalString("resource");
        Status = GetField("status") is null ? "stable" : OptionalString("status");
        if (Status is not ("stable" or "draft" or "deprecated"))
            warn("OKF110", "Unknown or invalid lifecycle status was retained.", GetField("status"));
        var tags = GetField("tags");
        Tags = Array.AsReadOnly(tags?.Kind == MarkdownYamlKind.Sequence
            ? tags.Children.Select(String).Where(static value => value is not null).Select(static value => value!).ToArray() : []);
        if (tags is not null && (tags.Kind != MarkdownYamlKind.Sequence || tags.Children.Any(static item => String(item) is null)))
            warn("OKF111", "tags should be a sequence of strings; the original value was retained.", tags);

        var sources = GetField("sources");
        Sources = Array.AsReadOnly(sources?.Kind == MarkdownYamlKind.Sequence
            ? sources.Children.Select(node => new OkfSource(node, UsageWindow)).ToArray() : []);
        if (sources is not null && sources.Kind != MarkdownYamlKind.Sequence)
            warn("OKF112", "sources should be a sequence of mappings.", sources);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (source.Raw.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(source.Resource))
                warn("OKF112", "A sources entry should contain a nonempty string resource; scope descriptions are allowed.", source.Raw);
            if (source.Id is not null && !sourceIds.Add(source.Id))
                warn("OKF113", $"Source id '{source.Id}' is repeated; claim attribution is ambiguous.", source.Raw);
            CheckTimestamp(Field(source.Raw, "last_modified"), "sources.last_modified");
            CheckWindow(Field(source.Raw, "usage_window"));
        }
        CheckWindow(UsageWindow);

        var generated = Generated;
        if (generated is not null)
        {
            if (generated.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(String(Field(generated, "by"))))
                warn("OKF114", "generated should be a mapping with a nonempty string by actor.", generated);
            GeneratedAt = CheckTimestamp(Field(generated, "at"), "generated.at")?.Value;
        }
        else
            LegacyTimestamp = CheckTimestamp(GetField("timestamp"), "timestamp")?.Value;

        var verified = GetField("verified");
        var events = new List<OkfVerificationEvent>();
        if (verified is not null)
        {
            IReadOnlyList<YamlNode> nodes = verified.Kind == MarkdownYamlKind.Mapping ? new[] { verified }
                : verified.Kind == MarkdownYamlKind.Sequence ? verified.Children : [];
            if (verified.Kind is not (MarkdownYamlKind.Mapping or MarkdownYamlKind.Sequence))
                warn("OKF115", "verified should be one event mapping or a sequence of event mappings.", verified);
            foreach (var node in nodes)
            {
                var actor = String(Field(node, "by"));
                var at = CheckTimestamp(Field(node, "at"), "verified.at");
                var item = new OkfVerificationEvent(node, actor, at?.Value, at is not null);
                events.Add(item);
                if (!item.IsValid)
                    warn("OKF115", "A verification event should contain a nonempty string by actor and an offset datetime at.", node);
            }
        }
        VerificationEvents = Array.AsReadOnly(events.ToArray());
        VerificationIsComplete = verified is null || (verified.Kind is MarkdownYamlKind.Mapping or MarkdownYamlKind.Sequence)
            && events.All(static item => item.IsValid);
        var valid = events.Where(static item => item.IsValid).ToArray();
        TrustTier = verified is null ? OkfTrustTier.Unverified
            : verified.Kind == MarkdownYamlKind.Sequence && events.Count == 0 ? OkfTrustTier.NoRecords
            : valid.Length == 0 ? OkfTrustTier.Indeterminate
            : valid.Any(static item => item.Actor!.StartsWith("human:", StringComparison.Ordinal)) ? OkfTrustTier.HumanReviewed
            : OkfTrustTier.MachineConfirmed;

        var stale = GetField("stale_after");
        var deadline = CheckTimestamp(stale, "stale_after");
        StaleAfter = deadline?.Value;
        Freshness = stale is null ? OkfFreshness.NoDeadline : deadline is null ? OkfFreshness.Invalid
            : deadline.CompareTo(OffsetTimestamp.From(asOf)) <= 0 ? OkfFreshness.Stale : OkfFreshness.Fresh;
        if (Type == "Attested Computation" && string.IsNullOrWhiteSpace(String(Runtime)))
            warn("OKF116", "An Attested Computation should declare a nonempty string runtime.", Runtime ?? raw);
        foreach (var key in new[] { "executor", "attester" })
        {
            var node = GetField(key);
            if (node is not null && (node.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(String(Field(node, "resource")))))
                warn("OKF116", $"{key} should be a mapping with a nonempty string resource.", node);
        }

        string? OptionalString(string key)
        {
            var node = GetField(key);
            var value = String(node);
            if (node is not null && value is null)
                warn("OKF117", $"{key} should be a string; no scalar coercion was applied.", node);
            return value;
        }
        OffsetTimestamp? CheckTimestamp(YamlNode? node, string key)
        {
            if (node is null)
                return null;
            if (TryReadTimestamp(String(node), out var value))
            {
                if (value!.Value is null)
                    warn("OKF121", $"{key} has sub-tick precision; its exact raw value is retained and its DateTimeOffset projection is unavailable.", node);
                return value;
            }
            warn("OKF118", $"{key} is not an ISO 8601 datetime with an explicit offset.", node);
            return null;
        }
        void CheckWindow(YamlNode? node)
        {
            if (node is null)
                return;
            if (node.Kind != MarkdownYamlKind.Mapping)
            {
                warn("OKF119", "usage_window should be a from/to datetime mapping.", node);
                return;
            }
            var from = CheckTimestamp(Field(node, "from"), "usage_window.from");
            var to = CheckTimestamp(Field(node, "to"), "usage_window.to");
            if (Field(node, "from") is null || Field(node, "to") is null || from is not null && to is not null && from.CompareTo(to) > 0)
                warn("OKF119", "usage_window should contain an ordered from/to datetime range.", node);
        }
    }

    public YamlNode Raw { get; }
    public string? Type { get; }
    public string? Title { get; }
    public string? Description { get; }
    public string? Resource { get; }
    public IReadOnlyList<string> Tags { get; }
    public string? Status { get; }
    public IReadOnlyList<OkfSource> Sources { get; }
    public YamlNode? UsageWindow => GetField("usage_window");
    public YamlNode? Generated => GetField("generated");
    public string? GeneratedBy => String(Field(Generated, "by"));
    public DateTimeOffset? GeneratedAt { get; }
    public DateTimeOffset? LegacyTimestamp { get; }
    public DateTimeOffset? DisplayChangedAt => Generated is null ? LegacyTimestamp : GeneratedAt;
    public IReadOnlyList<OkfVerificationEvent> VerificationEvents { get; }
    public bool VerificationIsComplete { get; }
    public OkfTrustTier TrustTier { get; }
    /// <summary>The exact DateTimeOffset projection when representable. Freshness compares the full original precision.</summary>
    public DateTimeOffset? StaleAfter { get; }
    public OkfFreshness Freshness { get; }
    public YamlNode? Runtime => GetField("runtime");
    public YamlNode? Parameters => GetField("parameters");
    public YamlNode? Computation => GetField("computation");
    public YamlNode? Executor => GetField("executor");
    public YamlNode? Attester => GetField("attester");
    public YamlNode? GetField(string name) => Field(Raw, name);

    internal static YamlNode? Field(YamlNode? mapping, string name) => mapping?.Kind == MarkdownYamlKind.Mapping
        ? mapping.Entries.FirstOrDefault(entry => String(entry.Key) == name)?.Value : null;
    internal static string? String(YamlNode? node) => node is { Kind: MarkdownYamlKind.Scalar, ScalarKind: YamlScalarKind.String }
        ? node.Value : null;

    public static bool TryParseOffsetDateTime(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!TryReadTimestamp(text, out var timestamp) || timestamp!.Value is not { } projected)
            return false;
        value = projected;
        return true;
    }

    private static bool TryReadTimestamp(string? text, out OffsetTimestamp? value)
    {
        value = null;
        if (text is null)
            return false;
        var match = Regex.Match(text,
            @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?<fraction>[.,]\d+)?)?(?:Z|[+-]\d{2}(?::?\d{2})?)\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success)
            return false;
        var fraction = match.Groups["fraction"];
        var whole = fraction.Success ? text.Remove(fraction.Index, fraction.Length) : text;
        if (!DateTimeOffset.TryParse(whole, CultureInfo.InvariantCulture, DateTimeStyles.None, out var second))
            return false;
        value = new OffsetTimestamp(second, fraction.Success ? fraction.Value[1..] : string.Empty);
        return true;
    }

    private sealed record OffsetTimestamp(DateTimeOffset WholeSecond, string Fraction)
    {
        internal DateTimeOffset? Value
        {
            get
            {
                if (Fraction.Length > 7 && Fraction.AsSpan(7).IndexOfAnyExcept('0') >= 0)
                    return null;
                var ticks = int.Parse(Fraction[..Math.Min(7, Fraction.Length)].PadRight(7, '0'), CultureInfo.InvariantCulture);
                return WholeSecond.AddTicks(ticks);
            }
        }

        internal static OffsetTimestamp From(DateTimeOffset value) =>
            new(new DateTimeOffset(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Offset),
                (value.Ticks % TimeSpan.TicksPerSecond).ToString("D7", CultureInfo.InvariantCulture));

        internal int CompareTo(OffsetTimestamp other)
        {
            var whole = WholeSecond.CompareTo(other.WholeSecond);
            if (whole != 0)
                return whole;
            for (var index = 0; index < Math.Max(Fraction.Length, other.Fraction.Length); index++)
            {
                var left = index < Fraction.Length ? Fraction[index] : '0';
                var right = index < other.Fraction.Length ? other.Fraction[index] : '0';
                if (left != right)
                    return left.CompareTo(right);
            }
            return 0;
        }
    }
}
