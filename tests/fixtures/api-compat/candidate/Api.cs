namespace Fixture.ApiCompat;

/// <summary>
/// Candidate surface: <c>Removed</c> is gone on purpose, so package validation
/// against the 1.0.0 baseline must report a removed-member incompatibility.
/// </summary>
public sealed class Surface
{
    public string Kept() => "kept";
}
