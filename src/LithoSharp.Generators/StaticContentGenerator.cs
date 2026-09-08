using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using LithoSharp.Routing;

namespace LithoSharp.Generators;

/// <summary>Generates binders, schemas, and references for explicitly declared static content.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class StaticContentGenerator : IIncrementalGenerator
{
    internal static readonly DiagnosticDescriptor InvalidDeclaration = Rule("LSG001", "Invalid static collection declaration");
    private static readonly DiagnosticDescriptor MissingInput = Rule("LSG002", "Invalid static collection input");
    private static readonly DiagnosticDescriptor DuplicateId = Rule("LSG003", "Duplicate static collection identity");
    private static readonly DiagnosticDescriptor InvalidRoute = Rule("LSG004", "Invalid static collection route");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            "LithoSharp.Content.StaticContentCollectionAttribute", static (node, _) => node is ClassDeclarationSyntax,
            static (attribute, _) => attribute);
        var files = context.AdditionalTextsProvider.Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellation) => ReadInput(pair.Left, pair.Right, cancellation));
        context.RegisterSourceOutput(declarations.Collect().Combine(files.Collect()),
            static (output, inputs) => Generate(output, inputs.Left, inputs.Right));
    }

    private static Input ReadInput(AdditionalText text, AnalyzerConfigOptionsProvider provider, System.Threading.CancellationToken cancellation)
    {
        var metadata = provider.GetOptions(text);
        string Get(string key) => metadata.TryGetValue("build_metadata.AdditionalFiles." + key, out var value) ? value : string.Empty;
        provider.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var projectDirectory);
        return new Input(text.Path, text.GetText(cancellation), Get("LithoSharpCollection"), Get("LithoSharpId"), Get("LithoSharpRoute"), projectDirectory ?? string.Empty);
    }

    private static void Generate(SourceProductionContext output, ImmutableArray<GeneratorAttributeSyntaxContext> attributes, ImmutableArray<Input> files)
    {
        var collections = new List<Collection>();
        foreach (var attribute in attributes)
        {
            var location = attribute.TargetNode.GetLocation();
            if (attribute.TargetSymbol is not INamedTypeSymbol symbol || !symbol.IsStatic || symbol.IsFileLocal || symbol.ContainingType is not null
                || symbol.Arity != 0 || !symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(output.CancellationToken) is ClassDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                Report(output, InvalidDeclaration, location, "The declaration must be a top-level, nongeneric static partial class.");
                continue;
            }
            var arguments = attribute.Attributes[0].ConstructorArguments;
            if (arguments.Length != 3 || arguments[0].Value is not INamedTypeSymbol front || arguments[1].Value is not ITypeSymbol page || arguments[2].Value is not string rawId)
            {
                Report(output, InvalidDeclaration, location, "The attribute requires front matter and page types and a collection ID.");
                continue;
            }
            if (!TryId(rawId, out var id) || GeneratorModel.IsScalar(front) || front.IsUnboundGenericType || front.IsRefLikeType
                || front.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                || GeneratorModel.IsList(front) || GeneratorModel.IsDictionary(front)
                || page.SpecialType == SpecialType.System_Void || page.TypeKind is TypeKind.Pointer or TypeKind.Error
                || page is INamedTypeSymbol { IsUnboundGenericType: true } or { IsRefLikeType: true }
                || page.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                Report(output, InvalidDeclaration, location, "The collection ID or declared types are invalid.");
                continue;
            }
            if (collections.Any(collection => collection.Id == id))
            {
                Report(output, DuplicateId, location, "Duplicate collection ID '" + id + "'.");
                continue;
            }
            try
            {
                var emitter = new BinderEmitter(front);
                var emitSchema = attribute.Attributes[0].NamedArguments.Any(pair => pair.Key == "EmitJsonSchema" && pair.Value.Value is true);
                var body = attribute.Attributes[0].NamedArguments.FirstOrDefault(pair => pair.Key == "BodyType").Value.Value as ITypeSymbol;
                if (body is not null && (body.SpecialType == SpecialType.System_Void || body.TypeKind is TypeKind.Pointer or TypeKind.Error || body is INamedTypeSymbol { IsUnboundGenericType: true } or { IsRefLikeType: true }))
                    throw new InvalidOperationException("The content body must be a closed, non-ref type.");
                collections.Add(new Collection(symbol, front, page, id, emitSchema, emitter, body));
            }
            catch (InvalidOperationException failure) { Report(output, InvalidDeclaration, location, failure.Message); }
        }

        var routes = new List<SiteRoute>();
        foreach (var input in files)
        {
            if (input.Collection.Length == 0 && input.Id.Length == 0 && input.Route.Length == 0) continue;
            var collection = TryId(input.Collection, out var collectionId)
                ? collections.FirstOrDefault(item => item.Id == collectionId) : null;
            if (collection is null || input.Text is null || !TryId(input.Id, out var entryId) || input.Route.Length == 0)
            {
                Report(output, MissingInput, input.Location, "The input requires an existing collection, readable content, a valid entry ID, and a route.");
                continue;
            }
            string name;
            try { name = InputName(input); }
            catch (ArgumentException failure) { Report(output, MissingInput, input.Location, failure.Message); continue; }
            if (name is "Pages" or "Entries" or "Ids" || collection.Entries.Any(entry => entry.Id == entryId || entry.Name == name))
            {
                Report(output, DuplicateId, input.Location, "Duplicate entry ID or generated member name '" + name + "'.");
                continue;
            }
            SiteRoute route;
            try
            {
                route = input.Route.EndsWith("/", StringComparison.Ordinal)
                    ? SiteRoute.ForDirectoryIndex(input.Route) : SiteRoute.ForFile(input.Route);
            }
            catch (Exception failure) when (failure is ArgumentException or UriFormatException)
            {
                Report(output, InvalidRoute, input.Location, failure.Message);
                continue;
            }
            if (routes.Any(existing => Conflicts(existing.RelativeOutputPath, route.RelativeOutputPath)))
            {
                Report(output, InvalidRoute, input.Location, "The route conflicts with another static entry's output: " + route.RelativeOutputPath);
                continue;
            }
            routes.Add(route);
            StaticYamlValidation.Validate(input.Path, input.Text.ToString(), collection.Front, output.ReportDiagnostic);
            collection.Entries.Add(new Entry(name, entryId, input.Route, route.PublicPath.EndsWith("/", StringComparison.Ordinal)));
        }
        foreach (var collection in collections)
        {
            var source = Render(collection);
            using var hash = SHA256.Create();
            var suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(collection.Symbol.ToDisplayString()))).Replace("-", string.Empty);
            output.AddSource(collection.Symbol.Name + "." + suffix + ".g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string Render(Collection collection)
    {
        var text = new StringBuilder("// <auto-generated />\n#nullable enable\n");
        if (!collection.Symbol.ContainingNamespace.IsGlobalNamespace)
            text.AppendLine("namespace " + collection.Symbol.ContainingNamespace.ToDisplayString() + ";");
        text.AppendLine((collection.Symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
            + " static partial class " + GeneratorModel.Identifier(collection.Symbol.Name));
        text.AppendLine("{");
        var front = GeneratorModel.Display(collection.Front);
        var page = GeneratorModel.Display(collection.Page);
        text.AppendLine("/// <summary>Binds front matter using generated member assignments.</summary>");
        text.AppendLine($"public static global::LithoSharp.Content.IContentFrontMatterBinder<{front}> Binder {{ get; }} = new BinderImpl();");
        text.AppendLine("/// <summary>JSON Schema for this collection's front matter.</summary>");
        text.AppendLine("public const string SchemaJson = " + GeneratorModel.Literal(SchemaEmitter.Emit(collection.Front, collection.Emitter.ObjectTypes)) + ";");
        if (collection.EmitSchema)
        {
            text.AppendLine("/// <summary>Writes the schema to an explicitly selected file.</summary>");
            text.AppendLine("public static void WriteJsonSchema(string outputPath) => global::System.IO.File.WriteAllText(outputPath, SchemaJson, new global::System.Text.UTF8Encoding(false));");
        }
        foreach (var group in new[] { "Pages", "Entries", "Ids" })
        {
            text.AppendLine("/// <summary>References derived from declared static content paths.</summary>");
            text.AppendLine("public static class " + group + " {");
            foreach (var entry in collection.Entries.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var route = "global::LithoSharp.Routing.SiteRoute." + (entry.Directory ? "ForDirectoryIndex" : "ForFile") + "(" + GeneratorModel.Literal(entry.Route) + ")";
                var id = "new global::LithoSharp.Content.ContentEntryId(" + GeneratorModel.Literal(entry.Id) + ")";
                var collectionId = "new global::LithoSharp.Content.ContentCollectionId(" + GeneratorModel.Literal(collection.Id) + ")";
                var pageId = $"page:collection:{collection.Id.Length}:{collection.Id}:{entry.Id.Length}:{entry.Id}";
                var type = group == "Pages" ? $"global::LithoSharp.Pages.PageRef<{page}>"
                    : group == "Entries" ? $"global::LithoSharp.Content.ContentRef<global::LithoSharp.Content.ContentEntry<{front}, {(collection.Body is null ? "string" : GeneratorModel.Display(collection.Body))}>>"
                    : "global::LithoSharp.Content.ContentEntryId";
                var value = group == "Pages" ? "new(new global::LithoSharp.Pages.PageId(" + GeneratorModel.Literal(pageId) + "), " + route + ")"
                    : group == "Entries" ? "new(" + collectionId + ", " + id + ", " + route + ")" : id;
                text.AppendLine("/// <summary>A reference to a declared static content input.</summary>");
                text.AppendLine("public static " + type + " " + entry.Name + " { get; } = " + value + ";");
            }
            text.AppendLine("}");
        }
        text.Append(collection.Emitter.Emit(collection.Front));
        return text.AppendLine("}").ToString();
    }

    private static bool TryId(string value, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl)) return false;
        try { id = value.Normalize(NormalizationForm.FormC); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string InputName(Input input)
    {
        var relative = input.Path.Replace('\\', '/');
        var root = input.ProjectDirectory.Replace('\\', '/').TrimEnd('/');
        if (relative.Split('/').Any(segment => segment is "." or "..") || root.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Static input paths must not contain traversal segments.");
        if (root.Length != 0)
        {
            if (!relative.StartsWith(root + "/", System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("Static content files must be inside MSBuildProjectDirectory.");
            relative = relative.Substring(root.Length + 1);
        }
        else if (relative.StartsWith("/", StringComparison.Ordinal) || relative.IndexOf(':') >= 0)
            throw new ArgumentException("MSBuildProjectDirectory is required for absolute AdditionalFiles paths.");
        var dot = relative.LastIndexOf('.');
        if (dot > relative.LastIndexOf('/')) relative = relative.Substring(0, dot);
        var parts = relative.Split(new[] { '/', '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join("_", parts.Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)));
        name = string.Concat(name.Select(character => SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_'));
        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStartCharacter(name[0])) name = "_" + name;
        return GeneratorModel.Identifier(name);
    }

    private static bool Conflicts(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);
    private static DiagnosticDescriptor Rule(string id, string title) => new(id, title, "{0}", "LithoSharp", DiagnosticSeverity.Error, true);
    private static void Report(SourceProductionContext output, DiagnosticDescriptor rule, Location location, string message) => output.ReportDiagnostic(Diagnostic.Create(rule, location, message));

    private sealed class Collection
    {
        public Collection(INamedTypeSymbol symbol, INamedTypeSymbol front, ITypeSymbol page, string id, bool emitSchema, BinderEmitter emitter, ITypeSymbol? body)
        { Symbol = symbol; Front = front; Page = page; Id = id; EmitSchema = emitSchema; Emitter = emitter; Body = body; }
        public ITypeSymbol? Body { get; }
        public INamedTypeSymbol Symbol { get; }
        public INamedTypeSymbol Front { get; }
        public ITypeSymbol Page { get; }
        public string Id { get; }
        public bool EmitSchema { get; }
        public BinderEmitter Emitter { get; }
        public List<Entry> Entries { get; } = new();
    }
    private sealed class Entry
    {
        public Entry(string name, string id, string route, bool directory) { Name = name; Id = id; Route = route; Directory = directory; }
        public string Name { get; }
        public string Id { get; }
        public string Route { get; }
        public bool Directory { get; }
    }
    private sealed class Input
    {
        public Input(string path, SourceText? text, string collection, string id, string route, string projectDirectory)
        { Path = path; Text = text; Collection = collection; Id = id; Route = route; ProjectDirectory = projectDirectory; }
        public string Path { get; }
        public SourceText? Text { get; }
        public string Collection { get; }
        public string Id { get; }
        public string Route { get; }
        public string ProjectDirectory { get; }
        public Location Location => Location.Create(Path, default, new LinePositionSpan(default, default));
    }
}
