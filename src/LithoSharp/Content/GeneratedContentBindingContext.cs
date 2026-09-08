using System.Collections;
using System.Globalization;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

/// <summary>Converts an unwrapped input value without discovering members through reflection.</summary>
public delegate bool GeneratedContentValueParser<T>(object input, GeneratedContentBindingContext context, out T value);

/// <summary>Shared validation and scalar conversion used by generated front matter binders.</summary>
public sealed class GeneratedContentBindingContext
{
    private readonly List<SiteDiagnostic> diagnostics;
    private readonly HashSet<string> knownNames = new(StringComparer.Ordinal);
    private readonly SiteSourceLocation? location;
    private readonly string fieldPath;
    private readonly bool rejectUnknownFields;

    /// <summary>Starts a binding operation over parsed front matter.</summary>
    /// <exception cref="ArgumentNullException">The values are null.</exception>
    public GeneratedContentBindingContext(IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? sourceLocation = null, bool rejectUnknownFields = true)
        : this(values, sourceLocation, string.Empty, rejectUnknownFields, [])
    {
    }

    private GeneratedContentBindingContext(IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? location, string fieldPath, bool rejectUnknownFields, List<SiteDiagnostic> diagnostics)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        this.location = location;
        this.fieldPath = fieldPath;
        this.rejectUnknownFields = rejectUnknownFields;
        this.diagnostics = diagnostics;
    }

    /// <summary>The supplied values, in their original enumeration order.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>Converts a present field; absent fields retain their current value.</summary>
    /// <remarks>Call ValidateRequired after all assignments, so setter side effects and defaults match reflection binding.</remarks>
    /// <exception cref="ArgumentNullException">The field name or parser is null.</exception>
    public bool TryRead<T>(string yamlName, T currentDefault, bool required, bool allowsNull,
        GeneratedContentValueParser<T> parser, out T value)
    {
        ArgumentNullException.ThrowIfNull(yamlName);
        ArgumentNullException.ThrowIfNull(parser);
        knownNames.Add(yamlName);
        if (!Values.TryGetValue(yamlName, out var input))
        {
            value = currentDefault;
            return false;
        }
        var child = At(yamlName, ValueLocation(yamlName));
        return child.ConvertValue(input, allowsNull, parser, out value);
    }

    /// <summary>Checks an absent member after all supplied fields have been assigned.</summary>
    /// <exception cref="ArgumentNullException">The field name is null.</exception>
    public void ValidateRequired<T>(string yamlName, T currentValue, bool required, bool allowsNull)
    {
        ArgumentNullException.ThrowIfNull(yamlName);
        knownNames.Add(yamlName);
        if (!Values.ContainsKey(yamlName) && (required || (currentValue is null && !allowsNull)))
        {
            Add(ContentFrontMatterDiagnosticIds.MissingRequiredField,
                $"Required front matter field '{yamlName}' is missing.", location);
        }
    }

    /// <summary>Records an expected validation exception from a generated property assignment.</summary>
    /// <exception cref="ArgumentNullException">The field name or exception is null.</exception>
    public void Reject(string yamlName, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(yamlName);
        ArgumentNullException.ThrowIfNull(failure);
        Add(ContentFrontMatterDiagnosticIds.InvalidValue,
            $"Front matter field '{yamlName}' was rejected: {failure.Message}", ValueLocation(yamlName));
    }

    /// <summary>Reports input names not recognized by TryRead or ValidateRequired.</summary>
    public void ValidateUnknownFields()
    {
        if (!rejectUnknownFields) return;
        foreach (var name in Values.Keys.Where(name => !knownNames.Contains(name)))
        {
            Add(ContentFrontMatterDiagnosticIds.UnknownField, $"Unknown front matter field '{name}'.",
                Values is LocatedYamlMapping mapping ? mapping.GetKeyLocation(name) : location);
        }
    }

    /// <summary>Returns the bound value or the accumulated diagnostics.</summary>
    /// <exception cref="ArgumentNullException">The instance is null.</exception>
    public ContentParseResult<T> Complete<T>(T instance) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(instance);
        return diagnostics.Count == 0 ? ContentParseResult<T>.Success(instance) : ContentParseResult<T>.Failure(diagnostics);
    }

    /// <summary>Binds a nested mapping using generated member assignments.</summary>
    /// <exception cref="ArgumentNullException">The input or binder is null.</exception>
    public bool TryObject<T>(object input, Func<GeneratedContentBindingContext, T> bind, out T value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(bind);
        if (input is T existing)
        {
            value = existing;
            return true;
        }
        if (input is not IReadOnlyDictionary<string, object?> mapping)
        {
            value = default!;
            return Invalid<T>();
        }
        value = bind(new GeneratedContentBindingContext(mapping, location, string.Empty, rejectUnknownFields, diagnostics));
        return true;
    }

    /// <summary>Converts a sequence to a typed list while retaining per-item locations.</summary>
    /// <exception cref="ArgumentNullException">The input or parser is null.</exception>
    public bool TryList<T>(object input, bool elementAllowsNull, GeneratedContentValueParser<T> parser, out List<T> value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parser);
        value = [];
        if (input is not IEnumerable sequence || input is string || input is IReadOnlyDictionary<string, object?>)
            return false;
        var success = true;
        foreach (var item in sequence)
        {
            var child = At($"{fieldPath}[{value.Count}]", location);
            success &= child.ConvertValue(item, elementAllowsNull, parser, out var converted);
            value.Add(converted);
        }
        return success;
    }

    /// <summary>Converts a sequence to a typed array.</summary>
    /// <exception cref="ArgumentNullException">The input or parser is null.</exception>
    public bool TryArray<T>(object input, bool elementAllowsNull, GeneratedContentValueParser<T> parser, out T[] value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parser);
        if (input is T[] existing)
        {
            value = existing;
            return true;
        }
        if (input is not IEnumerable || input is string || input is IReadOnlyDictionary<string, object?>)
        {
            value = [];
            return Invalid<T[]>();
        }
        var success = TryList(input, elementAllowsNull, parser, out var list);
        value = list.ToArray();
        return success;
    }

    /// <summary>Converts a mapping to a typed string-keyed dictionary.</summary>
    /// <exception cref="ArgumentNullException">The input or parser is null.</exception>
    public bool TryDictionary<T>(object input, bool valueAllowsNull, GeneratedContentValueParser<T> parser,
        out Dictionary<string, T> value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parser);
        if (input is Dictionary<string, T> existing)
        {
            value = existing;
            return true;
        }
        value = new(StringComparer.Ordinal);
        if (input is not IReadOnlyDictionary<string, object?> mapping) return false;
        var success = true;
        foreach (var pair in mapping)
        {
            var child = At($"{fieldPath}.{pair.Key}",
                mapping is LocatedYamlMapping located ? located.GetValueLocation(pair.Key) : location);
            if (child.ConvertValue(pair.Value, valueAllowsNull, parser, out var converted)) value.Add(pair.Key, converted);
            else success = false;
        }
        return success;
    }

    /// <summary>Converts a scalar using the same invariant-culture rules as reflection binding.</summary>
    /// <exception cref="ArgumentNullException">The input or context is null.</exception>
    public static bool TryParseScalar<T>(object input, GeneratedContentBindingContext context, out T value)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (typeof(T) == typeof(object))
        {
            value = (T)ContentScalarConversion.UnwrapTree(input)!;
            return true;
        }
        if (input is T existing)
        {
            value = existing;
            return true;
        }
        if (ContentScalarConversion.TryConvertScalar(input, typeof(T), out var converted))
        {
            value = (T)converted!;
            return true;
        }
        value = default!;
        return context.Invalid<T>();
    }

    /// <summary>Converts an enumeration without runtime type discovery.</summary>
    /// <exception cref="ArgumentNullException">The input or context is null.</exception>
    public static bool TryParseEnum<T>(object input, GeneratedContentBindingContext context, out T value) where T : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (input is T existing)
        {
            value = existing;
            return true;
        }
        return Enum.TryParse(input as string ?? System.Convert.ToString(input, CultureInfo.InvariantCulture),
            ignoreCase: false, out value) || context.Invalid<T>();
    }

    private bool ConvertValue<T>(object? input, bool allowsNull, GeneratedContentValueParser<T> parser, out T value)
    {
        var inputLocation = LocatedYamlValue.GetLocation(input) ?? location;
        input = LocatedYamlValue.Unwrap(input);
        if (input is null)
        {
            value = default!;
            if (allowsNull) return true;
            Add(ContentFrontMatterDiagnosticIds.NullNotAllowed,
                $"Front matter field '{fieldPath}' does not allow null.", inputLocation);
            return false;
        }
        var before = diagnostics.Count;
        var child = At(fieldPath, inputLocation);
        if (parser(input, child, out value)) return true;
        if (diagnostics.Count == before) child.Invalid<T>();
        return false;
    }

    private GeneratedContentBindingContext At(string path, SiteSourceLocation? sourceLocation) =>
        new(Values, sourceLocation, path, rejectUnknownFields, diagnostics);

    private SiteSourceLocation? ValueLocation(string name) =>
        Values is LocatedYamlMapping mapping ? mapping.GetValueLocation(name) : location;

    private bool Invalid<T>()
    {
        Add(ContentFrontMatterDiagnosticIds.InvalidValue,
            $"Front matter field '{fieldPath}' cannot be converted to '{typeof(T).Name}'.", location);
        return false;
    }

    private void Add(string id, string message, SiteSourceLocation? sourceLocation) =>
        diagnostics.Add(new SiteDiagnostic(id, SiteDiagnosticSeverity.Error, message, sourceLocation));
}
