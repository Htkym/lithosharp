using System.Text;

namespace LithoSharp.Build;

/// <summary>ビルドノードをビルド間で安定して識別する値を表します。</summary>
public sealed class BuildNodeId : IEquatable<BuildNodeId>
{
    /// <summary>指定した値からビルドノード識別子を作成します。</summary>
    /// <param name="value">OS の規則に依存しない識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> が空、前後に空白を含む、または制御文字を含みます。
    /// </exception>
    public BuildNodeId(string value)
    {
        Value = BuildIdentifier.Normalize(value, nameof(value), "node");
    }

    /// <summary>Unicode 正規化形式 C で正規化された識別子の値を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ値を表すかどうかを大文字と小文字を区別して判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(BuildNodeId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じビルドノード識別子を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ識別子を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is BuildNodeId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

/// <summary>ビルド成果物をビルド間で安定して識別する値を表します。</summary>
public sealed class BuildArtifactId : IEquatable<BuildArtifactId>
{
    /// <summary>指定した値からビルド成果物識別子を作成します。</summary>
    /// <param name="value">OS の規則に依存しない識別子。</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> が空、前後に空白を含む、または制御文字を含みます。
    /// </exception>
    public BuildArtifactId(string value)
    {
        Value = BuildIdentifier.Normalize(value, nameof(value), "artifact");
    }

    /// <summary>Unicode 正規化形式 C で正規化された識別子の値を取得します。</summary>
    public string Value { get; }

    /// <summary>指定した識別子が同じ値を表すかどうかを大文字と小文字を区別して判定します。</summary>
    /// <param name="other">比較する識別子。</param>
    /// <returns>正規化済みの値が一致する場合は <see langword="true"/>。</returns>
    public bool Equals(BuildArtifactId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    /// <summary>指定したオブジェクトが同じビルド成果物識別子を表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じ識別子を表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is BuildArtifactId other && Equals(other);

    /// <summary>正規化済みの識別子に基づくハッシュコードを返します。</summary>
    /// <returns>識別子のハッシュコード。</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <summary>正規化済みの識別子を返します。</summary>
    /// <returns><see cref="Value"/> の値。</returns>
    public override string ToString() => Value;
}

internal static class BuildIdentifier
{
    public static string Normalize(string value, string parameterName, string kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A build {kind} identifier must not be empty.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A build {kind} identifier must not have leading or trailing whitespace.",
                parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A build {kind} identifier must not contain control characters.",
                parameterName);
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
