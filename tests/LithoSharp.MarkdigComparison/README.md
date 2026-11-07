# Markdig shadow comparison (explicit runs only)

Reference-only harness kept after the C13 Markdig removal. It implements the
Markdig 1.4.0 pipeline (advanced extensions, disabled HTML) behind the
same internal compiler boundary and runs the shadow suites: supported-corpus
agreement, the C00 baseline pins, extension agreement where the rules match,
and the deterministic mutational fuzz. The C00 expected results remain the
historical baseline pins; updating this comparison dependency does not regenerate
those fixtures or certify agreement until the explicit suite passes.

This project is intentionally outside `LithoSharp.slnx`, so normal solution
restore never pulls the Markdig package back in. Run it explicitly:

```powershell
dotnet restore tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj --locked-mode
dotnet test --project tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj -c Release --no-restore
```

Comparison inputs live in [`../LithoSharp.Tests/Fixtures`](../LithoSharp.Tests/Fixtures)
and [`../fixtures/docusaurus`](../fixtures/docusaurus). The tests locate and read
them from the repository tree; keep the checkout available when running the host.
Fuzz violations are shrunk and saved under `.local/fuzz/c12`.

The product under test now uses Syntamark 1.2.0 with `lithosharp-markdown/1`.
Passing this explicit supported-subset comparison does not select CommonMark,
prove universal Markdown equivalence, or measure site-generation performance.
