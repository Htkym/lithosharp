using LithoSharp.Content;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

/// <summary>front matterの1項目の検査情報を表します。</summary>
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
        EnumValues = enumValues;
        Deprecated = deprecated;
        DeprecationMessage = deprecationMessage;
        Description = description;
        Fields = fields;
    }

    /// <summary>YAMLのキー名を取得します。</summary>
    public string Key { get; }

    /// <summary>項目の種類を取得します。boolean、integer、number、string、date-time、date、time、uuid、uri、enum、array、object、dictionary、anyのいずれかです。</summary>
    public string Type { get; }

    /// <summary>arrayやdictionaryの要素・値の種類を取得します。該当しない場合は <see langword="null"/> です。</summary>
    public string? ItemType { get; }

    /// <summary>入力に必須かどうかを取得します。実際のbinderの欠落判定と同じ基準です。</summary>
    public bool Required { get; }

    /// <summary>nullを許容するかどうかを取得します。</summary>
    public bool AllowsNull { get; }

    /// <summary>enumの候補名を取得します。enum以外は空です。</summary>
    public IReadOnlyList<string> EnumValues { get; }

    /// <summary>非推奨かどうかを取得します。</summary>
    public bool Deprecated { get; }

    /// <summary>非推奨の理由を取得します。該当しない場合は <see langword="null"/> です。</summary>
    public string? DeprecationMessage { get; }

    /// <summary>項目の説明を取得します。ない場合は <see langword="null"/> です。</summary>
    public string? Description { get; }

    /// <summary>object項目やobject要素のarray・dictionary項目の内側の項目を取得します。該当しない場合は空です。</summary>
    public IReadOnlyList<FrontMatterFieldInfo> Fields { get; }
}

/// <summary>一度の取得から得たfront matter schemaの不変snapshotを表します。</summary>
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
        Fields = fields;
    }

    /// <summary>schema名を取得します。組み込みは固定名、利用者定義は型名です。</summary>
    public string Name { get; }

    /// <summary>組み込みschemaかどうかを取得します。</summary>
    public bool IsBuiltIn { get; }

    /// <summary>対象のfront matter型を取得します。</summary>
    public Type FrontMatterType { get; }

    /// <summary>定義外の入力を拒否するかどうかを取得します。実際のbinderの設定と同じ値です。</summary>
    public bool RejectUnknownFields { get; }

    /// <summary>キー名順の項目を取得します。</summary>
    public IReadOnlyList<FrontMatterFieldInfo> Fields { get; }
}

/// <summary>front matter schemaの入口を表します。</summary>
/// <remarks>schemaは実際のbinderと同じ binding plan から作ります。補完用の固定キー一覧は持ちません。</remarks>
public static class FrontMatterSchemas
{
    /// <summary>文書（docs）の組み込みschemaを取得します。</summary>
    public static FrontMatterSchema Document { get; } =
        new ReflectionContentFrontMatterBinder<DocumentFrontMatter>().DescribeSchema("document", isBuiltIn: true);

    /// <summary>投稿（blog）の組み込みschemaを取得します。</summary>
    public static FrontMatterSchema Post { get; } =
        new ReflectionContentFrontMatterBinder<PostFrontMatter>().DescribeSchema("post", isBuiltIn: true);

    /// <summary>利用者定義のfront matter型からschemaを取得します。</summary>
    /// <typeparam name="TFrontMatter">公開された引数なしコンストラクターを持つfront matterの型。</typeparam>
    /// <returns>キー、型、必須性、enum、非推奨、説明を持つ不変snapshot。</returns>
    /// <exception cref="InvalidOperationException">対象型を作成できないか、同じYAML名を持つメンバーが複数あります。</exception>
    /// <remarks>組み込みとの区別は <see cref="FrontMatterSchema.IsBuiltIn"/> で行います。</remarks>
    public static FrontMatterSchema For<TFrontMatter>()
        where TFrontMatter : notnull =>
        new ReflectionContentFrontMatterBinder<TFrontMatter>().DescribeSchema(typeof(TFrontMatter).Name, isBuiltIn: false);
}
