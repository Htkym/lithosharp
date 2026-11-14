# LithoSharp.CodeFixes

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.ja.md)

Optional IDE fixes complement LithoSharp's compiler diagnostics for .NET documentation teams using C# schemas and generated references. A fix is offered only for the documented proven patterns; compilation and final-output validation remain separate.

Install alongside the matching LithoSharp.Analyzers and Generators packages in a Roslyn 4.14 IDE host. Workspaces dependencies belong to this IDE assembly; the compiler analyzer remains independent of Workspaces and Core runtime loading.

Use version 3.0.0 for all three packages and keep them private to the consuming
project. CodeFixes is optional; installing it does not turn editor Markdown
inspection into C# schema checking or enable a fix for every Analyzer diagnostic.

```xml
<PackageReference Include="LithoSharp.CodeFixes" Version="3.0.0" PrivateAssets="all" />
```

LSA1401 replaces only a direct literal of a verified fresh Closed manifest with the unique generated PageRef's `Route.PublicPath`. The original `Manifest.GetUrl` receiver, overload and runtime guard remain. Generated Pages initializers must all be canonical pure references to the verified immutable catalog. Open, stale, ambiguous, handwritten, side-effecting and unproved references receive no fix.

The action revalidates current compiler inputs and refuses stale workspace snapshots. Standard CodeAction preview and undo apply. Document, project and solution FixAll prove every target using its original project compilation and apply the independent literal replacements atomically. An unproved target or any intervening solution edit prevents the whole action. Project and solution actions also refuse linked files and diagnostics without an editable original source document. Existing fresh-manifest proof limits, including unsupported compilation references, still apply. Missing routes, raw HTML, headings and arbitrary URL expressions are never repaired by guessing.

See the [Analyzer guide](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.md),
[typed-content generator guide](https://github.com/Htkym/lithosharp/blob/main/docs/source-generators.md)
and [upgrade notes](https://github.com/Htkym/lithosharp/blob/main/docs/migration-3.0.md).
