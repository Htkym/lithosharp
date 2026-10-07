using System.Collections;
using System.Globalization;

namespace LithoSharp.Content;

internal static class ContentScalarConversion
{
    internal static bool TryConvertScalar(object input, Type targetType, out object? converted)
    {
        var text = input as string ?? Convert.ToString(input, CultureInfo.InvariantCulture);
        if (text is null)
        {
            converted = null;
            return false;
        }

        if (targetType == typeof(string))
        {
            converted = text;
            return true;
        }

        if (targetType == typeof(Guid) && Guid.TryParseExact(text, "D", out var guid))
        {
            converted = guid;
            return true;
        }

        // Offset-less front matter dates (e.g. "2017-12-05") must not depend on the
        // build machine's local time zone: assume UTC so UTC and JST hosts emit
        // identical output. Explicit offsets are still preserved. (RoundtripKind
        // cannot be combined with AssumeUniversal; AssumeUniversal alone keeps
        // explicit offsets intact and only affects offset-less input.)
        if (targetType == typeof(DateTimeOffset)
            && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var dateTimeOffset))
        {
            converted = dateTimeOffset;
            return true;
        }

        if (targetType == typeof(DateTime)
            && DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dateTime))
        {
            converted = dateTime;
            return true;
        }

        if (targetType == typeof(DateOnly)
            && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            converted = date;
            return true;
        }

        if (targetType == typeof(TimeOnly)
            && TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            converted = time;
            return true;
        }

        if (targetType == typeof(TimeSpan)
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration))
        {
            converted = duration;
            return true;
        }

        if (targetType == typeof(Uri)
            && Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri))
        {
            converted = uri;
            return true;
        }

        try
        {
            converted = Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
            return converted is not null;
        }
        catch (FormatException)
        {
            converted = null;
            return false;
        }
        catch (InvalidCastException)
        {
            converted = null;
            return false;
        }
        catch (OverflowException)
        {
            converted = null;
            return false;
        }
    }

    internal static object? UnwrapTree(object? value)
    {
        value = LocatedYamlValue.Unwrap(value);
        return value switch
        {
            IReadOnlyDictionary<string, object?> mapping => mapping.ToDictionary(
                static pair => pair.Key,
                static pair => UnwrapTree(pair.Value),
                StringComparer.Ordinal),
            IEnumerable sequence when value is not string => sequence.Cast<object?>()
                .Select(UnwrapTree)
                .ToArray(),
            _ => value,
        };
    }

}
