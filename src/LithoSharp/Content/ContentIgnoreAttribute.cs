namespace LithoSharp.Content;

/// <summary>Excludes a property or field from content binding and schemas.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ContentIgnoreAttribute : Attribute
{
}
