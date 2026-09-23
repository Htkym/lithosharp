# Generator host compatibility fixtures

These projects exist only for the negative compatibility tests in
`eng/Test-CompatibilityGate.ps1`. They are intentionally outside `LithoSharp.slnx`
and must never be referenced by product code, the test project or a package.

- `TooNewGenerator/` references `Microsoft.CodeAnalysis.CSharp` 5.9.0, which is
  newer than the compiler of the documented minimum SDK (10.0.300 provides
  compiler 5.6.0.0). Building it and running
  `eng/Test-GeneratorHostCompatibility.ps1 -GeneratorPath <built dll>` must fail
  with a CS9057-equivalent explanation.
