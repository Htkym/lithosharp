using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// A value witness, never a runtime TimeSpan/getter/factory invocation in the compiler host.
internal sealed record StaticDuration(long Ticks)
{
    internal static StaticValue? Evaluate(IOperation operation, INamedTypeSymbol? timeSpan, Func<IOperation, StaticValue> read)
    {
        bool Exact(ITypeSymbol? type) => timeSpan is not null && SymbolEqualityComparer.Default.Equals(type, timeSpan);
        if (operation is IConversionOperation converted && converted.IsImplicit && converted.OperatorMethod is null
            && converted.Conversion.Exists && converted.Type is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && Exact(nullable.TypeArguments[0])
            && Exact(converted.Operand.Type)) return read(converted.Operand);
        if (!Exact(operation.Type)) return null;
        if (operation is IFieldReferenceOperation field && field.Field.IsStatic && Exact(field.Field.ContainingType)
            && !field.Field.Locations.Any(location => location.IsInSource))
            return field.Field.Name switch {
                "Zero" => StaticValue.Known(new StaticDuration(0)),
                "MinValue" => StaticValue.Known(new StaticDuration(long.MinValue)),
                "MaxValue" => StaticValue.Known(new StaticDuration(long.MaxValue)),
                _ => null
            };
        var method = operation is IInvocationOperation call ? call.TargetMethod : (operation as IObjectCreationOperation)?.Constructor;
        if (method is null || !Exact(method.ContainingType) || method.Arity != 0 || method.DeclaredAccessibility != Accessibility.Public
            || method.Parameters.Length == 0 || method.Parameters.Length > 6 || method.Parameters.Any(p => p.RefKind != RefKind.None)) return null;
        long factor;
        if (operation is IObjectCreationOperation)
        {
            if (method.Parameters.Length != 1 || method.Parameters[0].Type.SpecialType != SpecialType.System_Int64) return null;
            factor = 1;
        }
        else
        {
            if (!method.IsStatic) return null;
            factor = method.Name switch { "FromTicks" => 1, "FromMilliseconds" => 10000, "FromSeconds" => 10000000,
                "FromMinutes" => 600000000, "FromHours" => 36000000000, "FromDays" => 864000000000, _ => 0 };
            if (factor == 0) return null;
        }
        var arguments = operation is IInvocationOperation invocation ? invocation.Arguments : ((IObjectCreationOperation)operation).Arguments;
        var total = StaticValue.Known(new StaticDuration(0));
        foreach (var argument in arguments.OrderBy(a => a.Parameter!.Ordinal))
        {
            var parameter = argument.Parameter!;
            var unit = method.Parameters.Length == 1 ? factor : parameter.Name switch {
                "days" => 864000000000L, "hours" => 36000000000L, "minutes" => 600000000L,
                "seconds" => 10000000L, "milliseconds" => 10000L, "microseconds" => 10L, _ => 0L };
            if (unit == 0 || parameter.Type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double)) return null;
            var input = argument.Value;
            var values = read(input);
            // Only compiler-defined implicit numeric widening; never unwrap a user conversion.
            if (values.Values is null && input is IConversionOperation numeric && numeric.IsImplicit && numeric.OperatorMethod is null
                && numeric.Conversion.IsNumeric) values = read(numeric.Operand);
            if (values.Values is null) return values;
            StaticValue? next = null;
            foreach (var value in values.Values)
                foreach (var previous in total.Values!)
                {
                    if (!Integer(value, out var number)) return StaticValue.Unknown("unsupported-duration-number");
                    try
                    {
                        var ticks = checked(number * unit);
                        if (parameter.Type.SpecialType == SpecialType.System_Double && (!Binary64Exact(number) || !Binary64Exact(ticks)))
                            return StaticValue.Unknown("duration-rounding");
                        var result = StaticValue.Known(new StaticDuration(checked(((StaticDuration)previous!).Ticks + ticks)));
                        next = next is null ? result : next.Join(result);
                        if (next.Values is null) return next;
                    }
                    catch (OverflowException) { return StaticValue.Unknown("duration-overflow"); }
                }
            total = next ?? StaticValue.Unknown("empty-duration-set");
        }
        return total;
    }

    private static bool Integer(object? value, out long result)
    {
        result = 0;
        switch (value)
        {
            case byte n: result = n; return true;
            case sbyte n: result = n; return true;
            case short n: result = n; return true;
            case ushort n: result = n; return true;
            case int n: result = n; return true;
            case uint n: result = n; return true;
            case long n: result = n; return true;
            case ulong n when n <= long.MaxValue: result = (long)n; return true;
            case double n when !double.IsNaN(n) && !double.IsInfinity(n) && Math.Truncate(n) == n:
                try { result = checked((long)n); return true; } catch (OverflowException) { return false; }
            case float n: return Integer((double)n, out result);
            default: return false;
        }
    }

    private static bool Binary64Exact(long value)
    {
        var magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        while (magnitude > (1UL << 53)) { if ((magnitude & 1) != 0) return false; magnitude >>= 1; }
        return true;
    }
}
