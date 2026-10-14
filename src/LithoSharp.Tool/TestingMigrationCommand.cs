using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Internal;
using LithoSharp.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Tool;

internal static class TestingMigrationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var files = new List<string>();
        var references = new List<string>();
        var dryRun = false;
        var format = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dry-run" && !dryRun) { dryRun = true; continue; }
            if (args[i] is not ("--source" or "--reference" or "--format") || i + 1 == args.Length)
                throw new CliUsageException("Testing migration requires --dry-run, explicit --source and --reference files; write/project options are unsupported.");
            var name = args[i++];
            if (name == "--source") files.Add(Path.GetFullPath(args[i]));
            else if (name == "--reference") references.Add(Path.GetFullPath(args[i]));
            else if (args[i] != "json" || format) throw new CliUsageException("Testing migration supports one --format json option.");
            else format = true;
        }
        if (!dryRun || files.Count == 0 || references.Count == 0)
            throw new CliUsageException("Usage: migrate testing --dry-run --source file.cs --reference metadata.dll [--format json]");
        if (files.Any(file => !Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)))
            throw new CliUsageException("Testing migration accepts explicit .cs sources, not projects or scripts.");

        var inputs = new List<Input>();
        foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, token);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var offset = text.StartsWith('\uFEFF') ? 1 : 0;
            inputs.Add(new(file, Hash(bytes), SourceText.From(text, Encoding.UTF8), offset,
                CSharpSyntaxTree.ParseText(text[offset..], path: file, encoding: Encoding.UTF8, cancellationToken: token)));
        }
        var metadata = new List<MetadataReference>();
        var metadataInputs = new List<HostTestingMigrationInput>();
        // Only the trusted runtime framework, not ambient application/Testing assemblies.
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            ?? throw new InvalidOperationException("Runtime framework metadata is unavailable.");
        metadata.AddRange(platform.Where(file => Path.GetFileName(file).StartsWith("System.", StringComparison.Ordinal)
            || Path.GetFileName(file) is "netstandard.dll" or "Microsoft.CSharp.dll").Select(file => MetadataReference.CreateFromFile(file)));
        foreach (var file in references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, token);
            metadata.Add(MetadataReference.CreateFromImage(bytes, filePath: file));
            metadataInputs.Add(new(file, Hash(bytes)));
        }
        var compilation = CSharpCompilation.Create("TestingMigrationInput", inputs.Select(input => input.Tree), metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var compilerDiagnostics = compilation.GetDiagnostics(token).Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
        var warnings = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MigrationAnalyzer()))
            .GetAnalyzerDiagnosticsAsync(token);
        var findings = new List<HostTestingMigrationFinding>();
        var legacy = LegacyTestingDomContract.FindProperty(compilation);
        using var grammar = SiteTestDocument.Parse(string.Empty);
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(input.Tree);
            var root = await input.Tree.GetRootAsync(token);
            // Explicit CLI inputs are inspected even when IDE warnings are suppressed or generated-code policy excludes them.
            foreach (var syntax in root.DescendantNodes().Where(node => node is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax))
            {
                if (legacy is null || model.GetSymbolInfo(syntax, token).Symbol is not IPropertySymbol property
                    || !SymbolEqualityComparer.Default.Equals(property.OriginalDefinition, legacy)) continue;
                if (syntax is not MemberAccessExpressionSyntax access)
                {
                    findings.Add(Finding(input, syntax.Span, syntax.Span,
                        "Manual", "read_shape_not_supported", null, LegacyTestingDomContract.Rule.Id));
                    continue;
                }
                var (classification, reason, preview, expression) = Classify(access, model, grammar.Snapshot, token);
                findings.Add(Finding(input, access.Span, expression.Span, classification, reason, preview, LegacyTestingDomContract.Rule.Id));
            }
            // Compiler errors are evidence of missing binding, never proof of a legacy API.
            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(access => access.Name.Identifier.ValueText == "Document"))
                if (model.GetSymbolInfo(access, token).Symbol is null
                    && model.GetTypeInfo(access.Expression, token).Type is INamedTypeSymbol owner
                    && owner.ToDisplayString() == "LithoSharp.Testing.SiteTestDocument"
                    && owner.ContainingAssembly.Identity.Name == "LithoSharp.Testing"
                    && owner.Locations.All(location => location.IsInMetadata))
                    findings.Add(Finding(input, access.Span, access.Span, "Unknown", "legacy_binding_unavailable", null, null));
        }
        var diagnostics = compilerDiagnostics.Concat(warnings).OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id, StringComparer.Ordinal).Select(d =>
            {
                var input = inputs.SingleOrDefault(input => input.Tree == d.Location.SourceTree);
                var span = input is null ? default : Shift(d.Location.SourceSpan, input.Offset);
                var line = input?.Text.Lines.GetLinePositionSpan(span);
                return new HostDiagnostic(d.Id, d.Severity.ToString(), d.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    input?.File, line?.Start.Line + 1, line?.Start.Character + 1,
                    line?.End.Line + 1, line?.End.Character + 1, d.Descriptor.Category);
            }).ToArray();
        var succeeded = !compilerDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        var response = new HostResponse
        {
            Success = succeeded, ExitCode = succeeded ? 0 : 1, Diagnostics = diagnostics,
            Migration = new(inputs.Select(input => new HostTestingMigrationInput(input.File, input.Hash)).ToArray(),
                metadataInputs.ToArray(), findings.OrderBy(finding => finding.File, StringComparer.Ordinal).ThenBy(finding => finding.Start).ToArray(),
                succeeded && findings.All(finding => finding.Classification != "Unknown") ? "Complete" : "Partial")
        };
        token.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return response.ExitCode;
    }

    private static (string Classification, string Reason, string? Preview, ExpressionSyntax Expression) Classify(
        MemberAccessExpressionSyntax access, SemanticModel model, HtmlTestDocument grammar, CancellationToken token)
    {
        ExpressionSyntax expression = access;
        while (expression.Parent is ExpressionSyntax parent && (parent switch
        {
            MemberAccessExpressionSyntax member => member.Expression == expression,
            InvocationExpressionSyntax call => call.Expression == expression,
            ElementAccessExpressionSyntax element => element.Expression == expression,
            ConditionalAccessExpressionSyntax conditional => conditional.Expression == expression,
            ParenthesizedExpressionSyntax parentheses => parentheses.Expression == expression,
            PostfixUnaryExpressionSyntax suppression => suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression),
            _ => false,
        })) expression = parent;
        if (expression.Parent is CastExpressionSyntax or IsPatternExpressionSyntax
            || expression.Parent is BinaryExpressionSyntax binary && binary.Kind() is SyntaxKind.AsExpression or SyntaxKind.IsExpression)
            return ("Manual", "dom_cast", null, expression);
        if (expression.Parent is AssignmentExpressionSyntax assignment && assignment.Left == expression
            || expression.Parent is PrefixUnaryExpressionSyntax prefix && prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            || expression.Parent is PostfixUnaryExpressionSyntax postfix && postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression)
            return ("Manual", "dom_mutation", null, expression);
        if (expression == access) return ("Manual", "dom_alias_or_escape", null, expression);
        // QueryAll's collection/indexer contract is not a proved read mapping; retain the full outer use for review.
        if (expression.DescendantNodesAndSelf().Any(node => node is ElementAccessExpressionSyntax or ElementBindingExpressionSyntax))
            return ("Manual", "read_shape_not_supported", null, expression);

        var renames = new Dictionary<SimpleNameSyntax, string> { [access.Name] = "Snapshot" };
        var members = expression.DescendantNodesAndSelf().Where(node => node is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax)
            .Where(node => node != access && (node is MemberBindingExpressionSyntax
                || ((MemberAccessExpressionSyntax)node).Expression.DescendantNodesAndSelf().Contains(access)))
            .OrderBy(node => node is MemberAccessExpressionSyntax member ? member.Name.Span.Start : ((MemberBindingExpressionSyntax)node).Name.Span.Start);
        var queried = false;
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested();
            var name = member is MemberAccessExpressionSyntax qualified ? qualified.Name : ((MemberBindingExpressionSyntax)member).Name;
            var symbol = model.GetSymbolInfo(member, token).Symbol;
            if (symbol is null) return ("Unknown", "legacy_binding_unavailable", null, expression);
            if (symbol?.ContainingAssembly?.Identity.Name != "AngleSharp" || symbol.Locations.Any(location => location.IsInSource))
                return ("Manual", "custom_or_unproved_dom_use", null, expression);
            if (name.Identifier.ValueText is "SetAttribute" or "RemoveAttribute" or "AppendChild" or "RemoveChild" or "InsertBefore" or "ReplaceChild")
                return ("Manual", "dom_mutation", null, expression);
            if (symbol is IMethodSymbol method)
            {
                if (method.IsExtensionMethod || method.IsGenericMethod || method.Parameters.Length != 1
                    || name.Identifier.ValueText is not ("QuerySelector" or "QuerySelectorAll" or "GetAttribute")
                    || member.Parent is not InvocationExpressionSyntax call || call.ArgumentList.Arguments.Count != 1)
                    return ("Manual", "read_shape_not_supported", null, expression);
                var value = model.GetConstantValue(call.ArgumentList.Arguments[0].Expression, token);
                if (!value.HasValue || value.Value is not string text)
                    return ("Unknown", name.Identifier.ValueText == "GetAttribute" ? "attribute_name_not_constant" : "selector_not_constant", null, expression);
                if (call.ArgumentList.Arguments[0].NameColon is { } named)
                    renames.Add(named.Name, name.Identifier.ValueText == "GetAttribute" ? "name" : "selector");
                if (name.Identifier.ValueText != "GetAttribute")
                {
                    try { grammar.Query(text, token); }
                    catch (HtmlSelectorUnsupportedException) { return ("Manual", "selector_unsupported", null, expression); }
                    catch (ArgumentException) { return ("Manual", "selector_invalid", null, expression); }
                    renames.Add(name, name.Identifier.ValueText == "QuerySelector" ? "Query" : "QueryAll");
                    queried = true;
                }
                else if (!queried) return ("Manual", "read_shape_not_supported", null, expression);
            }
            else if (symbol is IPropertySymbol)
            {
                if (name.Identifier.ValueText is "TextContent" or "Children") continue;
                if (name.Identifier.ValueText == "Title" && !queried) continue;
                if (!queried) return ("Manual", "read_shape_not_supported", null, expression);
                var replacement = name.Identifier.ValueText switch
                {
                    "ParentElement" => "Parent", "PreviousElementSibling" => "PreviousSibling",
                    "NextElementSibling" => "NextSibling", _ => null,
                };
                if (replacement is null) return ("Manual", "read_shape_not_supported", null, expression);
                renames.Add(name, replacement);
            }
            else return ("Unknown", "legacy_binding_unavailable", null, expression);
        }
        // Only a directly initialized ordinary local is a recognized read boundary.
        // Unknown surrounding operators/calls must not inherit a candidate from an inner DOM read.
        if (expression.Parent is not EqualsValueClauseSyntax initializer
            || initializer.Parent is not VariableDeclaratorSyntax declaration
            || declaration.Parent?.Parent is not LocalDeclarationStatementSyntax local
            || local.UsingKeyword.RawKind != 0)
        {
            while (expression.Parent is ExpressionSyntax outer) expression = outer;
            return ("Manual", "outer_use_not_supported", null, expression);
        }
        var preview = expression.ReplaceNodes(renames.Keys,
            (original, _) => SyntaxFactory.IdentifierName(renames[original]).WithTriviaFrom(original)).ToString();
        return ("ReadCandidate", "supported_read_requires_review", preview, expression);
    }

    private static HostTestingMigrationFinding Finding(Input input, TextSpan access, TextSpan expression,
        string classification, string reason, string? preview, string? diagnosticId)
    {
        var span = Shift(access, input.Offset);
        var whole = Shift(expression, input.Offset);
        var line = input.Text.Lines.GetLinePositionSpan(span);
        return new(input.File, input.Hash, span.Start, span.Length, line.Start.Line + 1, line.Start.Character + 1,
            line.End.Line + 1, line.End.Character + 1, classification, reason, diagnosticId,
            whole.Start, whole.Length, input.Text.ToString(whole), preview);
    }
    private static TextSpan Shift(TextSpan span, int offset) => new(span.Start + offset, span.Length);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record Input(string File, string Hash, SourceText Text, int Offset, SyntaxTree Tree);
    private sealed class MigrationAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(LegacyTestingDomContract.Rule);
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(LegacyTestingDomContract.Register);
        }
    }
}

internal sealed record HostTestingMigrationInput(string File, string SourceHash);
internal sealed record HostTestingMigrationFinding(string File, string SourceHash, int Start, int Length,
    int Line, int Column, int EndLine, int EndColumn, string Classification, string Reason, string? DiagnosticId,
    int ExpressionStart, int ExpressionLength, string Expression, string? Preview);
internal sealed record HostTestingMigrationReport(HostTestingMigrationInput[] Sources,
    HostTestingMigrationInput[] References, HostTestingMigrationFinding[] Findings, string BindingCoverage)
{
    public bool DryRun => true;
    public string Coverage => "Partial";
    public string Scope => "Explicit C# sources; bound legacy Document uses and unresolved metadata-owner accesses; no alias flow or automatic conversion proof.";
}
