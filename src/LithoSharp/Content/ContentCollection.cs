using System.Collections.ObjectModel;
using System.Text;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>コンテンツエントリから公開ルートを決定する規約を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
/// <param name="entry">ルートを決定するエントリ。</param>
/// <returns>エントリの公開ルート。</returns>
public delegate SiteRoute ContentRouteConvention<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

/// <summary>コンテンツエントリから公開情報と公開条件を作成する変換を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
/// <param name="entry">公開情報を作成するエントリ。</param>
/// <returns>エントリの公開情報と公開条件。</returns>
public delegate PageMetadata ContentPublicationMapper<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

/// <summary>型付きコンテンツエントリと、その読み込み後の不変設定スナップショットを表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public sealed class ContentCollection<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>コンテンツコレクションを作成します。</summary>
    /// <param name="id">ビルド間で安定したコレクション識別子。</param>
    /// <param name="inputRoot">コレクションを読み込む入力ルート。</param>
    /// <param name="entries">コレクションに含めるエントリ。</param>
    /// <param name="routeConvention">各エントリの公開ルートを決定する規約。</param>
    /// <param name="publicationMapper">各エントリの公開情報と公開条件を作成する変換。</param>
    /// <param name="layoutId">使用するレイアウトの識別子。指定しない場合は <see langword="null"/>。</param>
    /// <param name="orderingComparer">
    /// エントリの順序を決める比較方法。指定しない場合はエントリ識別子の序数順です。
    /// </param>
    /// <param name="declaredDependencies">コレクションが参照する宣言済み外部依存。</param>
    /// <param name="transformationId">
    /// ルート、公開情報、順序を決める変換規則の安定した識別子。
    /// キャッシュ可能にする場合は必須であり、規則の結果が変わるたびに値も変更する必要があります。
    /// </param>
    /// <param name="isCacheable">
    /// 宣言済み依存と <paramref name="transformationId"/> だけで結果を再現でき、
    /// 将来のキャッシュ対象にできるかどうか。既定値は <see langword="false"/> です。
    /// </param>
    /// <exception cref="ArgumentNullException">引数の必須値が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// 入力ルートが空、エントリに <see langword="null"/> が含まれる、識別子が重複している、
    /// またはキャッシュ可能なコレクションに変換規則の識別子がありません。
    /// </exception>
    /// <remarks>
    /// キャッシュ可能にする呼び出し元は、デリゲートまたは比較方法が参照する外部状態を
    /// <paramref name="declaredDependencies"/> にすべて宣言する必要があります。
    /// </remarks>
    public ContentCollection(
        ContentCollectionId id,
        string inputRoot,
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        ContentRouteConvention<TFrontMatter, TBody> routeConvention,
        ContentPublicationMapper<TFrontMatter, TBody> publicationMapper,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, TBody>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(inputRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(routeConvention);
        ArgumentNullException.ThrowIfNull(publicationMapper);
        if (string.IsNullOrWhiteSpace(inputRoot))
        {
            throw new ArgumentException("A content input root must not be empty.", nameof(inputRoot));
        }

        if (isCacheable && transformationId is null)
        {
            throw new ArgumentException(
                "A cacheable content collection requires a stable transformation identifier.",
                nameof(transformationId));
        }

        Id = id;
        InputRoot = inputRoot.Normalize(NormalizationForm.FormC);
        RouteConvention = routeConvention;
        PublicationMapper = publicationMapper;
        LayoutId = layoutId;
        OrderingComparer = orderingComparer ?? ContentEntryIdComparer.Instance;
        Entries = SnapshotEntries(entries, OrderingComparer);
        DeclaredDependencies = ContentDependency.Snapshot(declaredDependencies);
        TransformationId = transformationId;
        IsCacheable = isCacheable;
    }

    /// <summary>ビルド間で安定したコレクション識別子を取得します。</summary>
    public ContentCollectionId Id { get; }

    /// <summary>コレクションを読み込む入力ルートを取得します。</summary>
    public string InputRoot { get; }

    /// <summary>設定された順序で固定されたエントリのスナップショットを取得します。</summary>
    public IReadOnlyList<ContentEntry<TFrontMatter, TBody>> Entries { get; }

    /// <summary>各エントリの公開ルートを決定する規約を取得します。</summary>
    public ContentRouteConvention<TFrontMatter, TBody> RouteConvention { get; }

    /// <summary>各エントリの公開情報と公開条件を作成する変換を取得します。</summary>
    public ContentPublicationMapper<TFrontMatter, TBody> PublicationMapper { get; }

    /// <summary>使用するレイアウトの識別子を取得します。指定されていない場合は <see langword="null"/> です。</summary>
    public ContentLayoutId? LayoutId { get; }

    /// <summary>エントリの順序を決める比較方法を取得します。</summary>
    public IComparer<ContentEntry<TFrontMatter, TBody>> OrderingComparer { get; }

    /// <summary>種類、キー、値の序数順で固定された宣言済み外部依存を取得します。</summary>
    public IReadOnlyList<ContentDependency> DeclaredDependencies { get; }

    /// <summary>ルート、公開情報、順序を決める変換規則の安定した識別子を取得します。</summary>
    public ContentTransformationId? TransformationId { get; }

    /// <summary>
    /// 宣言済み依存と変換規則の識別子だけで結果を再現でき、将来のキャッシュ対象にできるかどうかを取得します。
    /// </summary>
    public bool IsCacheable { get; }

    /// <summary>公開対象のエントリを決定的にグループ化し、型付きの集約ページを生成します。</summary>
    /// <typeparam name="TPageContent">生成ページが保持する型付きコンテンツの型。</typeparam>
    /// <param name="generatedCollectionId">生成ページ集合をビルド間で安定して識別する識別子。</param>
    /// <param name="groupSelector">各公開エントリが属するグループキーを返す処理。</param>
    /// <param name="pageFactory">正規化済みキーと公開エントリからページを作成する処理。</param>
    /// <param name="renderer">生成ページを出力文字列へ描画する処理。</param>
    /// <param name="groupKeys">エントリがない場合も生成するグループキー。</param>
    /// <param name="layoutId">生成ページが使用するレイアウトの識別子。</param>
    /// <param name="groupOrderingComparer">
    /// グループの順序を決める比較方法。指定しない場合は正規化済みキーの序数順です。
    /// </param>
    /// <param name="declaredDependencies">グループ化、ページ作成、描画が参照する追加の外部依存。</param>
    /// <param name="transformationId">
    /// グループ化、ページ作成、描画規則の安定した識別子。
    /// キャッシュ可能にする場合は必須であり、規則の結果が変わるたびに値も変更する必要があります。
    /// </param>
    /// <param name="isCacheable">
    /// ソースコレクションと生成規則の宣言済み入力だけで結果を再現できるかどうか。
    /// </param>
    /// <param name="derivedSurfaces">
    /// 生成した集約ページを含める派生出力。既定では検索、サイトマップ、ページ別ソーシャル画像、
    /// <c>llms.txt</c> に含め、RSS とサイト全体のナビゲーションからは除外します。
    /// </param>
    /// <returns>生成オプションへ登録できるページ集合。</returns>
    /// <exception cref="ArgumentNullException">必須引数が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// 生成ページ集合の識別子がソースコレクションと同じ、またはキャッシュ可能性の宣言が不完全です。
    /// </exception>
    /// <remarks>
    /// ソースエントリの公開判定後にグループ化します。グループキーは Unicode 正規化形式 C へ正規化し、
    /// 大文字と小文字を区別します。同じエントリが同じ正規化済みキーを複数回返しても一度だけ追加されます。
    /// 返される型は生成ページを再び集約する API を公開しないため、再帰的な集約は行われません。
    /// キャッシュ可能にする場合は、このコレクションもキャッシュ可能である必要があります。
    /// </remarks>
    public SiteContentCollection GeneratePages<TPageContent>(
        ContentCollectionId generatedCollectionId,
        ContentPageGroupSelector<TFrontMatter, TBody> groupSelector,
        GeneratedPageFactory<TFrontMatter, TBody, TPageContent> pageFactory,
        GeneratedPageRenderer<TPageContent> renderer,
        IEnumerable<string>? groupKeys = null,
        ContentLayoutId? layoutId = null,
        IComparer<string>? groupOrderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false,
        GeneratedPageDerivedSurfaces derivedSurfaces = GeneratedPageDerivedSurfaces.Default)
        where TPageContent : notnull =>
        new GeneratedSiteContentCollection<TFrontMatter, TBody, TPageContent>(
            this,
            generatedCollectionId,
            groupSelector,
            pageFactory,
            renderer,
            groupKeys,
            layoutId,
            groupOrderingComparer,
            declaredDependencies,
            transformationId,
            isCacheable,
            derivedSurfaces);

    private static ReadOnlyCollection<ContentEntry<TFrontMatter, TBody>> SnapshotEntries(
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        IComparer<ContentEntry<TFrontMatter, TBody>> comparer)
    {
        var values = entries.ToArray();
        if (values.Any(static entry => entry is null))
        {
            throw new ArgumentException("Content entries must not contain null.", nameof(entries));
        }

        if (values
            .GroupBy(static entry => entry.Id)
            .Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException("Content entry identifiers must be unique.", nameof(entries));
        }

        Array.Sort(values, new DeterministicComparer(comparer));
        return Array.AsReadOnly(values);
    }

    private sealed class ContentEntryIdComparer : IComparer<ContentEntry<TFrontMatter, TBody>>
    {
        public static ContentEntryIdComparer Instance { get; } = new();

        public int Compare(
            ContentEntry<TFrontMatter, TBody>? x,
            ContentEntry<TFrontMatter, TBody>? y) =>
            StringComparer.Ordinal.Compare(x?.Id.Value, y?.Id.Value);
    }

    private sealed class DeterministicComparer(
        IComparer<ContentEntry<TFrontMatter, TBody>> comparer)
        : IComparer<ContentEntry<TFrontMatter, TBody>>
    {
        public int Compare(
            ContentEntry<TFrontMatter, TBody>? x,
            ContentEntry<TFrontMatter, TBody>? y)
        {
            var result = comparer.Compare(x, y);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(x?.Id.Value, y?.Id.Value);
        }
    }
}
