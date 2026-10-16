using LithoSharp.Content;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

public sealed class FrontMatterFieldInfo
{
    internal FrontMatterFieldInfo(
        string key,
        string type,
        string? itemType,
        bool required,
        bool allowsNull,
        IReadOnlyList<string> enumValues,
        bool deprecated,
        string? deprecationMessage,
        string? description,
        IReadOnlyList<FrontMatterFieldInfo> fields)
    {
        Key = key;
        Type = type;
        ItemType = itemType;
        Required = required;
        AllowsNull = allowsNull;
        EnumValues = Array.AsReadOnly(enumValues.ToArray());
        Deprecated = deprecated;
        DeprecationMessage = deprecationMessage;
        Description = description;
        Fields = Array.AsReadOnly(fields.ToArray());
    }

    public string Key { get; }

    public string Type { get; }

    public string? ItemType { get; }

    public bool Required { get; }

    public bool AllowsNull { get; }

    public IReadOnlyList<string> EnumValues { get; }

    public bool Deprecated { get; }

    public string? DeprecationMessage { get; }

    public string? Description { get; }

    public IReadOnlyList<FrontMatterFieldInfo> Fields { get; }
}

public sealed class FrontMatterSchema
{
    internal FrontMatterSchema(
        string name,
        bool isBuiltIn,
        Type frontMatterType,
        bool rejectUnknownFields,
        IReadOnlyList<FrontMatterFieldInfo> fields)
    {
        Name = name;
        IsBuiltIn = isBuiltIn;
        FrontMatterType = frontMatterType;
        RejectUnknownFields = rejectUnknownFields;
        Fields = Array.AsReadOnly(fields.ToArray());
    }

    public string Name { get; }

    public bool IsBuiltIn { get; }

    public Type FrontMatterType { get; }

    public bool RejectUnknownFields { get; }

    public IReadOnlyList<FrontMatterFieldInfo> Fields { get; }
}

public static class FrontMatterSchemas
{
    public static FrontMatterSchema Document { get; } =
        new ReflectionContentFrontMatterBinder<DocumentFrontMatter>().DescribeSchema("document", isBuiltIn: true);

    public static FrontMatterSchema Post { get; } =
        new ReflectionContentFrontMatterBinder<PostFrontMatter>().DescribeSchema("post", isBuiltIn: true);

    /// <summary>Requires a public parameterless constructor; duplicate YAML member names or an unconstructible type fail.</summary>
    public static FrontMatterSchema For<TFrontMatter>()
        where TFrontMatter : notnull =>
        new ReflectionContentFrontMatterBinder<TFrontMatter>().DescribeSchema(typeof(TFrontMatter).Name, isBuiltIn: false);
}
