namespace LithoSharp.Content.Compilation;

/// <summary>ASCII-only predicates shared by all parser targets.</summary>
internal static class LithoCharacters
{
    public static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    public static bool IsAsciiLetterOrDigit(char value) => IsAsciiLetter(value) || value is >= '0' and <= '9';
}
