using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LithoSharp.Content;

/// <summary>フロントマターのバインド診断識別子を提供します。</summary>
public static class ContentFrontMatterDiagnosticIds
{
    /// <summary>定義されていないフィールドです。</summary>
    public const string UnknownField = "LSC101";

    /// <summary>非 nullable メンバーに YAML の null が指定されています。</summary>
    public const string NullNotAllowed = "LSC102";

    /// <summary>入力値を対象の型へ変換できません。</summary>
    public const string InvalidValue = "LSC103";

    /// <summary>既定値を持たない必須メンバーが指定されていません。</summary>
    public const string MissingRequiredField = "LSC104";
}

/// <summary>
/// 公開プロパティと公開フィールドを使用して、キーと値を型付きフロントマターへ変換します。
/// </summary>
/// <typeparam name="TFrontMatter">変換後のフロントマターの型。</typeparam>
/// <remarks>
/// 対象型には公開された引数なしコンストラクターが必要です。メンバー名は既定で
/// snake_case に変換され、<see cref="YamlMemberAttribute"/> の別名が優先されます。
/// 入力にないメンバーはコンストラクターまたは初期化子が設定した値を保持します。
/// </remarks>
public sealed class ReflectionContentFrontMatterBinder<TFrontMatter>
    : IContentFrontMatterBinder<TFrontMatter>
    where TFrontMatter : notnull
{
    private readonly ReflectionBindingPlan _plan;

    /// <summary>リフレクションを使用するフロントマターバインダーを作成します。</summary>
    /// <param name="rejectUnknownFields">
    /// 定義されていない入力フィールドをエラーにする場合は <see langword="true"/>。
    /// 既定値は <see langword="true"/> です。
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// 対象型を作成できないか、同じ YAML 名を持つメンバーが複数あります。
    /// </exception>
    public ReflectionContentFrontMatterBinder(bool rejectUnknownFields = true)
    {
        RejectUnknownFields = rejectUnknownFields;
        _plan = ReflectionBindingPlan.Create(typeof(TFrontMatter));
    }

    /// <summary>定義されていない入力フィールドをエラーにするかどうかを取得します。</summary>
    public bool RejectUnknownFields { get; }

    /// <inheritdoc />
    public ContentParseResult<TFrontMatter> Bind(
        IReadOnlyDictionary<string, object?> values,
        Diagnostics.SiteSourceLocation? sourceLocation = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        var diagnostics = new List<Diagnostics.SiteDiagnostic>();
        var value = BindObject(_plan, values, sourceLocation, diagnostics);
        return diagnostics.Count == 0
            ? ContentParseResult<TFrontMatter>.Success((TFrontMatter)value)
            : ContentParseResult<TFrontMatter>.Failure(diagnostics);
    }

    private object BindObject(
        ReflectionBindingPlan plan,
        IReadOnlyDictionary<string, object?> values,
        Diagnostics.SiteSourceLocation? sourceLocation,
        List<Diagnostics.SiteDiagnostic> diagnostics)
    {
        var instance = plan.CreateInstance();
        var assignedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pair in values)
        {
            if (!plan.Members.TryGetValue(pair.Key, out var member))
            {
                if (RejectUnknownFields)
                {
                    diagnostics.Add(Diagnostic(
                        ContentFrontMatterDiagnosticIds.UnknownField,
                        $"Unknown front matter field '{pair.Key}'.",
                        GetKeyLocation(values, pair.Key, sourceLocation)));
                }

                continue;
            }

            assignedNames.Add(pair.Key);
            var location = GetValueLocation(values, pair.Key, sourceLocation);
            if (TryConvert(
                pair.Value,
                member.ValueType,
                member.Nullability,
                pair.Key,
                location,
                diagnostics,
                out var converted))
            {
                try
                {
                    member.SetValue(instance, converted);
                }
                catch (TargetInvocationException exception)
                    when (exception.InnerException is ArgumentException
                        or FormatException
                        or InvalidOperationException)
                {
                    diagnostics.Add(Diagnostic(
                        ContentFrontMatterDiagnosticIds.InvalidValue,
                        $"Front matter field '{pair.Key}' was rejected: {exception.InnerException.Message}",
                        location));
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(Diagnostic(
                        ContentFrontMatterDiagnosticIds.InvalidValue,
                        $"Front matter field '{pair.Key}' was rejected: {exception.Message}",
                        location));
                }
            }
        }

        foreach (var member in plan.Members.Values)
        {
            if (assignedNames.Contains(member.YamlName))
            {
                continue;
            }

            var current = member.GetValue(instance);
            if (member.IsRequired || (current is null && !AllowsNull(member.ValueType, member.Nullability)))
            {
                diagnostics.Add(Diagnostic(
                    ContentFrontMatterDiagnosticIds.MissingRequiredField,
                    $"Required front matter field '{member.YamlName}' is missing.",
                    sourceLocation));
            }
        }

        return instance;
    }

    private bool TryConvert(
        object? input,
        Type targetType,
        NullabilityInfo? nullability,
        string fieldPath,
        Diagnostics.SiteSourceLocation? fallbackLocation,
        List<Diagnostics.SiteDiagnostic> diagnostics,
        out object? converted)
    {
        var location = LocatedYamlValue.GetLocation(input) ?? fallbackLocation;
        input = LocatedYamlValue.Unwrap(input);
        if (input is null)
        {
            converted = null;
            if (AllowsNull(targetType, nullability))
            {
                return true;
            }

            diagnostics.Add(Diagnostic(
                ContentFrontMatterDiagnosticIds.NullNotAllowed,
                $"Front matter field '{fieldPath}' does not allow null.",
                location));
            return false;
        }

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType is not null)
        {
            return TryConvert(
                input,
                nullableType,
                nullability?.GenericTypeArguments.FirstOrDefault(),
                fieldPath,
                location,
                diagnostics,
                out converted);
        }

        if (targetType == typeof(object))
        {
            converted = UnwrapTree(input);
            return true;
        }

        if (targetType.IsInstanceOfType(input))
        {
            converted = input;
            return true;
        }

        if (TryGetDictionaryValueType(targetType, out var dictionaryValueType)
            && input is IReadOnlyDictionary<string, object?> mapping)
        {
            return TryConvertDictionary(
                mapping,
                targetType,
                dictionaryValueType,
                nullability,
                fieldPath,
                location,
                diagnostics,
                out converted);
        }

        if (TryGetSequenceElementType(targetType, out var elementType)
            && input is IEnumerable sequence
            && input is not string
            && input is not IReadOnlyDictionary<string, object?>)
        {
            return TryConvertSequence(
                sequence,
                targetType,
                elementType,
                nullability,
                fieldPath,
                location,
                diagnostics,
                out converted);
        }

        if (input is IReadOnlyDictionary<string, object?> nestedValues
            && !IsScalarType(targetType))
        {
            var nestedPlan = ReflectionBindingPlan.Create(targetType);
            converted = BindObject(nestedPlan, nestedValues, location, diagnostics);
            return true;
        }

        if (TryConvertScalar(input, targetType, out converted))
        {
            return true;
        }

        diagnostics.Add(Diagnostic(
            ContentFrontMatterDiagnosticIds.InvalidValue,
            $"Front matter field '{fieldPath}' cannot be converted to '{targetType.Name}'.",
            location));
        return false;
    }

    private bool TryConvertDictionary(
        IReadOnlyDictionary<string, object?> mapping,
        Type targetType,
        Type valueType,
        NullabilityInfo? nullability,
        string fieldPath,
        Diagnostics.SiteSourceLocation? location,
        List<Diagnostics.SiteDiagnostic> diagnostics,
        out object? converted)
    {
        var concreteType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
        var dictionary = (IDictionary)Activator.CreateInstance(concreteType)!;
        var success = true;
        foreach (var pair in mapping)
        {
            if (TryConvert(
                pair.Value,
                valueType,
                nullability?.GenericTypeArguments.ElementAtOrDefault(1),
                $"{fieldPath}.{pair.Key}",
                GetValueLocation(mapping, pair.Key, location),
                diagnostics,
                out var item))
            {
                dictionary.Add(pair.Key, item);
            }
            else
            {
                success = false;
            }
        }

        converted = targetType.IsAssignableFrom(concreteType)
            ? dictionary
            : null;
        if (converted is null)
        {
            diagnostics.Add(Diagnostic(
                ContentFrontMatterDiagnosticIds.InvalidValue,
                $"Front matter field '{fieldPath}' cannot be assigned to '{targetType.Name}'.",
                location));
        }

        return success && converted is not null;
    }

    private bool TryConvertSequence(
        IEnumerable sequence,
        Type targetType,
        Type elementType,
        NullabilityInfo? nullability,
        string fieldPath,
        Diagnostics.SiteSourceLocation? location,
        List<Diagnostics.SiteDiagnostic> diagnostics,
        out object? converted)
    {
        var values = sequence.Cast<object?>().ToArray();
        var array = Array.CreateInstance(elementType, values.Length);
        var success = true;
        for (var index = 0; index < values.Length; index++)
        {
            if (TryConvert(
                values[index],
                elementType,
                nullability?.ElementType ?? nullability?.GenericTypeArguments.FirstOrDefault(),
                $"{fieldPath}[{index}]",
                location,
                diagnostics,
                out var item))
            {
                array.SetValue(item, index);
            }
            else
            {
                success = false;
            }
        }

        if (targetType.IsArray)
        {
            converted = array;
            return success;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in array)
        {
            list.Add(item);
        }

        converted = targetType.IsAssignableFrom(listType)
            ? list
            : null;
        if (converted is null)
        {
            diagnostics.Add(Diagnostic(
                ContentFrontMatterDiagnosticIds.InvalidValue,
                $"Front matter field '{fieldPath}' cannot be assigned to '{targetType.Name}'.",
                location));
        }

        return success && converted is not null;
    }

    private static bool TryConvertScalar(object input, Type targetType, out object? converted)
    {
        var text = input as string ?? Convert.ToString(input, CultureInfo.InvariantCulture);
        if (text is null)
        {
            converted = null;
            return false;
        }

        if (targetType == typeof(string))
        {
            converted = text;
            return true;
        }

        if (targetType == typeof(Guid) && Guid.TryParseExact(text, "D", out var guid))
        {
            converted = guid;
            return true;
        }

        if (targetType == typeof(DateTimeOffset)
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dateTimeOffset))
        {
            converted = dateTimeOffset;
            return true;
        }

        if (targetType == typeof(DateTime)
            && DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dateTime))
        {
            converted = dateTime;
            return true;
        }

        if (targetType == typeof(DateOnly)
            && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            converted = date;
            return true;
        }

        if (targetType == typeof(TimeOnly)
            && TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            converted = time;
            return true;
        }

        if (targetType == typeof(TimeSpan)
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration))
        {
            converted = duration;
            return true;
        }

        if (targetType == typeof(Uri)
            && Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri))
        {
            converted = uri;
            return true;
        }

        if (targetType.IsEnum && Enum.TryParse(targetType, text, ignoreCase: false, out var enumValue))
        {
            converted = enumValue;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
            return converted is not null;
        }
        catch (FormatException)
        {
            converted = null;
            return false;
        }
        catch (InvalidCastException)
        {
            converted = null;
            return false;
        }
        catch (OverflowException)
        {
            converted = null;
            return false;
        }
    }

    private static object? UnwrapTree(object? value)
    {
        value = LocatedYamlValue.Unwrap(value);
        return value switch
        {
            IReadOnlyDictionary<string, object?> mapping => mapping.ToDictionary(
                static pair => pair.Key,
                static pair => UnwrapTree(pair.Value),
                StringComparer.Ordinal),
            IEnumerable sequence when value is not string => sequence.Cast<object?>()
                .Select(UnwrapTree)
                .ToArray(),
            _ => value,
        };
    }

    private static bool AllowsNull(Type type, NullabilityInfo? nullability) =>
        Nullable.GetUnderlyingType(type) is not null
        || (!type.IsValueType && nullability?.WriteState != NullabilityState.NotNull);

    private static bool IsScalarType(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Uri);

    private static bool TryGetSequenceElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var sequenceType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(static candidate =>
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        elementType = sequenceType?.GetGenericArguments()[0] ?? typeof(object);
        return sequenceType is not null;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionaryType = type.IsGenericType
            && IsDictionaryType(type.GetGenericTypeDefinition())
            ? type
            : type.GetInterfaces().FirstOrDefault(static candidate =>
                candidate.IsGenericType
                && IsDictionaryType(candidate.GetGenericTypeDefinition()));
        if (dictionaryType is null || dictionaryType.GetGenericArguments()[0] != typeof(string))
        {
            valueType = typeof(object);
            return false;
        }

        valueType = dictionaryType.GetGenericArguments()[1];
        return true;
    }

    private static bool IsDictionaryType(Type type) =>
        type == typeof(IReadOnlyDictionary<,>)
        || type == typeof(IDictionary<,>);

    private static Diagnostics.SiteSourceLocation? GetKeyLocation(
        IReadOnlyDictionary<string, object?> values,
        string key,
        Diagnostics.SiteSourceLocation? fallback) =>
        values is LocatedYamlMapping mapping ? mapping.GetKeyLocation(key) : fallback;

    private static Diagnostics.SiteSourceLocation? GetValueLocation(
        IReadOnlyDictionary<string, object?> values,
        string key,
        Diagnostics.SiteSourceLocation? fallback) =>
        values is LocatedYamlMapping mapping ? mapping.GetValueLocation(key) : fallback;

    private static Diagnostics.SiteDiagnostic Diagnostic(
        string id,
        string message,
        Diagnostics.SiteSourceLocation? location) =>
        new(id, Diagnostics.SiteDiagnosticSeverity.Error, message, location);

    private sealed class ReflectionBindingPlan
    {
        private ReflectionBindingPlan(
            Func<object> createInstance,
            IReadOnlyDictionary<string, ReflectionBindingMember> members)
        {
            CreateInstance = createInstance;
            Members = members;
        }

        public Func<object> CreateInstance { get; }

        public IReadOnlyDictionary<string, ReflectionBindingMember> Members { get; }

        public static ReflectionBindingPlan Create(Type type)
        {
            var constructor = type.IsValueType
                ? null
                : type.GetConstructor(Type.EmptyTypes)
                    ?? throw new InvalidOperationException(
                        $"Front matter type '{type.FullName}' requires a public parameterless constructor.");
            var nullability = new NullabilityInfoContext();
            var members = new Dictionary<string, ReflectionBindingMember>(StringComparer.Ordinal);

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0
                    || property.SetMethod is not { IsPublic: true }
                    || property.GetCustomAttribute<YamlIgnoreAttribute>() is not null)
                {
                    continue;
                }

                AddMember(
                    members,
                    new ReflectionBindingMember(
                        GetYamlName(property),
                        property.PropertyType,
                        nullability.Create(property),
                        property.IsDefined(typeof(RequiredMemberAttribute)),
                        property.GetValue,
                        property.SetValue));
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.IsInitOnly || field.GetCustomAttribute<YamlIgnoreAttribute>() is not null)
                {
                    continue;
                }

                AddMember(
                    members,
                    new ReflectionBindingMember(
                        GetYamlName(field),
                        field.FieldType,
                        nullability.Create(field),
                        field.IsDefined(typeof(RequiredMemberAttribute)),
                        field.GetValue,
                        field.SetValue));
            }

            return new ReflectionBindingPlan(
                type.IsValueType
                    ? () => Activator.CreateInstance(type)!
                    : () => constructor!.Invoke(null),
                members);
        }

        private static void AddMember(
            IDictionary<string, ReflectionBindingMember> members,
            ReflectionBindingMember member)
        {
            if (!members.TryAdd(member.YamlName, member))
            {
                throw new InvalidOperationException(
                    $"Front matter type has more than one member named '{member.YamlName}'.");
            }
        }

        private static string GetYamlName(MemberInfo member)
        {
            var yamlMember = member.GetCustomAttribute<YamlMemberAttribute>();
            var name = string.IsNullOrEmpty(yamlMember?.Alias)
                ? UnderscoredNamingConvention.Instance.Apply(member.Name)
                : yamlMember.Alias;
            return name.Normalize(NormalizationForm.FormC);
        }
    }

    private sealed record ReflectionBindingMember(
        string YamlName,
        Type ValueType,
        NullabilityInfo Nullability,
        bool IsRequired,
        Func<object, object?> GetValue,
        Action<object, object?> SetValue);
}

internal abstract class LocatedYamlValue(Diagnostics.SiteSourceLocation location)
{
    public Diagnostics.SiteSourceLocation Location { get; } = location;

    public static Diagnostics.SiteSourceLocation? GetLocation(object? value) =>
        value is LocatedYamlValue located ? located.Location : null;

    public static object? Unwrap(object? value) =>
        value is LocatedYamlScalar scalar ? scalar.Value : value;
}

internal sealed class LocatedYamlScalar(
    object? value,
    Diagnostics.SiteSourceLocation location)
    : LocatedYamlValue(location)
{
    public object? Value { get; } = value;
}

internal sealed class LocatedYamlSequence(
    IReadOnlyList<object?> values,
    Diagnostics.SiteSourceLocation location)
    : LocatedYamlValue(location), IReadOnlyList<object?>
{
    public object? this[int index] => values[index];

    public int Count => values.Count;

    public IEnumerator<object?> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class LocatedYamlMapping(
    IReadOnlyDictionary<string, object?> values,
    IReadOnlyDictionary<string, Diagnostics.SiteSourceLocation> keyLocations,
    IReadOnlyDictionary<string, Diagnostics.SiteSourceLocation> valueLocations,
    Diagnostics.SiteSourceLocation location)
    : LocatedYamlValue(location), IReadOnlyDictionary<string, object?>
{
    public object? this[string key] => values[key];

    public IEnumerable<string> Keys => values.Keys;

    public IEnumerable<object?> Values => values.Values;

    public int Count => values.Count;

    public bool ContainsKey(string key) => values.ContainsKey(key);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

    public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Diagnostics.SiteSourceLocation GetKeyLocation(string key) => keyLocations[key];

    public Diagnostics.SiteSourceLocation GetValueLocation(string key) => valueLocations[key];
}
