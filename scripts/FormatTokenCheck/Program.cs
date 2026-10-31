using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

if (args.Length != 3)
    throw new ArgumentException("Expected baseline root, checkout root, report path.");
var baseline = Path.GetFullPath(args[0]);
var checkout = Path.GetFullPath(args[1]);
var files = Directory.GetFiles(baseline, "*.cs", SearchOption.AllDirectories);
var rows = new List<object>();
var failures = new List<string>();
foreach (var beforePath in files.Order())
{
    var relative = Path.GetRelativePath(baseline, beforePath);
    var afterPath = Path.Combine(checkout, relative);
    if (!File.Exists(afterPath))
        throw new InvalidOperationException($"Missing source: {relative}");
    var before = File.ReadAllText(beforePath);
    var after = File.ReadAllText(afterPath);
    var options = new CSharpParseOptions(LanguageVersion.Preview);
    var conditionalAcceptance = relative.Replace('\\', '/') == "tests/LithoSharp.Tests/CompilerDiagnosticPreflightTests.cs";
    var contexts = conditionalAcceptance
        ? new[] { options, options.WithPreprocessorSymbols("COMPILER_DIAGNOSTIC_ACCEPTANCE") }
        : new[] { options };
    var tokensEqual = true;
    var triviaEqual = true;
    foreach (var context in contexts)
    {
        var oldRoot = CSharpSyntaxTree.ParseText(before, context).GetRoot();
        var newRoot = CSharpSyntaxTree.ParseText(after, context).GetRoot();
        // Both contexts are checked. Only this known whole-file conditional is covered
        // by the enabled context; no other disabled branches may be skipped.
        if (conditionalAcceptance)
        {
            foreach (var root in new[] { oldRoot, newRoot })
            {
                var directives = root.DescendantTrivia().Where(t => t.IsDirective).ToArray();
                var starts = directives.Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)).ToArray();
                if (starts.Length != 1 || starts[0].GetStructure() is not Microsoft.CodeAnalysis.CSharp.Syntax.IfDirectiveTriviaSyntax directive
                    || directive.Condition.ToString() != "COMPILER_DIAGNOSTIC_ACCEPTANCE"
                    || directives.Count(t => t.IsKind(SyntaxKind.EndIfDirectiveTrivia)) != 1
                    || directives.Any(t => t.IsKind(SyntaxKind.ElseDirectiveTrivia) || t.IsKind(SyntaxKind.ElifDirectiveTrivia)))
                    throw new InvalidOperationException("Unexpected conditional structure: " + relative);
                if (context.PreprocessorSymbolNames.Any() && root.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.DisabledTextTrivia)))
                    throw new InvalidOperationException("Acceptance context left an unchecked disabled branch: " + relative);
            }
        }
        tokensEqual &= oldRoot.DescendantTokens().Select(t => (t.RawKind, t.Text))
            .SequenceEqual(newRoot.DescendantTokens().Select(t => (t.RawKind, t.Text)));
        bool CompareTrivia(SyntaxTrivia t) => IsSignificant(t)
            && !(conditionalAcceptance && !context.PreprocessorSymbolNames.Any() && t.IsKind(SyntaxKind.DisabledTextTrivia));
        triviaEqual &= oldRoot.DescendantTrivia().Where(CompareTrivia).Select(t => (t.RawKind, t.ToFullString()))
            .SequenceEqual(newRoot.DescendantTrivia().Where(CompareTrivia).Select(t => (t.RawKind, t.ToFullString())));
    }
    if (!tokensEqual || !triviaEqual)
        failures.Add(relative);
    rows.Add(new { path = relative.Replace('\\', '/'), changed = before != after, tokensEqual, significantTriviaEqual = triviaEqual,
        beforeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(beforePath))),
        afterSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(afterPath))) });
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllText(args[2], JsonSerializer.Serialize(new { passed = failures.Count == 0, checkedFiles = rows.Count,
    failedFiles = failures, tokenComparison = "RawKind and Text; raw/interpolated string tokens included",
    sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"), files = rows }, new JsonSerializerOptions { WriteIndented = true }));
if (failures.Count != 0)
    throw new InvalidOperationException("Token/non-whitespace trivia differences: " + string.Join(", ", failures));
Console.WriteLine($"PASS: {rows.Count} files, all token texts and non-whitespace trivia preserved.");

static bool IsSignificant(SyntaxTrivia trivia) => !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia);
