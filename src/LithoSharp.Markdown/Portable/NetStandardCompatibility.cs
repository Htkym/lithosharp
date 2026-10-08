#if NETSTANDARD2_0
using System.Collections.Generic;

namespace LithoSharp.Content.Compilation;

// Only the missing netstandard2.0 BCL operations; modern hosts use their native methods.
internal static class NetStandardCompatibility
{
    public static bool Contains(this string text, char value) => text.IndexOf(value) >= 0;
    public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;
    public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;

    public static bool TryPop<T>(this Stack<T> stack, out T value)
    {
        if (stack.Count == 0)
        {
            value = default!;
            return false;
        }

        value = stack.Pop();
        return true;
    }
}
#endif
