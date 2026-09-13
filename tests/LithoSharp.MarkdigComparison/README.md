# Markdig shadow comparison (explicit runs only)

Reference-only harness kept after the C13 Markdig removal. It implements the
pinned Markdig 1.3.2 pipeline (advanced extensions, disabled HTML) behind the
same internal compiler boundary and runs the shadow suites: full-corpus
agreement, the C00 baseline pins, extension agreement where the rules match,
and the deterministic mutational fuzz.

This project is intentionally outside `LithoSharp.slnx`, so normal solution
restore never pulls the Markdig package back in. Run it explicitly:

```powershell
dotnet restore tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj
dotnet test tests/LithoSharp.MarkdigComparison/LithoSharp.MarkdigComparison.csproj -c Release
```

Comparison inputs live in `../LithoSharp.Tests/Fixtures` (copied at build)
and `../../fixtures/docusaurus` (read from the repository tree).
Fuzz violations are shrunk and saved under `.local/fuzz/c12`.
