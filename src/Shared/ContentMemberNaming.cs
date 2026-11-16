using System.Text;

namespace LithoSharp.Content;

internal static class ContentMemberNaming
{
    internal static string ToSnakeCase(string name)
    {
        var text = new StringBuilder(name.Length);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0
                && (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1])
                    || char.IsUpper(name[index - 1]) && index + 1 < name.Length && char.IsLower(name[index + 1])))
                text.Append('_');
            text.Append(char.ToLowerInvariant(character));
        }
        return text.ToString();
    }
}
