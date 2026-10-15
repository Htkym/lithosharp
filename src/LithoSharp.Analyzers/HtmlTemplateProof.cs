using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

internal enum HtmlHoleKind { Unknown, Encoded, Fragment, RawObject }
internal sealed record HtmlTemplatePiece(string? Literal, Location? Origin = null,
    HtmlHoleKind Kind = HtmlHoleKind.Unknown, string? KnownText = null, bool Deferred = false);

// A shape proof is independent of scalar contents and of the URL origin proof.
// Missing alternatives and exhausted budgets fail the whole proof, never just a suffix.
internal sealed class HtmlTemplateProof
{
    internal const int MaxPieces = 2 * HtmlTemplateAnalysis.MaxHoles + 1;
    private HtmlTemplateProof(ImmutableArray<ImmutableArray<HtmlTemplatePiece>> alternatives) => Alternatives = alternatives;
    internal ImmutableArray<ImmutableArray<HtmlTemplatePiece>> Alternatives { get; }

    private static HtmlTemplateProof? Create(IEnumerable<IEnumerable<HtmlTemplatePiece>> alternatives)
    {
        var result = ImmutableArray.CreateBuilder<ImmutableArray<HtmlTemplatePiece>>();
        foreach (var alternative in alternatives)
        {
            if (result.Count == StaticValueFlow.MaxCandidates) return null;
            var pieces = ImmutableArray.CreateBuilder<HtmlTemplatePiece>();
            var length = 0; var holes = 0;
            foreach (var piece in alternative)
            {
                length += piece.Literal?.Length ?? piece.KnownText?.Length ?? 0;
                if (length > HtmlTemplateAnalysis.MaxTemplateLength) return null;
                if (piece.Literal is null && ++holes > HtmlTemplateAnalysis.MaxHoles) return null;
                if (piece.Literal is not null && pieces.Count != 0 && pieces[pieces.Count - 1].Literal is { } previous)
                    pieces[pieces.Count - 1] = new(previous + piece.Literal);
                else pieces.Add(piece);
                if (pieces.Count > MaxPieces) return null;
            }
            result.Add(pieces.ToImmutable());
        }
        return result.Count == 0 ? null : new(result.ToImmutable());
    }

    internal static HtmlTemplateProof? Literal(string text) => Create(new[] { new[] { new HtmlTemplatePiece(text) } });
    internal static HtmlTemplateProof? Hole(HtmlTemplatePiece hole) => Create(new[] { new[] { hole } });
    internal static HtmlTemplateProof? Join(HtmlTemplateProof? left, HtmlTemplateProof? right) => left is null || right is null
        ? null : Create(left.Alternatives.Concat(right.Alternatives).Select(a => (IEnumerable<HtmlTemplatePiece>)a));
    internal static HtmlTemplateProof? Concat(HtmlTemplateProof? left, HtmlTemplateProof? right) => left is null || right is null
        ? null : Create(left.Alternatives.SelectMany(a => right.Alternatives.Select(b => a.Concat(b))));

    internal static HtmlTemplateProof? Evaluate(IOperation operation, Func<IOperation, StaticValue> snapshot,
        Func<ILocalSymbol, StaticValue> local, Func<CaptureId, StaticValue> capture)
    {
        if (StaticValueFlow.HasBindingError(operation)) return null;
        switch (operation)
        {
            case ILocalReferenceOperation reference: return local(reference.Local).Template;
            case IFlowCaptureReferenceOperation reference: return capture(reference.Id).Template;
            case IFlowCaptureOperation captured: return snapshot(captured.Value).Template;
            case ISimpleAssignmentOperation assignment when !assignment.IsRef: return snapshot(assignment.Value).Template;
            case IConversionOperation conversion when conversion.OperatorMethod is null
                && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference): return snapshot(conversion.Operand).Template;
            case IBinaryOperation binary when binary.OperatorKind == BinaryOperatorKind.Add && binary.OperatorMethod is null
                && binary.Type?.SpecialType == SpecialType.System_String
                && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String
                && binary.RightOperand.Type?.SpecialType == SpecialType.System_String:
                return Concat(snapshot(binary.LeftOperand).Template, snapshot(binary.RightOperand).Template);
            case IConditionalOperation conditional:
                var condition = snapshot(conditional.Condition);
                if (condition.Values is { Length: 1 } && condition.Values[0] is bool known)
                    return snapshot(known ? conditional.WhenTrue : conditional.WhenFalse!).Template;
                return Join(snapshot(conditional.WhenTrue).Template, conditional.WhenFalse is null ? null : snapshot(conditional.WhenFalse).Template);
            case IInterpolatedStringOperation interpolated:
                if (interpolated.Syntax.Span.Length > HtmlTemplateAnalysis.MaxTemplateLength) return null;
                var result = Literal("");
                foreach (var part in interpolated.Parts)
                {
                    HtmlTemplateProof? next;
                    if (part is IInterpolatedStringTextOperation literal && literal.Text.ConstantValue.Value is string contents)
                        next = Literal(contents);
                    else if (part is IInterpolationOperation hole)
                    {
                        var value = snapshot(hole.Expression);
                        var deferred = hole.Alignment is not null || hole.FormatString is not null;
                        var origin = hole.Syntax is InterpolationSyntax syntax ? syntax.Expression.GetLocation() : hole.Expression.Syntax.GetLocation();
                        if (!deferred && hole.Expression.Type?.SpecialType == SpecialType.System_String
                            && value.Values is { Length: > 0 } values && values.All(v => v is string))
                            next = Create(values.Cast<string>().Select(text => new[] { new HtmlTemplatePiece(null, origin, value.OutputKind, text) }));
                        else next = Hole(new(null, origin, value.OutputKind, Deferred: deferred));
                    }
                    else return null;
                    result = Concat(result, next);
                    if (result is null) return null;
                }
                return result;
            default:
                return operation.ConstantValue.HasValue && operation.ConstantValue.Value is string text ? Literal(text) : null;
        }
    }
}
