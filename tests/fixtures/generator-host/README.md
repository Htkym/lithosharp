# Generator host compatibility fixtures

These projects exist only for the negative compatibility tests in
`eng/Test-CompatibilityGate.ps1`. They are intentionally outside `LithoSharp.slnx`
and must never be referenced by product code, the test project or a package.

`FutureCodeAnalysis/` builds a fixture-only assembly named `Microsoft.CodeAnalysis`
with assembly version `65534.0.0.0`. It contains only the contract needed by
`TooNewGenerator/`, which records a genuine PE assembly reference to that identity.
Neither project has a NuGet package dependency. The deliberately future version
keeps the negative case invalid when the installed SDK's Roslyn version advances.

The harness copies fixture sources and lock files to its private `.tmp` directory,
excluding `bin` and `obj`, and builds there with locked restore. Running
`eng/Test-GeneratorHostCompatibility.ps1 -GeneratorPath <built dll>` must fail
specifically because that reference is newer than the active compiler. Loading the
same DLL as a compiler analyzer produces CS9057. The harness also checks the real
product generator, which must continue to pass against the actual SDK compiler.
