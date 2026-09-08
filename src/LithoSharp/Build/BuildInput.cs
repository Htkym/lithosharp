using System.Collections.ObjectModel;
using System.Text;

namespace LithoSharp.Build;

/// <summary>ビルドノードが宣言できる入力の種類を表します。</summary>
public enum BuildInputKind
{
    /// <summary>値またはコンテンツそのものへの直接依存です。</summary>
    Value,

    /// <summary>コレクション全体への依存です。</summary>
    Collection,

    /// <summary>設定値への依存です。</summary>
    Configuration,

    /// <summary>ファイルへの依存です。</summary>
    File,
}

/// <summary>ビルドノードが宣言する、種類付きの不変入力を表します。</summary>
public sealed class BuildInput
{
    private BuildInput(BuildInputKind kind, string key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    /// <summary>入力の種類を取得します。</summary>
    public BuildInputKind Kind { get; }

    /// <summary>入力を種類内で識別する正規化済みのキーを取得します。</summary>
    public string Key { get; }

    /// <summary>宣言時の値またはフィンガープリントを取得します。値を持たない入力では <see langword="null"/> です。</summary>
    public string? Value { get; }

    /// <summary>値またはコンテンツそのものへの直接依存を作成します。</summary>
    /// <param name="key">入力を識別する安定したキー。</param>
    /// <param name="value">入力値またはコンテンツ。</param>
    /// <returns>直接入力。</returns>
    public static BuildInput FromValue(string key, string value) =>
        new(BuildInputKind.Value, NormalizeKey(key), PreserveValue(value));

    /// <summary>コレクション全体への依存を作成します。</summary>
    /// <param name="collectionId">コレクションを識別する安定した値。</param>
    /// <returns>コレクション入力。</returns>
    public static BuildInput FromCollection(string collectionId) =>
        new(BuildInputKind.Collection, NormalizeKey(collectionId), value: null);

    /// <summary>決定的なフィンガープリントを持つコレクション全体への依存を作成します。</summary>
    /// <param name="collectionId">コレクションを識別する安定した値。</param>
    /// <param name="fingerprint">コレクション内容を表す空でない決定的なフィンガープリント。</param>
    /// <returns>フィンガープリント付きのコレクション入力。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collectionId"/> または <paramref name="fingerprint"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="collectionId"/> または <paramref name="fingerprint"/> が空です。
    /// </exception>
    public static BuildInput FromCollection(string collectionId, string fingerprint) =>
        new(
            BuildInputKind.Collection,
            NormalizeKey(collectionId),
            PreserveFingerprint(fingerprint));

    /// <summary>設定値への依存を作成します。</summary>
    /// <param name="key">設定項目を識別する安定したキー。</param>
    /// <param name="value">ビルドで使用する設定値。</param>
    /// <returns>設定入力。</returns>
    public static BuildInput FromConfiguration(string key, string value) =>
        new(BuildInputKind.Configuration, NormalizeKey(key), PreserveValue(value));

    /// <summary>出力ルートとは独立した相対ファイルへの依存を作成します。</summary>
    /// <param name="relativePath">OS に依存しない相対ファイルパス。</param>
    /// <returns>ファイル入力。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> が安全な相対ファイルパスではありません。</exception>
    public static BuildInput FromFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedPath = Routing.SiteRoute.NormalizeRelativeOutputPath(relativePath);
        return new BuildInput(BuildInputKind.File, normalizedPath, value: null);
    }

    /// <summary>決定的なフィンガープリントを持つ相対ファイルへの依存を作成します。</summary>
    /// <param name="relativePath">OS に依存しない相対ファイルパス。</param>
    /// <param name="fingerprint">ファイル内容を表す空でない決定的なフィンガープリント。</param>
    /// <returns>フィンガープリント付きのファイル入力。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relativePath"/> または <paramref name="fingerprint"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="relativePath"/> が安全な相対ファイルパスでないか、
    /// <paramref name="fingerprint"/> が空です。
    /// </exception>
    public static BuildInput FromFile(string relativePath, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedPath = Routing.SiteRoute.NormalizeRelativeOutputPath(relativePath);
        return new BuildInput(
            BuildInputKind.File,
            normalizedPath,
            PreserveFingerprint(fingerprint));
    }

    private static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A build input key must not be empty.", nameof(key));
        }

        if (!string.Equals(key, key.Trim(), StringComparison.Ordinal)
            || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A build input key must not have surrounding whitespace or control characters.",
                nameof(key));
        }

        return key.Normalize(NormalizationForm.FormC);
    }

    private static string PreserveValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    private static string PreserveFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length == 0)
        {
            throw new ArgumentException(
                "A build input fingerprint must not be empty.",
                nameof(fingerprint));
        }

        return fingerprint;
    }

    internal static ReadOnlyCollection<BuildInput> Snapshot(IEnumerable<BuildInput>? inputs)
    {
        var values = inputs?.ToArray() ?? [];
        if (values.Any(input => input is null))
        {
            throw new ArgumentException("Build inputs must not contain null.", nameof(inputs));
        }

        return Array.AsReadOnly(values
            .OrderBy(input => input.Kind)
            .ThenBy(input => input.Key, StringComparer.Ordinal)
            .ThenBy(input => input.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
