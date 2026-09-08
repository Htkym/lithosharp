using System.Text;

namespace LithoSharp.Content;

/// <summary>コンテンツコレクションをビルド間で安定して識別する値を表します。</summary>
public sealed class ContentCollectionId : IEquatable<ContentCollectionId>
{
    /// <summary>指定した値からコレクション識別子を作成します。</summary>
    /// <param name="value">OS や実行環境に依存しない識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> が有効な識別子ではありません。</exception>
    public ContentCollectionId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    /// <summary>Unicode 正規化形式 C で正規化された識別子を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ値を表すかどうかを判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(ContentCollectionId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じ識別子を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ識別子を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is ContentCollectionId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

/// <summary>コンテンツエントリをビルド間で安定して識別する値を表します。</summary>
public sealed class ContentEntryId : IEquatable<ContentEntryId>
{
    /// <summary>指定した値からエントリ識別子を作成します。</summary>
    /// <param name="value">OS や実行環境に依存しない識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> が有効な識別子ではありません。</exception>
    public ContentEntryId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    /// <summary>Unicode 正規化形式 C で正規化された識別子を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ値を表すかどうかを判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(ContentEntryId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じ識別子を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ識別子を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is ContentEntryId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

/// <summary>コンテンツのレイアウトを安定して参照する識別子を表します。</summary>
public sealed class ContentLayoutId : IEquatable<ContentLayoutId>
{
    /// <summary>指定した値からレイアウト識別子を作成します。</summary>
    /// <param name="value">OS や実行環境に依存しない識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> が有効な識別子ではありません。</exception>
    public ContentLayoutId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    /// <summary>Unicode 正規化形式 C で正規化された識別子を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ値を表すかどうかを判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(ContentLayoutId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じ識別子を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ識別子を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is ContentLayoutId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

/// <summary>ルート、公開情報、順序を決める変換規則をビルド間で安定して識別する値を表します。</summary>
public sealed class ContentTransformationId : IEquatable<ContentTransformationId>
{
    /// <summary>指定した値から変換規則の識別子を作成します。</summary>
    /// <param name="value">変換規則またはその版を安定して表す識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> が有効な識別子ではありません。</exception>
    public ContentTransformationId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    /// <summary>Unicode 正規化形式 C で正規化された識別子を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ変換規則を表すかどうかを判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(ContentTransformationId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じ変換規則を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ変換規則を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is ContentTransformationId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

internal static class ContentIdentity
{
    public static string Normalize(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A content identifier must not be empty.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A content identifier must not have surrounding whitespace or control characters.",
                parameterName);
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
