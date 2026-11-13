namespace LithoSharp.Content;

/// <summary>Overrides the snake_case name of a front matter property or field.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ContentNameAttribute : Attribute
{
    /// <summary>Sets the name used by content binders and schemas.</summary>
    /// <param name="name">The nonempty content field name.</param>
    public ContentNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The content field name.</summary>
    public string Name { get; }
}
