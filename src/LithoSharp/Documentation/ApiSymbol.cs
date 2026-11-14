using System.Collections.ObjectModel;

namespace LithoSharp.Documentation;

/// <summary>The declared accessibility of an explicitly supplied API symbol.</summary>
public enum ApiVisibility
{
    /// <summary>Accessible by all consumers.</summary>
    Public,
    /// <summary>Accessible by derived types.</summary>
    Protected,
    /// <summary>Accessible in the assembly or by derived types.</summary>
    ProtectedInternal,
    /// <summary>Accessible in the declaring assembly.</summary>
    Internal,
    /// <summary>Accessible by derived types in the declaring assembly.</summary>
    PrivateProtected,
    /// <summary>Accessible in the declaring type.</summary>
    Private
}

/// <summary>A parameter from an explicit symbol source; its type is never inferred from prose.</summary>
public sealed class ApiParameter
{
    /// <summary>Creates a named parameter with its C# type, including any ref modifier.</summary>
    public ApiParameter(string name, string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Name = name;
        Type = type;
    }
    /// <summary>The source parameter name.</summary>
    public string Name { get; }
    /// <summary>The explicitly supplied C# type.</summary>
    public string Type { get; }
}

/// <summary>Symbol facts supplied by a trusted source or metadata reader, keyed by the exact XML ID.</summary>
public sealed class ApiSymbol
{
    /// <summary>Creates a symbol without executing or guessing the API's source code.</summary>
    public ApiSymbol(string id, string signature, ApiVisibility visibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (!Enum.IsDefined(visibility))
            throw new ArgumentOutOfRangeException(nameof(visibility));
        Id = id;
        Signature = signature;
        Visibility = visibility;
    }
    /// <summary>The exact documentation ID, including overload and generic arity.</summary>
    public string Id { get; }
    /// <summary>The complete C# declaration supplied by the symbol source.</summary>
    public string Signature { get; }
    /// <summary>The declared accessibility.</summary>
    public ApiVisibility Visibility { get; }
    /// <summary>The ordered parameter symbols.</summary>
    public IReadOnlyList<ApiParameter> Parameters { get; init; } = [];
    /// <summary>The return type, or null when not applicable or unavailable.</summary>
    public string? ReturnType { get; init; }
    /// <summary>The explicit C# generic constraint clauses.</summary>
    public IReadOnlyList<string> GenericConstraints { get; init; } = [];
    /// <summary>Exact base type and interface IDs, resolved through the source reference map.</summary>
    public IReadOnlyList<string> Inheritance { get; init; } = [];
    /// <summary>The exact inherited documentation target when inheritdoc omits cref.</summary>
    public string? InheritDocId { get; init; }
    /// <summary>The obsolete attribute message, including an empty message when present.</summary>
    public string? ObsoleteMessage { get; init; }
    /// <summary>An optional original source path for catalog integration.</summary>
    public string? SourcePath { get; init; }
}

/// <summary>An explicitly named assembly's symbol and cross-assembly reference maps.</summary>
public sealed class ApiAssemblySource
{
    /// <summary>Snapshots symbol identities and references; duplicate exact IDs are rejected.</summary>
    public ApiAssemblySource(string assemblyName, IEnumerable<ApiSymbol> symbols, IReadOnlyDictionary<string, DocumentKey> references)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(references);
        AssemblyName = assemblyName;
        Symbols = new ReadOnlyDictionary<string, ApiSymbol>(symbols.ToDictionary(symbol => (symbol ?? throw new ArgumentException("A symbol source cannot contain null symbols.", nameof(symbols))).Id, StringComparer.Ordinal));
        References = new ReadOnlyDictionary<string, DocumentKey>(references.ToDictionary(pair =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            return pair.Key;
        }, pair => pair.Value ?? throw new ArgumentException("A reference map cannot contain null targets.", nameof(references)), StringComparer.Ordinal));
    }
    /// <summary>The explicit assembly identity; no assembly is loaded or executed.</summary>
    public string AssemblyName { get; }
    /// <summary>Symbol data indexed by exact XML ID.</summary>
    public IReadOnlyDictionary<string, ApiSymbol> Symbols { get; }
    /// <summary>Explicit exact-ID targets outside this input's collection/version/language.</summary>
    public IReadOnlyDictionary<string, DocumentKey> References { get; }
}
