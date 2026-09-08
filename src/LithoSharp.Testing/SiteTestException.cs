namespace LithoSharp.Testing;

/// <summary>Represents a failed site or DOM assertion, independently of any test framework.</summary>
public sealed class SiteTestException : Exception
{
    /// <summary>Creates a site test failure.</summary>
    /// <param name="message">Failure description.</param>
    /// <exception cref="ArgumentException">The message is null, empty, or whitespace.</exception>
    public SiteTestException(string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }
}
