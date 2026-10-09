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
using LithoSharp.Internal;

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
        var provenance = context.CompilationProvider.Combine(context.AdditionalTextsProvider.Collect())
            .Combine(context.AnalyzerConfigOptionsProvider).Combine(declarations.Collect()).Select(static (input, cancellation) =>
                input.Right.Any(declaration => declaration.Attributes.Any(attribute =>
                    attribute.NamedArguments.Any(argument => argument.Key == "EmitStaticSiteManifest" && argument.Value.Value is true)))
                ? StaticManifestFingerprint.Create(input.Left.Left.Left, input.Left.Left.Right, input.Left.Right, cancellation) : null);
        context.RegisterSourceOutput(declarations.Collect().Combine(files.Collect()).Combine(provenance),
            static (output, inputs) => Generate(output, inputs.Left.Left, inputs.Left.Right, inputs.Right));
    }

    private static Input ReadInput(AdditionalText text, AnalyzerConfigOptionsProvider provider, System.Threading.CancellationToken cancellation)
    {
        var metadata = provider.GetOptions(text);
        string Get(string key) => metadata.TryGetValue("build_metadata.AdditionalFiles." + key, out var value) ? value : string.Empty;
        provider.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var projectDirectory);
        return new Input(text.Path, text.GetText(cancellation), Get("LithoSharpCollection"), Get("LithoSharpId"), Get("LithoSharpRoute"),
            Get("LithoSharpSite"), Get("LithoSharpVariant"), projectDirectory ?? string.Empty);
    }

    private static void Generate(SourceProductionContext output, ImmutableArray<GeneratorAttributeSyntaxContext> attributes, ImmutableArray<Input> files, string? fingerprint)
    {
        var complete = true;
        void Fail(DiagnosticDescriptor rule, Location location, string message)
        { complete = false; Report(output, rule, location, message); }
        var collections = new List<Collection>();
        foreach (var attribute in attributes)
        {
            var location = attribute.TargetNode.GetLocation();
            if (attribute.TargetSymbol is not INamedTypeSymbol symbol || !symbol.IsStatic || symbol.IsFileLocal || symbol.ContainingType is not null
                || symbol.Arity != 0 || !symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(output.CancellationToken) is ClassDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                Fail(InvalidDeclaration, location, "The declaration must be a top-level, nongeneric static partial class.");
                continue;
            }
            var arguments = attribute.Attributes[0].ConstructorArguments;
            if (arguments.Length != 3 || arguments[0].Value is not INamedTypeSymbol front || arguments[1].Value is not ITypeSymbol page || arguments[2].Value is not string rawId)
            {
                Fail(InvalidDeclaration, location, "The attribute requires front matter and page types and a collection ID.");
                continue;
            }
            if (!TryId(rawId, out var id) || GeneratorModel.IsScalar(front) || front.IsUnboundGenericType || front.IsRefLikeType
                || front.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                || GeneratorModel.IsList(front) || GeneratorModel.IsDictionary(front)
                || page.SpecialType == SpecialType.System_Void || page.TypeKind is TypeKind.Pointer or TypeKind.Error
                || page is INamedTypeSymbol { IsUnboundGenericType: true } or { IsRefLikeType: true }
                || page.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                Fail(InvalidDeclaration, location, "The collection ID or declared types are invalid.");
                continue;
            }
            var site = attribute.Attributes[0].NamedArguments.FirstOrDefault(pair => pair.Key == "Site").Value.Value as string ?? string.Empty;
            var variant = attribute.Attributes[0].NamedArguments.FirstOrDefault(pair => pair.Key == "Variant").Value.Value as string ?? string.Empty;
            if (attribute.Attributes[0].NamedArguments.Any(pair => (pair.Key is "Site" or "Variant") && pair.Value.Value is not string)
                || !TryScope(site, out site) || !TryScope(variant, out variant))
            {
                Fail(InvalidDeclaration, location, "Site and variant identities must be empty or valid IDs.");
                continue;
            }
            if (collections.Any(collection => collection.Id == id && collection.Site == site && collection.Variant == variant))
            {
                Fail(DuplicateId, location, "Duplicate collection ID '" + id + "'.");
                continue;
            }
            try
            {
                var emitSchema = attribute.Attributes[0].NamedArguments.Any(pair => pair.Key == "EmitJsonSchema" && pair.Value.Value is true);
                var emitManifest = attribute.Attributes[0].NamedArguments.Any(pair => pair.Key == "EmitStaticSiteManifest" && pair.Value.Value is true);
                // Check the original partial declaration before emitting any source, even for an empty collection.
                var generatedNames = new[] { "Binder", "SchemaJson", "Catalog", "Pages", "Entries", "Ids", "BinderImpl" }
                    .Concat(emitManifest ? new[] { "Manifest" } : Array.Empty<string>())
                    .Concat(emitSchema ? new[] { "WriteJsonSchema" } : Array.Empty<string>());
                var collision = generatedNames.FirstOrDefault(name => symbol.Name == name || symbol.GetMembers(name).Length != 0);
                if (collision is not null)
                {
                    var memberLocation = symbol.GetMembers(collision).SelectMany(member => member.Locations).FirstOrDefault(item => item.IsInSource);
                    Fail(InvalidDeclaration, memberLocation ?? location,
                        "Generated member '" + collision + "' conflicts with the collection type name or an existing member. Rename the declaration or member.");
                    continue;
                }
                var emitter = new BinderEmitter(front);
                var body = attribute.Attributes[0].NamedArguments.FirstOrDefault(pair => pair.Key == "BodyType").Value.Value as ITypeSymbol;
                if (body is not null && (body.SpecialType == SpecialType.System_Void || body.TypeKind is TypeKind.Pointer or TypeKind.Error || body is INamedTypeSymbol { IsUnboundGenericType: true } or { IsRefLikeType: true }))
                    throw new InvalidOperationException("The content body must be a closed, non-ref type.");
                collections.Add(new Collection(symbol, front, page, id, site, variant, emitSchema, emitManifest, emitter, body));
            }
            catch (InvalidOperationException failure) { Fail(InvalidDeclaration, location, failure.Message); }
        }

        var routes = new List<(string Site, string Variant, SiteRoute Route)>();
        foreach (var input in files)
        {
            if (input.Collection.Length == 0 && input.Id.Length == 0 && input.Route.Length == 0 && input.Site.Length == 0 && input.Variant.Length == 0) continue;
            var collection = TryId(input.Collection, out var collectionId) && TryScope(input.Site, out var site) && TryScope(input.Variant, out var variant)
                ? collections.FirstOrDefault(item => item.Id == collectionId && item.Site == site && item.Variant == variant) : null;
            if (collection is null || input.Text is null || !TryId(input.Id, out var entryId) || input.Route.Length == 0)
            {
                Fail(MissingInput, input.Location, "The input requires an existing collection in its site/variant scope, readable content, a valid entry ID, and a route.");
                continue;
            }
            string name;
            string sourcePath;
            try { sourcePath = RelativeInputPath(input); name = InputName(sourcePath); }
            catch (ArgumentException failure) { Fail(MissingInput, input.Location, failure.Message); continue; }
            if (name is "Pages" or "Entries" or "Ids" or "Catalog" || collection.Entries.Any(entry => entry.Id == entryId || entry.Name == name))
            {
                Fail(DuplicateId, input.Location, "Duplicate entry ID or generated member name '" + name + "'.");
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
                Fail(InvalidRoute, input.Location, failure.Message);
                continue;
            }
            if (routes.Any(existing => existing.Site == collection.Site && existing.Variant == collection.Variant
                && Conflicts(existing.Route.RelativeOutputPath, route.RelativeOutputPath)))
            {
                Fail(InvalidRoute, input.Location, "The route conflicts with another static entry's output: " + route.RelativeOutputPath);
                continue;
            }
            routes.Add((collection.Site, collection.Variant, route));
            StaticYamlValidation.Validate(input.Path, input.Text.ToString(), collection.Front, diagnostic =>
            { if (diagnostic.Severity == DiagnosticSeverity.Error) complete = false; output.ReportDiagnostic(diagnostic); });
            collection.Entries.Add(new Entry(name, entryId, sourcePath, input.Route, route.PublicPath.EndsWith("/", StringComparison.Ordinal)));
        }
        foreach (var collection in collections)
        {
            var source = Render(collection, fingerprint, complete);
            using var hash = SHA256.Create();
            var suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(collection.Symbol.ToDisplayString()))).Replace("-", string.Empty);
            output.AddSource(collection.Symbol.Name + "." + suffix + ".g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string Render(Collection collection, string? fingerprint, bool complete)
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
        text.AppendLine("/// <summary>Declared inputs shared by references and explicit runtime route registration.</summary>");
        text.AppendLine("public static global::LithoSharp.Content.StaticContentCatalog Catalog { get; } = new(");
        text.AppendLine("new global::LithoSharp.Content.ContentCollectionId(" + GeneratorModel.Literal(collection.Id) + "),");
        text.AppendLine("new global::LithoSharp.Content.StaticContentCatalogEntry[] {");
        foreach (var entry in collection.Entries.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var route = "global::LithoSharp.Routing.SiteRoute." + (entry.Directory ? "ForDirectoryIndex" : "ForFile") + "(" + GeneratorModel.Literal(entry.Route) + ")";
            text.AppendLine("new(new global::LithoSharp.Content.ContentEntryId(" + GeneratorModel.Literal(entry.Id) + "), "
                + GeneratorModel.Literal(entry.SourcePath) + ", " + route + "),");
        }
        text.AppendLine("}, " + GeneratorModel.Literal(collection.Site) + ", " + GeneratorModel.Literal(collection.Variant) + ");");
        if (collection.EmitManifest)
        {
            var closed = complete && fingerprint is not null;
            var paths = collection.Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry =>
                entry.Directory ? SiteRoute.ForDirectoryIndex(entry.Route).PublicPath : SiteRoute.ForFile(entry.Route).PublicPath);
            text.AppendLine("/// <summary>Explicit immutable route lookup; anchors/assets and site publication are not certified.</summary>");
            text.AppendLine("[global::LithoSharp.Content.StaticSiteManifestSource(" + GeneratorModel.Literal(StaticManifestFingerprint.Contract) + ", "
                + GeneratorModel.Literal(fingerprint ?? "") + ", " + GeneratorModel.Literal(closed ? "Closed" : "Open") + ", "
                + GeneratorModel.Literal(collection.Site) + ", " + GeneratorModel.Literal(collection.Variant) + ", new string[] {"
                + string.Join(", ", paths.Select(GeneratorModel.Literal)) + "})]");
            text.AppendLine("public static global::LithoSharp.Content.StaticSiteManifest Manifest { get; } = "
                + (closed ? "global::LithoSharp.Content.StaticSiteManifest.Create(Catalog, " + GeneratorModel.Literal(fingerprint!) + ")"
                    : "global::LithoSharp.Content.StaticSiteManifest.Open(Catalog)") + ";");
        }
        foreach (var group in new[] { "Pages", "Entries", "Ids" })
        {
            text.AppendLine("/// <summary>References derived from declared static content paths.</summary>");
            text.AppendLine("public static class " + group + " {");
            foreach (var entry in collection.Entries.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var id = "new global::LithoSharp.Content.ContentEntryId(" + GeneratorModel.Literal(entry.Id) + ")";
                var catalogEntry = "Catalog.GetEntry(" + id + ")";
                var route = catalogEntry + ".Route";
                var pageId = $"page:collection:{collection.Id.Length}:{collection.Id}:{entry.Id.Length}:{entry.Id}";
                var type = group == "Pages" ? $"global::LithoSharp.Pages.PageRef<{page}>"
                    : group == "Entries" ? $"global::LithoSharp.Content.ContentRef<global::LithoSharp.Content.ContentEntry<{front}, {(collection.Body is null ? "string" : GeneratorModel.Display(collection.Body))}>>"
                    : "global::LithoSharp.Content.ContentEntryId";
                var value = group == "Pages" ? "new(new global::LithoSharp.Pages.PageId(" + GeneratorModel.Literal(pageId) + "), " + route + ")"
                    : group == "Entries" ? "new(Catalog.CollectionId, " + catalogEntry + ".Id, " + route + ")" : catalogEntry + ".Id";
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

    private static bool TryScope(string value, out string id)
    {
        id = string.Empty;
        return value.Length == 0 || TryId(value, out id);
    }

    private static string RelativeInputPath(Input input)
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
        return SiteRoute.NormalizeRelativeOutputPath(relative);
    }

    private static string InputName(string relative)
    {
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
        public Collection(INamedTypeSymbol symbol, INamedTypeSymbol front, ITypeSymbol page, string id, string site, string variant, bool emitSchema, bool emitManifest, BinderEmitter emitter, ITypeSymbol? body)
        { Symbol = symbol; Front = front; Page = page; Id = id; Site = site; Variant = variant; EmitSchema = emitSchema; EmitManifest = emitManifest; Emitter = emitter; Body = body; }
        public string Site { get; }
        public string Variant { get; }
        public ITypeSymbol? Body { get; }
        public INamedTypeSymbol Symbol { get; }
        public INamedTypeSymbol Front { get; }
        public ITypeSymbol Page { get; }
        public string Id { get; }
        public bool EmitSchema { get; }
        public bool EmitManifest { get; }
        public BinderEmitter Emitter { get; }
        public List<Entry> Entries { get; } = new();
    }
    private sealed class Entry
    {
        public Entry(string name, string id, string sourcePath, string route, bool directory) { Name = name; Id = id; SourcePath = sourcePath; Route = route; Directory = directory; }
        public string SourcePath { get; }
        public string Name { get; }
        public string Id { get; }
        public string Route { get; }
        public bool Directory { get; }
    }
    private sealed class Input
    {
        public Input(string path, SourceText? text, string collection, string id, string route, string site, string variant, string projectDirectory)
        { Path = path; Text = text; Collection = collection; Id = id; Route = route; Site = site; Variant = variant; ProjectDirectory = projectDirectory; }
        public string Site { get; }
        public string Variant { get; }
        public string Path { get; }
        public SourceText? Text { get; }
        public string Collection { get; }
        public string Id { get; }
        public string Route { get; }
        public string ProjectDirectory { get; }
        public Location Location => Location.Create(Path, default, new LinePositionSpan(default, default));
    }
}
