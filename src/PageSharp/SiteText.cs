namespace PageSharp;

/// <summary>
/// UI strings (theme text) for the site. They are injected so that a site can
/// swap in a different language. The default set is English; <see cref="Japanese"/>
/// provides a Japanese set.
/// </summary>
public sealed record SiteText
{
    /// <summary>Label for the menu toggle button.</summary>
    public string MenuLabel { get; init; } = "Menu";

    /// <summary>aria-label for the site navigation.</summary>
    public string SiteNavigationLabel { get; init; } = "Site navigation";

    /// <summary>Label for the search input.</summary>
    public string SearchInputLabel { get; init; } = "Search posts";

    /// <summary>Label for the search button.</summary>
    public string SearchButtonLabel { get; init; } = "Search";

    /// <summary>Placeholder for the header search form.</summary>
    public string HeaderSearchPlaceholder { get; init; } = "Search posts...";

    /// <summary>Heading for the table of contents.</summary>
    public string TableOfContentsHeading { get; init; } = "On this page";

    /// <summary>Message shown when a post has no headings.</summary>
    public string TableOfContentsEmpty { get; init; } = "This post has no headings.";

    /// <summary>Lede shown on the home page.</summary>
    public string IndexLede { get; init; } = "A static site built with PageSharp.";

    /// <summary>Message shown when there are no posts.</summary>
    public string IndexEmpty { get; init; } = "No posts yet.";

    /// <summary>"Latest post" heading.</summary>
    public string LatestPostHeading { get; init; } = "Latest post";

    /// <summary>"Older posts" heading.</summary>
    public string OlderPostsHeading { get; init; } = "Older posts";

    /// <summary>Message shown when there are no older posts.</summary>
    public string OlderPostsEmpty { get; init; } = "No older posts yet.";

    /// <summary>Heading for the archives page.</summary>
    public string ArchivesHeading { get; init; } = "Archives";

    /// <summary>Message shown when there are no posts to archive.</summary>
    public string ArchivesEmpty { get; init; } = "No posts to archive yet.";

    /// <summary>Heading for the tags page.</summary>
    public string TagsHeading { get; init; } = "Tags";

    /// <summary>Description for the tags page.</summary>
    public string TagsIntro { get; init; } = "Browse posts by tag.";

    /// <summary>"Available tags" heading.</summary>
    public string ExistingTagsHeading { get; init; } = "Available tags";

    /// <summary>Message shown when no tags are available.</summary>
    public string TagsEmpty { get; init; } = "No tags available yet.";

    /// <summary>Heading for the search page.</summary>
    public string SearchHeading { get; init; } = "Search posts";

    /// <summary>Description for the search page.</summary>
    public string SearchIntro { get; init; } = "Search titles, summaries, tags, and body text.";

    /// <summary>Placeholder for the search page input.</summary>
    public string SearchInputPlaceholder { get; init; } = "Type a keyword";

    /// <summary>Message shown while the search index is loading.</summary>
    public string SearchLoading { get; init; } = "Loading the search index...";

    /// <summary>First part of the message shown when JavaScript is disabled.</summary>
    public string SearchNoscriptPrefix { get; init; } = "Enable JavaScript to search. Browse posts from the ";

    /// <summary>Archive link text inside the JavaScript-disabled message.</summary>
    public string SearchNoscriptArchivesLinkText { get; init; } = "archives";

    /// <summary>Last part of the message shown when JavaScript is disabled.</summary>
    public string SearchNoscriptSuffix { get; init; } = ".";

    /// <summary>Heading for the posts section of llms.txt.</summary>
    public string LlmsPostsHeading { get; init; } = "Posts";

    /// <summary>
    /// Search status template shown before a query, listing how many posts are searchable.
    /// Placeholder: <c>{count}</c>.
    /// </summary>
    public string SearchStatusBrowseAll { get; init; } = "Search across {count} posts.";

    /// <summary>Search status template for the number of results. Placeholder: <c>{count}</c>.</summary>
    public string SearchStatusHits { get; init; } = "{count} results.";

    /// <summary>Search status template for results within a tag. Placeholders: <c>{count}</c>, <c>{tag}</c>.</summary>
    public string SearchStatusHitsInTag { get; init; } = "{count} results in tag '{tag}'.";

    /// <summary>Suffix appended when only the top results are shown. Placeholder: <c>{shown}</c>.</summary>
    public string SearchStatusShowingTopSuffix { get; init; } = " (showing top {shown})";

    /// <summary>Search status template when browsing posts of a tag. Placeholders: <c>{count}</c>, <c>{tag}</c>.</summary>
    public string SearchStatusTaggedPosts { get; init; } = "Showing {count} posts tagged '{tag}'.";

    /// <summary>Search status template when a query has no match. Placeholder: <c>{query}</c>.</summary>
    public string SearchStatusNoMatch { get; init; } = "No posts match '{query}'.";

    /// <summary>Search status template when a query has no match within a tag. Placeholders: <c>{tag}</c>, <c>{query}</c>.</summary>
    public string SearchStatusNoMatchInTag { get; init; } = "No posts in tag '{tag}' match '{query}'.";

    /// <summary>Search status template when a tag has no posts. Placeholder: <c>{tag}</c>.</summary>
    public string SearchStatusNoPostsInTag { get; init; } = "No posts are tagged '{tag}'.";

    /// <summary>Search status shown when there are no posts to search.</summary>
    public string SearchStatusNoPosts { get; init; } = "No posts to search yet.";

    /// <summary>Search scope note shown when a tag filter is active. Placeholder: <c>{tag}</c>.</summary>
    public string SearchScopeTag { get; init; } = "Filtering by tag '{tag}'.";

    /// <summary>Search status shown when the search index fails to load.</summary>
    public string SearchStatusLoadError { get; init; } = "Could not load the search index. Please try again later.";

    /// <summary>The default English set.</summary>
    public static SiteText English { get; } = new();

    /// <summary>A Japanese set.</summary>
    public static SiteText Japanese { get; } = new()
    {
        MenuLabel = "メニュー",
        SiteNavigationLabel = "サイトナビゲーション",
        SearchInputLabel = "記事を検索",
        SearchButtonLabel = "検索",
        HeaderSearchPlaceholder = "記事を検索…",
        TableOfContentsHeading = "この記事の内容",
        TableOfContentsEmpty = "本文の見出しはありません。",
        IndexLede = "PageSharp で構築した静的サイトです。",
        IndexEmpty = "まだ記事はありません。",
        LatestPostHeading = "最新の記事",
        OlderPostsHeading = "過去記事",
        OlderPostsEmpty = "過去記事はまだありません。",
        ArchivesHeading = "アーカイブ",
        ArchivesEmpty = "アーカイブできる記事はまだありません。",
        TagsHeading = "タグ",
        TagsIntro = "タグから記事を探せます。",
        ExistingTagsHeading = "利用できるタグ",
        TagsEmpty = "まだ利用できるタグはありません。",
        SearchHeading = "記事を検索",
        SearchIntro = "タイトル・要約・タグ・本文をまとめて検索します。",
        SearchInputPlaceholder = "キーワードを入力",
        SearchLoading = "検索インデックスを読み込んでいます…",
        SearchNoscriptPrefix = "検索を利用するには JavaScript を有効にしてください。",
        SearchNoscriptArchivesLinkText = "アーカイブ",
        SearchNoscriptSuffix = "から記事を閲覧できます。",
        LlmsPostsHeading = "記事",
        SearchStatusBrowseAll = "{count} 件の記事を検索できます。",
        SearchStatusHits = "{count} 件ヒットしました。",
        SearchStatusHitsInTag = "タグ「{tag}」の中で {count} 件ヒットしました。",
        SearchStatusShowingTopSuffix = "（上位 {shown} 件を表示）",
        SearchStatusTaggedPosts = "タグ「{tag}」が付いた記事を {count} 件表示しています。",
        SearchStatusNoMatch = "「{query}」に一致する記事は見つかりませんでした。",
        SearchStatusNoMatchInTag = "タグ「{tag}」の中に「{query}」に一致する記事は見つかりませんでした。",
        SearchStatusNoPostsInTag = "タグ「{tag}」が付いた記事は見つかりませんでした。",
        SearchStatusNoPosts = "記事がありません。",
        SearchScopeTag = "タグ「{tag}」で絞り込み中です。",
        SearchStatusLoadError = "検索インデックスを読み込めませんでした。時間をおいて再度お試しください。",
    };
}
