using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
var before = File.ReadAllText(args[0]);
var after = File.ReadAllText(args[1]);
var reports = new List<object>();
foreach (var symbols in new[] { Array.Empty<string>(), new[] { "COMPILER_DIAGNOSTIC_ACCEPTANCE" } })
{
 var options = new CSharpParseOptions(LanguageVersion.Preview).WithPreprocessorSymbols(symbols);
 var a = CSharpSyntaxTree.ParseText(before, options).GetRoot();
 var b = CSharpSyntaxTree.ParseText(after, options).GetRoot();
 var at = a.DescendantTokens().ToArray(); var bt = b.DescendantTokens().ToArray();
 var differences = new List<object>();
 for (int i = 0; i < Math.Min(at.Length, bt.Length); i++)
  if (at[i].RawKind != bt[i].RawKind || at[i].Text != bt[i].Text)
   differences.Add(new { index=i, kindBefore=at[i].Kind().ToString(), kindAfter=bt[i].Kind().ToString(), textBefore=at[i].Text, textAfter=bt[i].Text, valueBefore=at[i].ValueText, valueAfter=bt[i].ValueText, valueEqual=at[i].ValueText==bt[i].ValueText });
 var atr = a.DescendantTrivia().Where(t=>!t.IsKind(SyntaxKind.WhitespaceTrivia)&&!t.IsKind(SyntaxKind.EndOfLineTrivia)).Select(t=>new {kind=t.Kind().ToString(),text=t.ToFullString()}).ToArray();
 var btr = b.DescendantTrivia().Where(t=>!t.IsKind(SyntaxKind.WhitespaceTrivia)&&!t.IsKind(SyntaxKind.EndOfLineTrivia)).Select(t=>new {kind=t.Kind().ToString(),text=t.ToFullString()}).ToArray();
 reports.Add(new {symbols,tokensBefore=at.Length,tokensAfter=bt.Length,allTokenValuesEqual=at.Select(t=>(t.RawKind,t.ValueText)).SequenceEqual(bt.Select(t=>(t.RawKind,t.ValueText))),differences,triviaBefore=atr,triviaAfter=btr,significantTriviaEqual=atr.SequenceEqual(btr)});
}
File.WriteAllText(args[2],JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
