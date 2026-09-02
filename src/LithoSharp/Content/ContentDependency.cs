using System.Collections.ObjectModel;
using LithoSharp.Build;

namespace LithoSharp.Content;

/// <summary>コンテンツコレクションが宣言する外部依存の種類を表します。</summary>
public enum ContentDependencyKind
{
    /// <summary>名前付きの値への依存です。</summary>
    Value,

    /// <summary>入力ルートからの相対ファイルへの依存です。</summary>
    File,
}

/// <summary>コンテンツコレクションが宣言する不変の外部依存を表します。</summary>
public sealed class ContentDependency : IEquatable<ContentDependency>
{
    private ContentDependency(ContentDependencyKind kind, string key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    /// <summary>依存の種類を取得します。</summary>
    public ContentDependencyKind Kind { get; }

    /// <summary>依存を種類内で識別する正規化済みのキーを取得します。</summary>
    public string Key { get; }

    /// <summary>値依存の値を取得します。ファイル依存では <see langword="null"/> です。</summary>
    public string? Value { get; }

    /// <summary>入力ルートからの相対ファイルへの依存を作成します。</summary>
    /// <param name="relativePath">OS に依存しない相対ファイルパス。</param>
    /// <returns>ファイル依存。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> が安全な相対パスではありません。</exception>
    public static ContentDependency FromFile(string relativePath)
    {
        var input = BuildInput.FromFile(relativePath);
        return new ContentDependency(ContentDependencyKind.File, input.Key, value: null);
    }

    /// <summary>名前付きの値への依存を作成します。</summary>
    /// <param name="key">値を識別する安定したキー。</param>
    /// <param name="value">依存する値。</param>
    /// <returns>値依存。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> または <paramref name="value"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> が有効な識別子ではありません。</exception>
    public static ContentDependency FromValue(string key, string value)
    {
        var normalizedKey = ContentIdentity.Normalize(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        return new ContentDependency(
            ContentDependencyKind.Value,
            normalizedKey,
            value);
    }

    /// <summary>指定した依存が同じ種類、キー、値を表すかどうかを判定します。</summary>
    /// <param name="other">比較する依存。</param>
    /// <returns>種類、キー、値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(ContentDependency? other) =>
        other is not null
        && Kind == other.Kind
        && StringComparer.Ordinal.Equals(Key, other.Key)
        && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じ依存を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ依存を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is ContentDependency other && Equals(other);

    /// <summary>種類、キー、値に基づくハッシュコードを返します。</summary>
    /// <returns>依存のハッシュコード。</returns>
    public override int GetHashCode() =>
        HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(Key), Value);

    internal static ReadOnlyCollection<ContentDependency> Snapshot(
        IEnumerable<ContentDependency>? dependencies)
    {
        var values = dependencies?.ToArray() ?? [];
        if (values.Any(static dependency => dependency is null))
        {
            throw new ArgumentException("Content dependencies must not contain null.", nameof(dependencies));
        }

        return Array.AsReadOnly(values
            .Distinct()
            .OrderBy(static dependency => dependency.Kind)
            .ThenBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
