#if NETSTANDARD2_0 && !SYNTAMARK_SOURCE
namespace System.Runtime.CompilerServices;

// Reuse Syntamark.Source's marker when that immutable source pair is selected.
internal static class IsExternalInit { }
#endif
