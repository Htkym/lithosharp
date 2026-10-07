# Markdig shadow comparison (explicit runs only)

Reference-only harness kept after the C13 Markdig removal. It implements the
Markdig 1.4.0 pipeline (advanced extensions, disabled HTML) behind the
same internal compiler boundary and runs the shadow suites: full-corpus
agreement, the C00 baseline pins, extension agreement where the rules match,
and the deterministic mutational fuzz. The C00 expected results remain the
historical baseline pins; updating this comparison dependency does not regenerate
those fixtures or certify agreement until the explicit suite passes.

This project is intentionally outside `LithoSharp.slnx`, so normal solution
restore never pulls the Markdig package back in. Run it explicitly:

```powershell
dotnet restore tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj
dotnet test tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj -c Release
```

Comparison inputs live in `../LithoSharp.Tests/Fixtures` (copied at build)
and `../../fixtures/docusaurus` (read from the repository tree).
Fuzz violations are shrunk and saved under `.local/fuzz/c12`.
