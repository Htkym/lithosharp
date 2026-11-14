using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using LithoSharp.Documentation;
using Microsoft.CodeAnalysis;

internal static class PublicApiDocumentation
{
    private sealed record Entry(string Collection, ISymbol Symbol, XElement? Documentation, bool Dependency);

    internal static ApiReferenceSite Create(string siteRoot, string inputDirectory, WebsiteApiLayout? layout, bool exportMaterialOnly = false)
    {
        Directory.CreateDirectory(inputDirectory);
        var snapshot = ApiMetadata.Read(AppContext.BaseDirectory);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (package, symbols) in snapshot.Assemblies)
        {
            using var reader = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "api-inputs", package + ".xml"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Element("assembly")?.Element("name")?.Value != package)
                throw new InvalidDataException("API XML assembly identity differs from the selected package: " + package);
            var comments = document.Descendants("member").ToDictionary(member => member.Attribute("name")!.Value, StringComparer.Ordinal);
            foreach (var symbol in symbols)
            {
                var id = symbol.GetDocumentationCommentId()!;
                if (!entries.TryAdd(id, new(package, symbol, comments.GetValueOrDefault(id), false)))
                    throw new InvalidDataException("Duplicate exported API ID: " + id);
            }
        }
        var referenced = entries.Values.SelectMany(entry => entry.Documentation?.Descendants().Attributes("cref").Select(attribute => attribute.Value) ?? [])
            .Concat(entries.Values.SelectMany(entry => entry.Symbol is INamedTypeSymbol type
                ? type.Interfaces.Cast<INamedTypeSymbol>().Concat(type.BaseType is { } parent ? [parent] : [])
                    .Select(value => value.OriginalDefinition.GetDocumentationCommentId()).Where(id => id is not null).Select(id => id!)
                : [])).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in referenced)
        {
            if (entries.ContainsKey(id))
                continue;
            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(id, snapshot.Compilation);
            if (symbol is not null && ApiMetadata.Exported(symbol))
                entries.TryAdd(id, new("dependency-api-" + symbol.ContainingAssembly.Name, symbol, null, true));
        }
        File.WriteAllText(Path.Combine(inputDirectory, "english-units.json"), JsonSerializer.Serialize(
            ApiXmlLocalization.ExportUnits(entries.Values.Where(entry => entry.Documentation is not null).Select(entry => entry.Documentation!)),
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(inputDirectory, "public-symbols.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, assemblies = snapshot.Assemblies.Select(pair => new
            {
                assembly = pair.Key, ids = pair.Value.Select(symbol => symbol.GetDocumentationCommentId()).Order(StringComparer.Ordinal).ToArray(),
                types = pair.Value.OfType<INamedTypeSymbol>().Select(type => type.GetDocumentationCommentId()).Order(StringComparer.Ordinal).ToArray()
            }),
            plannedOutputs = new[] { "en", "ja" }.SelectMany(locale => entries.Select(pair =>
                ApiReferenceSite.RouteFor("api/" + locale + "/" + pair.Value.Collection.ToLowerInvariant(), pair.Key).RelativeOutputPath)).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (exportMaterialOnly)
            return new ApiReferenceSite();
        var localization = new ApiXmlLocalization(Path.Combine(siteRoot, "api", "ja"));
        var extension = new ApiReferenceSite { Layout = layout ?? throw new ArgumentNullException(nameof(layout)), LayoutFingerprint = WebsiteApiLayout.Fingerprint };
        var included = entries.Keys.ToHashSet(StringComparer.Ordinal);
        var coverage = new List<object>();
        foreach (var locale in new[] { "en", "ja" })
        {
            foreach (var group in entries.Values.GroupBy(entry => (entry.Collection, Assembly: entry.Symbol.ContainingAssembly.Name)))
            {
                var references = entries.ToDictionary(pair => pair.Key, pair => new DocumentKey(pair.Value.Collection, "3.0", locale, pair.Key), StringComparer.Ordinal);
                var projected = group.Select(entry => ApiMetadata.Project(entry.Symbol, included)).ToArray();
                var members = group.Select(entry => Prepare(entry, locale, localization, entries, projected.First(symbol => symbol.Id == entry.Symbol.GetDocumentationCommentId()))).ToArray();
                var xml = new XDocument(new XElement("doc", new XElement("assembly", new XElement("name", group.Key.Assembly)), new XElement("members", members)));
                var file = Path.Combine(inputDirectory, group.Key.Collection + "." + group.Key.Assembly + "." + locale + ".xml");
                xml.Save(file);
                extension.AddXml(new(group.Key.Collection, new("3.0", locale, inputDirectory, "api/" + locale + "/" + group.Key.Collection.ToLowerInvariant()), file),
                    new ApiAssemblySource(group.Key.Assembly, projected, references));
                coverage.Add(new { collection = group.Key.Collection, assembly = group.Key.Assembly, locale,
                    ids = projected.Select(symbol => symbol.Id).Order(StringComparer.Ordinal).ToArray(),
                    pages = projected.Select(symbol => new { id = symbol.Id,
                        output = ApiReferenceSite.RouteFor("api/" + locale + "/" + group.Key.Collection.ToLowerInvariant(), symbol.Id).RelativeOutputPath }).ToArray(),
                    originalXmlEntries = group.Count(entry => entry.Documentation is not null), dependencyCards = group.Count(entry => entry.Dependency) });
            }
        }
        File.WriteAllText(Path.Combine(inputDirectory, "api-coverage.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, source = "selected-package-assembly-metadata-and-compiler-xml", variants = coverage }, new JsonSerializerOptions { WriteIndented = true }));
        return extension;
    }

    private static XElement Prepare(Entry entry, string locale, ApiXmlLocalization localization, IReadOnlyDictionary<string, Entry> entries, ApiSymbol symbol)
    {
        var id = entry.Symbol.GetDocumentationCommentId()!;
        var member = entry.Documentation is null ? new XElement("member", new XAttribute("name", id)) : new XElement(entry.Documentation);
        if (locale == "ja")
            member = new XElement(member.Name, member.Attributes(), member.Elements().Select(element => localization.Translate(element, id)));
        foreach (var reference in member.Descendants().Where(element => element.Name.LocalName is "see" or "seealso").ToArray())
            if (reference.Attribute("cref") is { } target && !entries.ContainsKey(target.Value))
                reference.ReplaceWith(new XElement("c", target.Value));
        foreach (var inherited in member.Descendants("inheritdoc").ToArray())
            if (!entries.TryGetValue(inherited.Attribute("cref")?.Value ?? symbol.InheritDocId ?? "", out var target) || target.Documentation is null)
                inherited.ReplaceWith(new XElement(inherited.Parent == member ? "remarks" : "para", locale == "ja"
                    ? "選択した入力ではこの継承元の説明を解決できません。宣言の構造は表示したメタデータで確認してください。"
                    : "The selected input cannot resolve this inherited explanation. Use the displayed metadata for the declaration's structure."));
        if (member.Element("summary") is null && !member.Elements("inheritdoc").Any())
        {
            var kind = Kind(entry.Symbol, locale);
            member.AddFirst(new XElement("summary", locale == "ja"
                ? new object[] { "これは ", new XElement("c", entry.Symbol.ContainingType?.ToDisplayString() ?? entry.Symbol.ContainingNamespace.ToDisplayString()), " で宣言された", kind, "です。シグネチャと可視性は、選択したパッケージのメタデータから取得します。" }
                : ["This ", kind, " is declared by ", new XElement("c", entry.Symbol.ContainingType?.ToDisplayString() ?? entry.Symbol.ContainingNamespace.ToDisplayString()), ". Its signature and visibility come from the selected package metadata."]));
        }
        if (entry.Documentation is null)
            member.Add(new XElement("remarks", locale == "ja"
                ? "選択した入力には、この宣言固有の XML コメントがありません。型と引数の構造は表示しますが、固有の動作条件をメタデータだけから推測しません。"
                : "The selected input has no declaration-specific XML comments. The type and parameter structure is shown without inferring member-specific behavior from metadata alone."));
        if (entry.Dependency)
            member.Add(new XElement("remarks", locale == "ja"
                ? "これは公開 API が参照する外部宣言の参照カードです。依存ライブラリ全体の API リファレンスではありません。"
                : "This is a reference card for an external declaration used by the public API, not a full API reference for that dependency."));
        return member;
    }

    private static string Kind(ISymbol symbol, string locale) => (symbol.Kind, symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) switch
    {
        (SymbolKind.NamedType, _) => locale == "ja" ? "型" : "type",
        (SymbolKind.Property, _) => locale == "ja" ? "プロパティ" : "property",
        (SymbolKind.Field, _) => locale == "ja" ? "フィールド" : "field",
        (SymbolKind.Event, _) => locale == "ja" ? "イベント" : "event",
        (_, true) => locale == "ja" ? "コンストラクター" : "constructor",
        _ => locale == "ja" ? "メソッド" : "method"
    };
}
