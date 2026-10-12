# LithoSharp.CodeFixes

Install alongside the matching LithoSharp.Analyzers and Generators packages in a Roslyn 4.14 IDE host. Workspaces dependencies belong to this IDE assembly; the compiler analyzer remains independent of Workspaces and Core runtime loading.

LSA1401 replaces only a direct literal of a verified fresh Closed manifest with the unique generated PageRef's `Route.PublicPath`. The original `Manifest.GetUrl` receiver, overload and runtime guard remain. Generated Pages initializers must all be canonical pure references to the verified immutable catalog. Open, stale, ambiguous, handwritten, side-effecting and unproved references receive no fix.

The action revalidates current compiler inputs and refuses stale workspace snapshots. Standard CodeAction preview and undo apply. Document FixAll proves every target using the same original compilation and applies the independent literal replacements atomically. An unproved target or any intervening solution edit prevents the whole action. Project and solution FixAll are unavailable. Missing routes, raw HTML, headings and arbitrary URL expressions are never repaired by guessing.
