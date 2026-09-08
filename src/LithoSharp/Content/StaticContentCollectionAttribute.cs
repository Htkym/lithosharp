namespace LithoSharp.Content;

/// <summary>Declares a static Markdown collection for LithoSharp.Generators.</summary>
/// <remarks>Apply to a top-level static partial class. AdditionalFiles identify the collection, entry ID, and route.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StaticContentCollectionAttribute : Attribute
{
    /// <summary>Declares the front matter type, page type, and stable collection ID.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The collection ID is invalid.</exception>
    public StaticContentCollectionAttribute(Type frontMatterType, Type pageType, string collectionId)
    {
        ArgumentNullException.ThrowIfNull(frontMatterType);
        ArgumentNullException.ThrowIfNull(pageType);
        FrontMatterType = frontMatterType;
        PageType = pageType;
        CollectionId = new ContentCollectionId(collectionId).Value;
    }

    /// <summary>The type bound from front matter.</summary>
    public Type FrontMatterType { get; }

    /// <summary>The type used for generated page references.</summary>
    public Type PageType { get; }

    /// <summary>The stable collection ID.</summary>
    public string CollectionId { get; }

    /// <summary>Whether to generate the optional JSON Schema export.</summary>
    public bool EmitJsonSchema { get; set; }
    /// <summary>The content body type used by generated entry references. Defaults to Markdown strings.</summary>
    public Type BodyType { get; set; } = typeof(string);
}
