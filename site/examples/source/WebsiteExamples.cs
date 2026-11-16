using LithoSharp.Documentation;

namespace LithoSharp.Website.Examples;

/// <summary>Functions shared by the published snippets and their explicitly mapped tests.</summary>
public static class WebsiteExamples
{
    #region PublishedVersion
    /// <summary>Checks an explicitly supplied supported version for published content.</summary>
    /// <param name="draft">Whether publication is disabled.</param>
    /// <param name="supportedVersion">The declared product version.</param>
    /// <returns>True when a draft or a document with a nonblank version is supplied.</returns>
    public static bool HasPublishedVersion(bool draft, string? supportedVersion)
    {
        var rules = new DocumentSemanticRuleSet([DocumentSemanticRule.RequiredWhenPublished("supported_version")]);
        var document = new DocumentSemanticDocument(new("examples", "3.0", "en", "sample"), "sample.md",
            new Dictionary<string, object?> { ["supported_version"] = supportedVersion },
            draft ? DocumentSemanticPublication.Unpublished : DocumentSemanticPublication.Published, isComplete: true);
        return rules.Evaluate(document).Single().State is DocumentSemanticState.Passed or DocumentSemanticState.NotApplicable;
    }
    #endregion
}
