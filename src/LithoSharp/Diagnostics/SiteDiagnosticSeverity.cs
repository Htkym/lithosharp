namespace LithoSharp.Diagnostics;

#if NETSTANDARD2_0
internal enum SiteDiagnosticSeverity
#else
public enum SiteDiagnosticSeverity
#endif
{
    Info,
    Warning,
    Error,
}
