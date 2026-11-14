using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal sealed class ApiXmlLocalization
{
    private readonly Dictionary<string, (string English, string Japanese)> units = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Literal = new(["code", "c", "paramref", "typeparamref", "see", "seealso", "inheritdoc"], StringComparer.Ordinal);

    internal ApiXmlLocalization(string directory)
    {
        using var reviews = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "..", "translation-reviews.json")));
        if (reviews.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported API translation review schema.");
        var reviewed = reviews.RootElement.GetProperty("files").EnumerateArray().ToDictionary(file => file.GetProperty("path").GetString()!,
            file => file.GetProperty("sha256").GetString()!, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "units-*.json").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            if (!reviewed.TryGetValue("ja/" + Path.GetFileName(path), out var expected) || expected != Convert.ToHexStringLower(SHA256.HashData(bytes)))
                throw new InvalidDataException("API translation needs an explicit review: " + Path.GetFileName(path));
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported API translation unit schema.");
            foreach (var unit in document.RootElement.GetProperty("units").EnumerateObject())
            {
                var english = unit.Value.GetProperty("english").GetString()!;
                var japanese = unit.Value.GetProperty("japanese").GetString();
                if (unit.Name != Hash(english) || string.IsNullOrWhiteSpace(japanese) || !units.TryAdd(unit.Name, (english, japanese)))
                    throw new InvalidDataException("Invalid or duplicate API translation unit: " + unit.Name);
            }
        }
    }

    internal XElement Translate(XElement element, string memberId)
    {
        if (Literal.Contains(element.Name.LocalName))
            return new(element);
        var children = element.Elements().Select(child => Translate(child, memberId)).ToArray();
        var index = 0;
        var pattern = Regex.Replace(string.Concat(element.Nodes().Select(node => node is XElement ? "{" + index++ + "}" : node is XText text ? text.Value : "")), @"\s+", " ").Trim();
        if (!Regex.IsMatch(Regex.Replace(pattern, @"\{\d+\}", ""), "[A-Za-z]"))
            return Rebuild(element, pattern, children);
        var hash = Hash(pattern);
        if (!units.TryGetValue(hash, out var translated) || translated.English != pattern)
            throw new InvalidDataException($"Missing Japanese API documentation: {memberId}/{element.Name.LocalName}; unit {hash}; {pattern}");
        return Rebuild(element, translated.Japanese, children);
    }

    private static XElement Rebuild(XElement source, string text, XElement[] children)
    {
        var result = new XElement(source.Name, source.Attributes());
        var used = new HashSet<int>();
        var offset = 0;
        foreach (Match placeholder in Regex.Matches(text, @"\{(\d+)\}"))
        {
            result.Add(new XText(text[offset..placeholder.Index]));
            var index = int.Parse(placeholder.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (index >= children.Length || !used.Add(index))
                throw new InvalidDataException("Invalid API XML placeholder: " + source.Name.LocalName);
            result.Add(children[index]);
            offset = placeholder.Index + placeholder.Length;
        }
        result.Add(new XText(text[offset..]));
        if (used.Count != children.Length)
            throw new InvalidDataException("API XML translation omitted an inline element: " + source.Name.LocalName);
        return result;
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static object ExportUnits(IEnumerable<XElement> members)
    {
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var member in members)
            foreach (var section in member.Elements())
                Visit(section, member.Attribute("name")!.Value + "/" + section.Name.LocalName);
        return new { schemaVersion = 1, units = result };

        void Visit(XElement element, string context)
        {
            if (Literal.Contains(element.Name.LocalName))
                return;
            var index = 0;
            var pattern = Regex.Replace(string.Concat(element.Nodes().Select(node => node is XElement ? "{" + index++ + "}" : node is XText text ? text.Value : "")), @"\s+", " ").Trim();
            if (Regex.IsMatch(Regex.Replace(pattern, @"\{\d+\}", ""), "[A-Za-z]"))
                result.TryAdd(Hash(pattern), new { english = pattern, contexts = new[] { context } });
            foreach (var child in element.Elements())
                Visit(child, context + "/" + child.Name.LocalName);
        }
    }
}
