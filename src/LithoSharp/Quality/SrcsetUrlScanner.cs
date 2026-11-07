using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Quality;

internal static class SrcsetUrlScanner
{
    internal sealed record Candidate(string Value, HtmlSpan Source);
    // Extract accepted srcset candidate URLs; image selection and sizes are separate concerns.
    internal static IEnumerable<string> Read(string value, CancellationToken cancellationToken)
        => ReadCandidates(value, cancellationToken).Select(candidate => candidate.Value);

    internal static IEnumerable<Candidate> ReadCandidates(string value, CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (IsWhitespace(value[index]) || value[index] == ','))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            }
            if (index == value.Length)
                yield break;
            var urlStart = index;
            while (index < value.Length && !IsWhitespace(value[index]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            }
            var urlEnd = index;
            if (value[urlEnd - 1] == ',')
            {
                while (value[urlEnd - 1] == ',')
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    urlEnd--;
                }
                yield return new(value.Substring(urlStart, urlEnd - urlStart), new(urlStart, urlEnd - urlStart));
                continue;
            }

            var valid = true;
            var width = false;
            var density = false;
            var height = false;
            while (index < value.Length)
            {
                while (index < value.Length && IsWhitespace(value[index]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    index++;
                }
                if (index == value.Length)
                    break;
                if (value[index] == ',')
                {
                    index++;
                    break;
                }
                var descriptorStart = index;
                var inParens = false;
                while (index < value.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var character = value[index];
                    if (inParens)
                    {
                        if (character == ')')
                            inParens = false;
                    }
                    else
                    {
                        if (IsWhitespace(character) || character == ',')
                            break;
                        if (character == '(')
                            inParens = true;
                    }
                    index++;
                }
                valid &= AcceptDescriptor(value.Substring(descriptorStart, index - descriptorStart),
                    ref width, ref density, ref height, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (valid && (!height || width))
                yield return new(value.Substring(urlStart, urlEnd - urlStart), new(urlStart, urlEnd - urlStart));
        }
    }

    private static bool AcceptDescriptor(string descriptor, ref bool width, ref bool density,
        ref bool height, CancellationToken cancellationToken)
    {
        var number = descriptor.Substring(0, descriptor.Length - 1);
        switch (descriptor[descriptor.Length - 1])
        {
            case 'w' when !width && !density && IsPositiveInteger(number, cancellationToken):
                width = true;
                return true;
            case 'h' when !height && !density && IsPositiveInteger(number, cancellationToken):
                height = true;
                return true;
            case 'x' when !width && !height && !density && IsDensity(number, cancellationToken):
                density = true;
                return true;
            default:
                return false;
        }
    }

    private static bool IsPositiveInteger(string value, CancellationToken cancellationToken)
    {
        var positive = false;
        foreach (var character in value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character is < '0' or > '9')
                return false;
            positive |= character != '0';
        }
        return positive;
    }

    private static bool IsDensity(string value, CancellationToken cancellationToken)
    {
        var index = 0;
        if (index < value.Length && value[index] == '-')
            index++;
        var integral = ReadDigits(value, ref index, cancellationToken);
        if (index < value.Length && value[index] == '.')
        {
            index++;
            if (!ReadDigits(value, ref index, cancellationToken))
                return false;
        }
        else if (!integral)
            return false;
        if (index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-')
                index++;
            if (!ReadDigits(value, ref index, cancellationToken))
                return false;
        }
        if (index != value.Length)
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        var accepted = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var density)
            && !double.IsNaN(density) && !double.IsInfinity(density) && density >= 0;
        cancellationToken.ThrowIfCancellationRequested();
        return accepted;
    }

    private static bool ReadDigits(string value, ref int index, CancellationToken cancellationToken)
    {
        var start = index;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
        }
        return index != start;
    }

    private static bool IsWhitespace(char character) => character is '\t' or '\n' or '\f' or '\r' or ' ';
}
