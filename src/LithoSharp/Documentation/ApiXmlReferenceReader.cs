using System.Xml;
using System.Xml.Linq;

namespace LithoSharp.Documentation;

internal sealed class ApiXmlReferenceReader
{
    internal sealed record Entry(ApiReferenceInput Input, string Id, XElement Element, ApiAssemblySource? Source)
    {
        internal DocumentKey Key => new(Input.Collection, Input.Variant.Version, Input.Variant.Locale, Id);
        internal ApiSymbol? Symbol => Source?.Symbols.GetValueOrDefault(Id);
        internal DocumentKey Target(string id) => Source?.References.GetValueOrDefault(id) ?? new(Input.Collection, Input.Variant.Version, Input.Variant.Locale, id);
    }

    private readonly Dictionary<DocumentKey, Entry> entries = [];
    private readonly Dictionary<DocumentKey, XElement> expanded = [];
    internal IEnumerable<Entry> Entries => entries.Values;

    internal ApiXmlReferenceReader(IEnumerable<(ApiReferenceInput Input, byte[] Bytes, ApiAssemblySource? Source)> inputs)
    {
        foreach (var (input, bytes, source) in inputs)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var members = (document.Root?.Name == "doc" ? document.Root.Element("members") : null) ?? throw new ArgumentException("XML documentation requires doc/members/member: " + input.FilePath);
            var elements = members.Elements("member").ToList();
            var documented = elements.Select(element => element.Attribute("name")?.Value ?? throw new ArgumentException("XML member requires a name: " + input.FilePath)).ToHashSet(StringComparer.Ordinal);
            if (source is not null)
                elements.AddRange(source.Symbols.Keys.Where(id => !documented.Contains(id)).Select(id => new XElement("member", new XAttribute("name", id))));
            foreach (var element in elements)
            {
                var id = element.Attribute("name")!.Value;
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("XML member requires a nonempty exact ID: " + input.FilePath);
                var entry = new Entry(input, id, element, source);
                if (entry.Symbol is { } symbol)
                    Validate(symbol, entry);
                if (!entries.TryAdd(entry.Key, entry))
                    throw new ArgumentException("Duplicate API identity: " + entry.Key + " in " + input.FilePath);
            }
        }
    }

    internal IEnumerable<KeyValuePair<DocumentKey, ApiMember>> Read(Func<DocumentKey, SiteUrl?> findUrl)
    {
        foreach (var entry in entries.Values)
        {
            var element = Expand(entry, []);
            string Render(XNode node) => node switch
            {
                XText text => Html.Encode(text.Value),
                XElement child when child.Name.LocalName is "see" or "seealso" => child.Attribute("cref") is { } reference
                    ? Link(child.Annotation<Entry>() ?? entry, reference.Value, child.Value.Length == 0 ? reference.Value : child.Value)
                    : Html.Encode(child.Attribute("langword")?.Value ?? child.Value),
                XElement child when child.Name.LocalName == "para" => "<p>" + string.Concat(child.Nodes().Select(Render)) + "</p>",
                XElement child when child.Name.LocalName == "code" => "<pre><code>" + Html.Encode(child.Value) + "</code></pre>",
                XElement child when child.Name.LocalName is "c" or "paramref" or "typeparamref" => "<code>" + Html.Encode(child.Attribute("name")?.Value ?? child.Value) + "</code>",
                XElement child => (child.Attribute("cref") is { } reference ? Link(child.Annotation<Entry>() ?? entry, reference.Value, reference.Value) + " " : "") + string.Concat(child.Nodes().Select(Render)),
                _ => ""
            };
            string Link(Entry origin, string id, string label)
            {
                var target = origin.Target(id);
                var url = findUrl(target) ?? throw Error(origin, "Unresolved API reference " + id + " (" + target + ")");
                return "<a href=\"" + url.ToAttributeValue() + "\">" + Html.Encode(label) + "</a>";
            }
            string Description(string section, string? name = null) => string.Concat(element.Elements(section)
                .Where(value => name is null || value.Attribute("name")?.Value == name).SelectMany(value => value.Nodes()).Select(Render));
            string Text(string label) => Html.Encode(Label(entry.Input.Variant.Locale, label));
            var symbol = entry.Symbol;
            var documentation = "<p>" + Description("summary") + "</p>";
            foreach (var section in element.Elements().Where(section => section.Name.LocalName != "summary"
                && !(section.Name.LocalName == "param" && symbol?.Parameters.Any(parameter => parameter.Name == section.Attribute("name")?.Value) == true)
                && !(section.Name.LocalName == "returns" && symbol?.ReturnType is not null)))
                documentation += "<section><h2>" + Text(section.Name.LocalName) + (section.Attribute("name") is { } name ? " " + Html.Encode(name.Value) : "") + "</h2>" + Render(section) + "</section>";
            var body = "";
            if (symbol is not null)
            {
                body += "<section><h2>" + Text("Signature") + "</h2><pre><code class=\"language-csharp\">" + Html.Encode(symbol.Signature) + "</code></pre></section>";
                body += "<p>" + Text("Visibility") + ": <code>" + Html.Encode(symbol.Visibility.ToString()) + "</code></p>";
                if (symbol.Parameters.Count > 0)
                    body += "<section><h2>" + Text("Parameters") + "</h2><dl>" + string.Concat(symbol.Parameters.Select(parameter => "<dt><code>" + Html.Encode(parameter.Type) + " " + Html.Encode(parameter.Name) + "</code></dt><dd>" + Description("param", parameter.Name) + "</dd>")) + "</dl></section>";
                if (symbol.ReturnType is not null)
                    body += "<section><h2>" + Text("Returns") + "</h2><p><code>" + Html.Encode(symbol.ReturnType) + "</code></p>" + Description("returns") + "</section>";
                if (symbol.GenericConstraints.Count > 0)
                    body += "<section><h2>" + Text("Generic constraints") + "</h2><pre><code>" + Html.Encode(string.Join("\n", symbol.GenericConstraints)) + "</code></pre></section>";
                if (symbol.Inheritance.Count > 0)
                    body += "<section><h2>" + Text("Inheritance") + "</h2><ul>" + string.Concat(symbol.Inheritance.Select(id => "<li>" + Link(entry, id, id) + "</li>")) + "</ul></section>";
                if (symbol.ObsoleteMessage is not null)
                    body += "<aside role=\"note\">" + Text("Obsolete") + ": " + Html.Encode(symbol.ObsoleteMessage) + "</aside>";
            }
            var canonicalDocumentation = new XElement("documentation", element.Elements().Where(section => section.Name.LocalName is not ("deprecated" or "obsolete")));
            yield return new(entry.Key, new(entry.Id, entry.Id.Length > 2 ? entry.Id[2..] : entry.Id, element.Element("summary")?.Value.Trim() ?? "", body + documentation,
                symbol?.ObsoleteMessage is not null || element.Element("deprecated") is not null || element.Element("obsolete") is not null)
            {
                Symbol = symbol,
                AssemblyName = entry.Source?.AssemblyName,
                DocumentationContent = canonicalDocumentation.ToString(SaveOptions.DisableFormatting),
                ParsedBodyHtml = body + documentation,
                ObsoleteContent = string.Concat(element.Elements().Where(section => section.Name.LocalName is "deprecated" or "obsolete").Select(section => section.ToString(SaveOptions.DisableFormatting)))
            });
        }
    }

    internal static string Label(string locale, string label)
    {
        if (!string.Equals(locale, "ja", StringComparison.OrdinalIgnoreCase)
            && !locale.StartsWith("ja-", StringComparison.OrdinalIgnoreCase))
            return label;
        return label switch
        {
            "Signature" => "シグネチャ",
            "Visibility" => "アクセス範囲",
            "Parameters" or "param" => "パラメーター",
            "Returns" or "returns" => "戻り値",
            "Generic constraints" => "型パラメーターの制約",
            "Inheritance" => "継承",
            "Obsolete" or "obsolete" or "Deprecated" or "deprecated" => "非推奨",
            "remarks" => "補足",
            "example" => "使用例",
            "exception" => "例外",
            "value" => "値",
            "typeparam" => "型パラメーター",
            "seealso" => "関連項目",
            "API changes" => "APIの変更",
            "API language" => "APIの言語",
            "added" => "追加",
            "removed" => "削除",
            "signature" => "シグネチャの変更",
            "visibility" => "アクセス範囲の変更",
            "documentation" => "説明の変更",
            "symbol-information" => "シンボル情報の変更",
            _ => label,
        };
    }

    private XElement Expand(Entry entry, List<DocumentKey> stack)
    {
        if (expanded.TryGetValue(entry.Key, out var cached))
            return cached;
        if (stack.Contains(entry.Key))
            throw Error(entry, "Cyclic inheritdoc: " + string.Join(" -> ", stack.Append(entry.Key)));
        stack.Add(entry.Key);
        try
        {
            XElement Inherited(XElement directive)
            {
                if (directive.Attribute("path") is not null)
                    throw Error(entry, "inheritdoc path selection is unsupported; supply explicit sections and cref");
                var id = directive.Attribute("cref")?.Value;
                if (id is null)
                {
                    var declared = entry.Element.Elements("inheritdoc").Select(value => value.Attribute("cref")?.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
                    if (declared.Length > 1)
                        throw Error(entry, "Ambiguous inheritdoc: supply an exact cref for the partial section");
                    id = declared.FirstOrDefault() ?? entry.Symbol?.InheritDocId;
                }
                if (string.IsNullOrWhiteSpace(id))
                    throw Error(entry, "Unresolved inheritdoc: supply an exact cref or symbol InheritDocId");
                var key = entry.Target(id);
                if (!entries.TryGetValue(key, out var target))
                    throw Error(entry, "Unresolved inheritdoc documentation: " + id + " (" + key + ")");
                var inherited = (XElement)Clone(Expand(target, stack));
                if (target.Symbol is { } parent && entry.Symbol is { } child && parent.Parameters.Count == child.Parameters.Count)
                {
                    var names = parent.Parameters.Zip(child.Parameters).ToDictionary(pair => pair.First.Name, pair => pair.Second.Name, StringComparer.Ordinal);
                    foreach (var parameter in inherited.Descendants().Where(value => value.Name.LocalName is "param" or "paramref"))
                        if (parameter.Attribute("name") is { } name && names.TryGetValue(name.Value, out var replacement))
                            name.Value = replacement;
                }
                return inherited;
            }
            XNode Copy(XNode node, XElement? section)
            {
                if (node is not XElement child)
                    return node is XText text ? new XText(text.Value) : new XText("");
                var copy = new XElement(child.Name, child.Attributes());
                copy.AddAnnotation(entry);
                foreach (var nested in child.Nodes())
                {
                    if (nested is XElement directive && directive.Name.LocalName == "inheritdoc")
                    {
                        var inherited = Inherited(directive);
                        var match = inherited.Elements().FirstOrDefault(value => SectionKey(value) == SectionKey(section ?? child))
                            ?? throw Error(entry, "Unresolved inheritdoc section: " + (section ?? child).Name.LocalName);
                        copy.Add(match.Nodes().Select(Clone));
                    }
                    else
                        copy.Add(Copy(nested, section ?? child));
                }
                return copy;
            }
            var result = new XElement(entry.Element.Name, entry.Element.Attributes());
            foreach (var section in entry.Element.Elements().Where(value => value.Name.LocalName != "inheritdoc"))
                result.Add(Copy(section, section));
            foreach (var directive in entry.Element.Elements("inheritdoc"))
            {
                var inherited = Inherited(directive);
                foreach (var section in inherited.Elements())
                    if (!result.Elements().Any(value => SectionKey(value) == SectionKey(section)))
                        result.Add(Clone(section));
            }
            expanded.Add(entry.Key, result);
            return result;
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private static string SectionKey(XElement section) => section.Name.LocalName + ":" + (section.Attribute("name")?.Value ?? section.Attribute("cref")?.Value ?? "");
    private static XNode Clone(XNode node)
    {
        if (node is not XElement element)
            return node is XText text ? new XText(text.Value) : new XText("");
        var clone = new XElement(element.Name, element.Attributes(), element.Nodes().Select(Clone));
        if (element.Annotation<Entry>() is { } origin)
            clone.AddAnnotation(origin);
        return clone;
    }
    private static void Validate(ApiSymbol symbol, Entry entry)
    {
        if (symbol.Parameters is null || symbol.Parameters.Any(parameter => parameter is null)
            || symbol.Parameters.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != symbol.Parameters.Count
            || symbol.GenericConstraints is null || symbol.GenericConstraints.Any(string.IsNullOrWhiteSpace)
            || symbol.Inheritance is null || symbol.Inheritance.Any(string.IsNullOrWhiteSpace))
            throw Error(entry, "Invalid explicit API symbol data");
    }
    private static ArgumentException Error(Entry entry, string message) => new(message + "; member " + entry.Id + "; source " + entry.Input.FilePath
        + (entry.Source is null ? "" : "; assembly " + entry.Source.AssemblyName));
}
