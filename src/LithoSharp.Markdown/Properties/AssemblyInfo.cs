using System.Runtime.CompilerServices;

// Runtime adapters can use the legacy AST without publishing a second public AST.
// Compiler/source-host payloads do not include this runtime-only assembly metadata.
#if NET10_0
[assembly: InternalsVisibleTo("LithoSharp")]
[assembly: InternalsVisibleTo("LithoSharp.Tests")]
[assembly: InternalsVisibleTo("LithoSharp.MarkdigComparison")]
[assembly: InternalsVisibleTo("LithoSharp.Tool")]
[assembly: InternalsVisibleTo("LithoSharp.Testing")]
[assembly: InternalsVisibleTo("LithoSharp.Mdx")]
[assembly: InternalsVisibleTo("LithoSharp.Performance")]
#endif
