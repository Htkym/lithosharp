namespace LithoSharp.Content;

/// <summary>Requires a nonempty input value when a field in the same mapping equals the decoded literal text.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = true)]
public sealed class ContentRequiredWhenAttribute : Attribute
{
    public ContentRequiredWhenAttribute(string conditionField, string expectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionField);
        ArgumentNullException.ThrowIfNull(expectedValue);
        ConditionField = conditionField.Normalize();
        ExpectedValue = expectedValue;
    }

    public string ConditionField { get; }
    public string ExpectedValue { get; }
}
