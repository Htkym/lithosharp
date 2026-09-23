namespace Fixture.ApiCompat;

/// <summary>
/// Baseline surface of the API compatibility fixture. The candidate removes
/// <see cref="Removed"/> so that pack-time package validation must fail.
/// </summary>
public sealed class Surface
{
    public string Kept() => "kept";

    public string Removed() => "removed";
}
