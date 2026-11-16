using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;

namespace LithoSharp.Documentation;

/// <summary>The exact source bytes and identity used when reviewing one document.</summary>
public sealed class DocumentRevision
{
    /// <summary>Captures a SHA-256 fingerprint without reading or changing the document.</summary>
    public DocumentRevision(DocumentKey key, string sourcePath, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = new Content.ContentCollectionId(key.Collection);
        _ = new Content.ContentEntryId(key.Version);
        _ = new Content.ContentEntryId(key.Id);
        _ = System.Globalization.CultureInfo.GetCultureInfo(key.Locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(contentHash);
        if (contentHash.Length != 64 || !contentHash.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A document revision requires a SHA-256 hash of its exact source bytes.", nameof(contentHash));
        }
        Key = key;
        SourcePath = sourcePath;
        ContentHash = contentHash.ToLowerInvariant();
    }

    public DocumentKey Key { get; }
    public string SourcePath { get; }
    public string ContentHash { get; }
}

/// <summary>An explicit review of a translation against one exact source revision.</summary>
public sealed class DocumentTranslationReview
{
    /// <summary>Records review completion. Builds never call this operation automatically.</summary>
    public DocumentTranslationReview(DocumentRevision source, DocumentRevision translation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translation);
        if (source.Key == translation.Key)
        {
            throw new ArgumentException("A translation must have a distinct document identity.", nameof(translation));
        }
        Source = source;
        Translation = translation;
    }

    public DocumentRevision Source { get; }
    public DocumentRevision Translation { get; }
}

/// <summary>The state of one explicitly declared source/translation relationship.</summary>
public enum DocumentTranslationReviewState
{
    Current,
    Unreviewed,
    SourceChanged,
    TranslationChanged,
    SourceReassigned,
    SourceMissing,
    TranslationMissing,
    Unavailable
}

/// <summary>A review assessment against a complete or explicitly incomplete input snapshot.</summary>
public sealed class DocumentTranslationReviewResult
{
    private DocumentTranslationReviewResult(DocumentKey source, DocumentKey translation, DocumentTranslationReviewState state,
        DocumentRevision? sourceRevision, DocumentRevision? translationRevision)
    {
        Source = source;
        Translation = translation;
        State = state;
        SourceRevision = sourceRevision;
        TranslationRevision = translationRevision;
        if (state != DocumentTranslationReviewState.Current)
        {
            Diagnostic = new("LSDOC203", SiteDiagnosticSeverity.Warning,
                $"Translation review is {state}: {translation}. Source: {source}.",
                translationRevision is null ? null : new SiteSourceLocation(translationRevision.SourcePath),
                "translation-review", sourceRevision is null ? [] : [new SiteSourceLocation(sourceRevision.SourcePath)]);
        }
    }

    public DocumentKey Source { get; }
    public DocumentKey Translation { get; }
    public DocumentTranslationReviewState State { get; }
    public DocumentRevision? SourceRevision { get; }
    public DocumentRevision? TranslationRevision { get; }
    public SiteDiagnostic? Diagnostic { get; }

    /// <summary>Assesses declared relationships without advancing review hashes or executing code.</summary>
    /// <remarks>An incomplete snapshot cannot establish that a document is missing.</remarks>
    public static IReadOnlyList<DocumentTranslationReviewResult> Inspect(
        IEnumerable<DocumentRevision> documents, IReadOnlyDictionary<DocumentKey, DocumentKey> sources,
        IEnumerable<DocumentTranslationReview> reviews, bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(reviews);
        var snapshot = documents.ToDictionary(document => document.Key);
        var recorded = reviews.ToDictionary(review => review.Translation.Key);
        var results = new List<DocumentTranslationReviewResult>();
        foreach (var pair in sources.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            var translation = pair.Key;
            var source = pair.Value;
            snapshot.TryGetValue(source, out var sourceRevision);
            snapshot.TryGetValue(translation, out var translationRevision);
            recorded.TryGetValue(translation, out var review);
            var state = sourceRevision is null || translationRevision is null
                ? !isComplete ? DocumentTranslationReviewState.Unavailable
                    : sourceRevision is null ? DocumentTranslationReviewState.SourceMissing : DocumentTranslationReviewState.TranslationMissing
                : review is null ? DocumentTranslationReviewState.Unreviewed
                : review.Source.Key != source ? DocumentTranslationReviewState.SourceReassigned
                : review.Source.ContentHash != sourceRevision.ContentHash ? DocumentTranslationReviewState.SourceChanged
                : review.Translation.ContentHash != translationRevision.ContentHash ? DocumentTranslationReviewState.TranslationChanged
                : DocumentTranslationReviewState.Current;
            results.Add(new(source, translation, state, sourceRevision, translationRevision));
        }
        return new ReadOnlyCollection<DocumentTranslationReviewResult>(results);
    }
}
