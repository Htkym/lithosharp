using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

/// <summary>型付きフロントマターと本文を持つ、読み取り専用のコンテンツエントリを表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public sealed class ContentEntry<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>コンテンツエントリを作成します。</summary>
    /// <param name="id">ビルド間で安定したエントリ識別子。</param>
    /// <param name="sourcePath">入力ルートからの相対ソースパス。</param>
    /// <param name="sourceFingerprint">ソース内容から決定的に生成したハッシュまたは指紋。</param>
    /// <param name="frontMatter">型付きフロントマター。</param>
    /// <param name="body">型付き本文。</param>
    /// <param name="sourceLocation">エントリの開始位置。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException">引数の必須値が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourcePath"/> または <paramref name="sourceFingerprint"/> が無効です。
    /// </exception>
    public ContentEntry(
        ContentEntryId id,
        string sourcePath,
        string sourceFingerprint,
        TFrontMatter frontMatter,
        TBody body,
        SiteSourceLocation? sourceLocation = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(frontMatter);
        ArgumentNullException.ThrowIfNull(body);

        Id = id;
        SourcePath = Build.BuildInput.FromFile(sourcePath).Key;
        SourceFingerprint = ContentIdentity.Normalize(sourceFingerprint, nameof(sourceFingerprint));
        FrontMatter = frontMatter;
        Body = body;
        SourceLocation = sourceLocation;
    }

    /// <summary>ビルド間で安定したエントリ識別子を取得します。</summary>
    public ContentEntryId Id { get; }

    /// <summary><c>/</c> 区切りで正規化された、入力ルートからの相対ソースパスを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>ソース内容から決定的に生成されたハッシュまたは指紋を取得します。</summary>
    public string SourceFingerprint { get; }

    /// <summary>型付きフロントマターを取得します。</summary>
    public TFrontMatter FrontMatter { get; }

    /// <summary>型付き本文を取得します。</summary>
    public TBody Body { get; }

    /// <summary>エントリの開始位置を取得します。特定できない場合は <see langword="null"/> です。</summary>
    public SiteSourceLocation? SourceLocation { get; }
}
