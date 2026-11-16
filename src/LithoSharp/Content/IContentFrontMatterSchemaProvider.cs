namespace LithoSharp.Content;

/// <summary>Supplies generated static schema metadata during an explicitly requested build or inspection.</summary>
public interface IContentFrontMatterSchemaProvider
{
    /// <summary>The generated JSON Schema, including the versioned LithoSharp static metadata.</summary>
    string SchemaJson { get; }
}
