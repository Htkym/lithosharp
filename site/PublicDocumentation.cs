using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Routing;
using Syntamark;

internal static class PublicDocumentation
{
    private sealed record Source(string Locale, string Path, string Title, string Hash, string Collection, string Id, bool Historical)
    {
        internal string Version => Historical ? "2.2" : "3.0";
    }

    internal static IReadOnlyList<SiteGeneratedAsset> Register(DocumentationSite docs, string siteRoot, string inputDirectory, string baseUrl)
    {
        var repository = Path.GetFullPath("..", siteRoot);
        var inventoryBytes = File.ReadAllBytes(Path.Combine(siteRoot, "documentation-inventory.json"));
        using var inventory = JsonDocument.Parse(inventoryBytes);
        inputDirectory = Path.Combine(inputDirectory, Convert.ToHexStringLower(SHA256.HashData(
            [.. inventoryBytes, .. Encoding.UTF8.GetBytes(baseUrl)])));
        var sources = inventory.RootElement.GetProperty("entries").EnumerateArray().SelectMany(entry => new[] { "en", "ja" }.Select(locale =>
        {
            var source = entry.GetProperty("source").GetProperty(locale);
            return new Source(locale, source.GetProperty("path").GetString()!, source.GetProperty("title").GetString()!,
                source.GetProperty("sha256").GetString()!, entry.GetProperty("collection").GetString()!, entry.GetProperty("id").GetString()!,
                entry.GetProperty("historical").GetBoolean());
        })).ToArray();
        var routes = sources.ToDictionary(source => source.Path, source => SiteRoute.ForDirectoryIndex(
            $"{source.Collection}/{source.Locale}/{source.Id}", baseUrl), StringComparer.OrdinalIgnoreCase);
        var reviews = ReadReviews(siteRoot);
        var revisions = sources.Select(source => new DocumentRevision(new(source.Collection, source.Version, source.Locale, source.Id), source.Path, source.Hash)).ToArray();
        var relationships = sources.Where(source => source.Locale == "ja").ToDictionary(source => new DocumentKey(source.Collection, source.Version, "ja", source.Id),
            source => new DocumentKey(source.Collection, source.Version, "en", source.Id));
        var reviewResults = DocumentTranslationReviewResult.Inspect(revisions, relationships, reviews, isComplete: true);
        if (reviewResults.Any(result => result.State != DocumentTranslationReviewState.Current))
            throw new InvalidDataException("Canonical documentation needs an explicit translation review: "
                + string.Join(", ", reviewResults.Where(result => result.State != DocumentTranslationReviewState.Current).Select(result => result.Translation)));
        var assets = new Dictionary<string, SiteGeneratedAsset>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var path = Path.GetFullPath(source.Path, repository);
            var bytes = File.ReadAllBytes(path);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != source.Hash)
                throw new InvalidDataException("Public documentation changed after its inventory/review: " + source.Path);
            var text = WebsiteDocumentationLayout.ProjectSource(source.Path, new UTF8Encoding(false, true).GetString(bytes));
            var analysis = MarkdownParser.Analyze(text, MarkdownAnalysisParts.Links, "website-reference", source.Path, source.Hash);
            if (analysis.Status != MarkdownParseStatus.Complete || analysis.Links is null
                || analysis.GetAvailability(MarkdownAnalysisParts.Links) != MarkdownAnalysisAvailability.Complete)
                throw new InvalidDataException("Canonical documentation links are unavailable: " + source.Path);
            var replacements = new Dictionary<(int Start, int Length), string>();
            foreach (var link in analysis.Links)
            {
                if (link.Target is not { } target || link.TargetRawSpan is not { } span || target.StartsWith('#'))
                    continue;
                const string repositoryUrl = "https://github.com/Htkym/lithosharp/blob/main/";
                var fromRepositoryUrl = target.StartsWith(repositoryUrl, StringComparison.Ordinal);
                if (target.Contains("://", StringComparison.Ordinal) && !fromRepositoryUrl)
                    continue;
                var hash = target.IndexOfAny(['?', '#']);
                var local = hash < 0 ? target : target[..hash];
                var fragment = hash < 0 ? "" : target[hash..];
                var physical = fromRepositoryUrl ? Path.GetFullPath(Uri.UnescapeDataString(local[repositoryUrl.Length..]), repository)
                    : Path.GetFullPath(Uri.UnescapeDataString(local), Path.GetDirectoryName(path)!);
                var relative = Path.GetRelativePath(repository, physical).Replace('\\', '/');
                if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                    throw new InvalidDataException("Documentation link leaves the repository: " + target);
                if (!File.Exists(physical) && !Directory.Exists(physical))
                    throw new InvalidDataException("Documentation link has no source target: " + source.Path + " -> " + target);
                string url;
                if (routes.TryGetValue(relative, out var route))
                    url = SiteUrl.FromRoute(route).Value + fragment;
                else if (link.Image && File.Exists(physical))
                {
                    var image = File.ReadAllBytes(physical);
                    var digest = Convert.ToHexStringLower(SHA256.HashData(image));
                    var output = "reference-assets/" + digest + Path.GetExtension(physical).ToLowerInvariant();
                    assets.TryAdd(output, new("reference:" + digest, output, image, [BuildInput.FromFile(relative, digest)]));
                    url = SiteUrl.ForFile(output, baseUrl).Value + fragment;
                }
                else
                    url = (Directory.Exists(physical) ? "https://github.com/Htkym/lithosharp/tree/main/" : repositoryUrl)
                        + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)) + fragment;
                if (replacements.TryGetValue((span.Start, span.Length), out var previous) && previous != url)
                    throw new InvalidDataException("Ambiguous parsed documentation link: " + source.Path);
                replacements[(span.Start, span.Length)] = url;
            }
            foreach (var replacement in replacements.OrderByDescending(pair => pair.Key.Start))
                text = text.Remove(replacement.Key.Start, replacement.Key.Length).Insert(replacement.Key.Start, replacement.Value);
            var directory = Path.Combine(inputDirectory, source.Collection, source.Locale);
            Directory.CreateDirectory(directory);
            var header = "---\nid: " + JsonSerializer.Serialize(source.Id) + "\ntitle: " + JsonSerializer.Serialize(source.Title)
                + "\nsupported_version: " + JsonSerializer.Serialize(source.Version)
                + "\ncustom_edit_url: " + JsonSerializer.Serialize("https://github.com/Htkym/lithosharp/blob/main/" + source.Path) + "\n---\n\n";
            File.WriteAllText(Path.Combine(directory, source.Id + ".md"), header + text.TrimStart('\uFEFF'), new UTF8Encoding(false));
        }
        Directory.CreateDirectory(inputDirectory);
        File.WriteAllText(Path.Combine(inputDirectory, "canonical-translation-reviews.json"), JsonSerializer.Serialize(reviewResults));
        foreach (var collection in sources.Select(source => source.Collection).Distinct(StringComparer.Ordinal))
            docs.AddCollection(new DocumentationCollection(collection, new[] { "en", "ja" }.Select(locale => new DocumentVariant(
                sources.First(source => source.Collection == collection).Version, locale, Path.Combine(inputDirectory, collection, locale), collection + "/" + locale)).ToArray())
            {
                SourceLocale = "en", MissingDocuments = MissingDocumentPolicy.Error, SemanticRules = DocumentSemanticRuleSet.Documentation,
                TrackTranslationReviews = false
            });
        return assets.Values.ToArray();
    }

    private static IReadOnlyList<DocumentTranslationReview> ReadReviews(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "reference-translation-reviews.json")));
        return document.RootElement.GetProperty("reviews").EnumerateArray().Select(review =>
        {
            var collection = review.GetProperty("collection").GetString()!;
            var id = review.GetProperty("id").GetString()!;
            var version = review.GetProperty("version").GetString()!;
            return new DocumentTranslationReview(
                new(new(collection, version, "en", id), review.GetProperty("sourcePath").GetString()!, review.GetProperty("sourceSha256").GetString()!),
                new(new(collection, version, "ja", id), review.GetProperty("translationPath").GetString()!, review.GetProperty("translationSha256").GetString()!));
        }).ToArray();
    }
}
