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
    internal OkfVerificationEvent(YamlNode raw, string? actor, DateTimeOffset? at, bool timestampIsValid, bool isCurrent)
    {
        (Raw, Actor, At, IsValid) = (raw, actor, at, !string.IsNullOrWhiteSpace(actor) && timestampIsValid);
        IsCurrent = IsValid && isCurrent;
    }
    public YamlNode Raw { get; }
    public string? Actor { get; }
    /// <summary>The exact DateTimeOffset projection, when representable; Raw retains the original timestamp precision.</summary>
    public DateTimeOffset? At { get; }
    public bool IsValid { get; }
    public bool IsCurrent { get; }
}

public sealed class OkfSource
{
    internal OkfSource(YamlNode raw, YamlNode? commonUsageWindow, bool isVersion03)
    {
        Raw = raw;
        Id = OkfMetadata.String(OkfMetadata.Field(raw, "id"));
        Resource = OkfMetadata.String(OkfMetadata.Field(raw, "resource"));
        Title = OkfMetadata.String(OkfMetadata.Field(raw, "title"));
        var ownWindow = OkfMetadata.Field(raw, "usage_window");
        UsageWindow = ownWindow is null || isVersion03 && OkfMetadata.IsAbsent(ownWindow) ? commonUsageWindow : ownWindow;
    }
    public YamlNode Raw { get; }
    public string? Id { get; }
    public string? Resource { get; }
    public string? Title { get; }
    public YamlNode? UsageWindow { get; }
}

/// <summary>A usable declared parameter. Raw retains its full YAML mapping.</summary>
public sealed record OkfParameter(YamlNode Raw, string Name, string Type, bool Required);

/// <summary>Noncoercing known views over the original ordered YAML tree.</summary>
public sealed class OkfMetadata
{
    internal OkfMetadata(YamlNode raw, DateTimeOffset asOf, Action<string, string, YamlNode?> warn, bool isVersion03 = false)
    {
        Raw = raw;
        Type = String(GetField("type"));
        Title = OptionalString("title");
        Description = OptionalString("description");
        Resource = OptionalString("resource");
        Status = KnownField("status") is null ? "stable" : OptionalString("status");
        if (Status is not ("stable" or "draft" or "deprecated"))
            warn("OKF110", "Unknown or invalid lifecycle status was retained.", GetField("status"));
        if (isVersion03)
            SupersededBy = Status == "deprecated" ? OptionalString("superseded_by") : null;
        var tags = KnownField("tags");
        var tagItems = Items(tags, isVersion03);
        Tags = Array.AsReadOnly(tagItems.Where(item => !isVersion03 || !IsAbsent(item)).Select(String).OfType<string>().ToArray());
        if (tags is not null && (!isVersion03 && tags.Kind != MarkdownYamlKind.Sequence
            || tagItems.Any(item => (!isVersion03 || !IsAbsent(item)) && String(item) is null)))
            warn("OKF111", isVersion03 ? "tags should contain strings; the original value was retained."
                : "tags should be a sequence of strings; the original value was retained.", tags);

        var sources = KnownField("sources");
        var sourceItems = new List<OkfSource>();
        if (!isVersion03 && sources is not null && sources.Kind != MarkdownYamlKind.Sequence)
            warn("OKF112", "sources should be a sequence of mappings.", sources);
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Items(sources, isVersion03))
        {
            if (isVersion03 && IsAbsent(node))
                continue;
            var source = new OkfSource(node, isVersion03 ? KnownField("usage_window") : UsageWindow, isVersion03);
            if (source.Raw.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(source.Resource))
            {
                warn("OKF112", "A sources entry should contain a nonempty string resource; scope descriptions are allowed.", source.Raw);
                if (isVersion03)
                    continue;
            }
            sourceItems.Add(source);
            if (source.Id is not null && !sourceIds.Add(source.Id))
                warn("OKF113", $"Source id '{source.Id}' is repeated; claim attribution is ambiguous.", source.Raw);
            CheckTimestamp(Field(source.Raw, "last_modified"), "sources.last_modified");
            CheckWindow(Field(source.Raw, "usage_window"));
        }
        Sources = Array.AsReadOnly(sourceItems.ToArray());
        CheckWindow(UsageWindow);

        var generated = KnownField("generated");
        OffsetTimestamp? generatedAt = null;
        var generationTimeIsUnknown = false;
        if (generated is not null)
        {
            var hasGenerator = !string.IsNullOrWhiteSpace(String(Field(generated, "by")));
            if (!hasGenerator)
                warn("OKF114", "generated should be a mapping with a nonempty string by actor.", generated);
            var generationTime = Field(generated, "at");
            if (!isVersion03 || hasGenerator)
            {
                generatedAt = CheckTimestamp(generationTime, "generated.at");
                GeneratedAt = generatedAt?.Value;
                generationTimeIsUnknown = isVersion03 && generatedAt is null;
            }
        }
        else if (!isVersion03)
            LegacyTimestamp = CheckTimestamp(GetField("timestamp"), "timestamp")?.Value;

        var verified = KnownField("verified");
        var events = new List<OkfVerificationEvent>();
        if (verified is not null)
        {
            IReadOnlyList<YamlNode> nodes = verified.Kind == MarkdownYamlKind.Mapping ? new[] { verified }
                : verified.Kind == MarkdownYamlKind.Sequence ? verified.Children : [];
            if (verified.Kind is not (MarkdownYamlKind.Mapping or MarkdownYamlKind.Sequence))
                warn("OKF115", "verified should be one event mapping or a sequence of event mappings.", verified);
            foreach (var node in nodes)
            {
                if (isVersion03 && IsAbsent(node))
                    continue;
                var actor = String(Field(node, "by"));
                var at = CheckTimestamp(Field(node, "at"), "verified.at");
                var item = new OkfVerificationEvent(node, actor, at?.Value, at is not null,
                    !isVersion03 || !generationTimeIsUnknown && (generatedAt is null || at?.CompareTo(generatedAt) >= 0));
                events.Add(item);
                if (!item.IsValid)
                    warn("OKF115", isVersion03 ? "A verification event should contain a nonempty string by actor and an RFC 3339 at."
                        : "A verification event should contain a nonempty string by actor and an offset datetime at.", node);
            }
        }
        VerificationEvents = Array.AsReadOnly(events.ToArray());
        VerificationIsComplete = verified is null || (verified.Kind is MarkdownYamlKind.Mapping or MarkdownYamlKind.Sequence)
            && events.All(static item => item.IsValid);
        var valid = events.Where(static item => item.IsCurrent).ToArray();
        TrustTier = verified is null || isVersion03 && valid.Length == 0 ? OkfTrustTier.Unverified
            : verified.Kind == MarkdownYamlKind.Sequence && events.Count == 0 ? OkfTrustTier.NoRecords
            : valid.Length == 0 ? OkfTrustTier.Indeterminate
            : valid.Any(item => item.Actor!.StartsWith("human:", StringComparison.Ordinal)
                && (!isVersion03 || !item.Actor.AsSpan(6).IsWhiteSpace())) ? OkfTrustTier.HumanReviewed
            : OkfTrustTier.MachineConfirmed;

        var stale = KnownField("stale_after");
        var deadline = CheckTimestamp(stale, "stale_after");
        StaleAfter = deadline?.Value;
        Freshness = stale is null ? OkfFreshness.NoDeadline : deadline is null ? OkfFreshness.Invalid
            : deadline.CompareTo(OffsetTimestamp.From(asOf)) <= 0 ? OkfFreshness.Stale : OkfFreshness.Fresh;
        if (Type == "Attested Computation" && string.IsNullOrWhiteSpace(String(Runtime)))
            warn("OKF116", "An Attested Computation should declare a nonempty string runtime.", Runtime ?? raw);
        if (isVersion03)
        {
            var parameterItems = new List<OkfParameter>();
            foreach (var node in Items(KnownField("parameters"), acceptSingle: true))
            {
                if (IsAbsent(node))
                    continue;
                var name = String(Field(node, "name"));
                var type = String(Field(node, "type"));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
                {
                    warn("OKF116", "A parameters entry should contain nonempty string name and type.", node);
                    continue;
                }
                var required = Field(node, "required");
                if (!IsAbsent(required) && required?.ScalarKind != YamlScalarKind.Boolean)
                    warn("OKF116", "A parameters entry should use a boolean required value.", required);
                parameterItems.Add(new OkfParameter(node, name, type,
                    required is { Kind: MarkdownYamlKind.Scalar, ScalarKind: YamlScalarKind.Boolean }
                    && string.Equals(required.Value, "true", StringComparison.OrdinalIgnoreCase)));
            }
            ParameterEntries = Array.AsReadOnly(parameterItems.ToArray());
        }
        foreach (var key in new[] { "executor", "attester" })
        {
            var node = KnownField(key);
            if (node is not null && (node.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(String(Field(node, "resource")))))
                warn("OKF116", $"{key} should be a mapping with a nonempty string resource.", node);
        }

        YamlNode? KnownField(string key)
        {
            var node = GetField(key);
            return isVersion03 && IsAbsent(node) ? null : node;
        }
        string? OptionalString(string key)
        {
            var node = KnownField(key);
            var value = String(node);
            if (node is not null && value is null)
                warn("OKF117", $"{key} should be a string; no scalar coercion was applied.", node);
            return value;
        }
        OffsetTimestamp? CheckTimestamp(YamlNode? node, string key)
        {
            if (node is null || isVersion03 && IsAbsent(node))
                return null;
            if (isVersion03 ? TryReadVersion03Timestamp(String(node), out var value) : TryReadTimestamp(String(node), out value))
            {
                if (!isVersion03 && value!.Value is null)
                    warn("OKF121", $"{key} has sub-tick precision; its exact raw value is retained and its DateTimeOffset projection is unavailable.", node);
                return value;
            }
            warn("OKF118", isVersion03 ? $"{key} is not an RFC 3339 date or datetime with an explicit offset."
                : $"{key} is not an ISO 8601 datetime with an explicit offset.", node);
            return null;
        }
        void CheckWindow(YamlNode? node)
        {
            if (node is null || isVersion03 && IsAbsent(node))
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
    public string? SupersededBy { get; }
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
    public IReadOnlyList<OkfParameter> ParameterEntries { get; } = Array.Empty<OkfParameter>();
    public YamlNode? Computation => GetField("computation");
    public YamlNode? Executor => GetField("executor");
    public YamlNode? Attester => GetField("attester");
    public YamlNode? GetField(string name) => Field(Raw, name);

    internal static YamlNode? Field(YamlNode? mapping, string name) => mapping?.Kind == MarkdownYamlKind.Mapping
        ? mapping.Entries.FirstOrDefault(entry => String(entry.Key) == name)?.Value : null;
    internal static string? String(YamlNode? node) => node is { Kind: MarkdownYamlKind.Scalar, ScalarKind: YamlScalarKind.String }
        ? node.Value : null;

    internal static bool IsAbsent(YamlNode? node) => node is null
        or { Kind: MarkdownYamlKind.Scalar, ScalarKind: YamlScalarKind.Null }
        or { Kind: MarkdownYamlKind.Scalar, ScalarKind: YamlScalarKind.String, Value: "" }
        or { Kind: MarkdownYamlKind.Sequence, Children.Count: 0 };

    private static IReadOnlyList<YamlNode> Items(YamlNode? node, bool acceptSingle) =>
        node?.Kind == MarkdownYamlKind.Sequence ? node.Children : acceptSingle && node is not null ? [node] : [];

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

    private static bool TryReadVersion03Timestamp(string? text, out OffsetTimestamp? value)
    {
        value = null;
        if (text is null)
            return false;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            value = new OffsetTimestamp(new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "");
            return true;
        }
        var match = Regex.Match(text,
            @"\A(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})[Tt](?<hour>[0-9]{2}):(?<minute>[0-9]{2}):(?<second>[0-9]{2})(?:\.(?<fraction>[0-9]+))?(?<offset>[Zz]|[+-][0-9]{2}:[0-9]{2})\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            return false;
        var hour = int.Parse(match.Groups["hour"].ValueSpan, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups["minute"].ValueSpan, CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups["second"].ValueSpan, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59 || second > 60)
            return false;
        var offset = match.Groups["offset"].ValueSpan;
        var offsetHour = offset.Length == 1 ? 0 : int.Parse(offset.Slice(1, 2), CultureInfo.InvariantCulture);
        var offsetMinute = offset.Length == 1 ? 0 : int.Parse(offset[4..], CultureInfo.InvariantCulture);
        if (offsetHour > 23 || offsetMinute > 59)
            return false;
        var offsetInMinutes = (offsetHour * 60 + offsetMinute) * (offset[0] == '-' ? -1 : 1);
        var local = day.ToDateTime(new TimeOnly(hour, minute, Math.Min(second, 59)));
        var utcTicks = local.Ticks - offsetInMinutes * TimeSpan.TicksPerMinute;
        if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            return false;
        var utc = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        if (second == 60 && (utc is not { Hour: 23, Minute: 59 } || utc.Day != DateTime.DaysInMonth(utc.Year, utc.Month)))
            return false;
        var wholeSecond = Math.Abs(offsetInMinutes) <= 14 * 60
            ? new DateTimeOffset(local, TimeSpan.FromMinutes(offsetInMinutes)) : utc;
        value = new OffsetTimestamp(wholeSecond, match.Groups["fraction"].Value, second == 60);
        return true;
    }

    private sealed record OffsetTimestamp(DateTimeOffset WholeSecond, string Fraction, bool IsLeapSecond = false)
    {
        internal DateTimeOffset? Value
        {
            get
            {
                if (IsLeapSecond || Fraction.Length > 7 && Fraction.AsSpan(7).IndexOfAnyExcept('0') >= 0)
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
            if (IsLeapSecond != other.IsLeapSecond)
                return IsLeapSecond.CompareTo(other.IsLeapSecond);
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
