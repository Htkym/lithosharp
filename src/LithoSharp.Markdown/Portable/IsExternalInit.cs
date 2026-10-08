#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

// The compiler marker needed for records/init on the portable target.
internal static class IsExternalInit { }
#endif
