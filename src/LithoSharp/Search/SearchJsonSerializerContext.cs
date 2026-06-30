using System.Text.Json.Serialization;

namespace LithoSharp.Search;

/// <summary>
/// The System.Text.Json source-generator context used to serialize the search index.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SearchIndex))]
internal sealed partial class SearchJsonSerializerContext : JsonSerializerContext;
