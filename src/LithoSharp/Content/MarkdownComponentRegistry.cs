using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;

namespace LithoSharp.Content;

/// <summary>Explicit, immutable registrations for attribute-only Markdown components.</summary>
public sealed class MarkdownComponentRegistry
{
    private readonly IReadOnlyDictionary<string, MarkdownComponentRegistration> byName;
    internal static MarkdownComponentRegistry Empty { get; } = new([]);

    /// <summary>Copies registrations and rejects duplicate component names.</summary>
    public MarkdownComponentRegistry(IEnumerable<MarkdownComponentRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var entries = registrations.ToArray();
        if (entries.Any(static entry => entry is null))
            throw new ArgumentException("Component registrations must not contain null.", nameof(registrations));
        if (entries.GroupBy(static entry => entry.Name, StringComparer.Ordinal).Any(static group => group.Count() != 1))
            throw new ArgumentException("Component names must be unique.", nameof(registrations));
        Registrations = Array.AsReadOnly(entries.OrderBy(static entry => entry.Name, StringComparer.Ordinal).ToArray());
        byName = Registrations.ToDictionary(static entry => entry.Name, StringComparer.Ordinal);
        Schemas = Array.AsReadOnly(Registrations.Select(static entry => new MarkdownComponentSchema(entry.Name,
            entry.PropsType.FullName ?? entry.PropsType.Name, entry.SchemaJson, entry.Fingerprint, entry.SemanticRules?.ToJson())).ToArray());
        Dependencies = ContentDependency.Snapshot(Registrations.SelectMany(static entry => entry.Dependencies));
        Fingerprint = Hash(JsonSerializer.Serialize(Registrations.Select(static entry => new { entry.Name, entry.Fingerprint })));
    }

    /// <summary>Registrations in ordinal name order. Inspection reads metadata without invoking user code.</summary>
    public IReadOnlyList<MarkdownComponentRegistration> Registrations { get; }

    /// <summary>Serializable static descriptors for explicitly acquired editor contexts.</summary>
    public IReadOnlyList<MarkdownComponentSchema> Schemas { get; }

    /// <summary>Declared inputs. File paths are relative to their registration's dependency root.</summary>
    public IReadOnlyList<ContentDependency> Dependencies { get; }

    /// <summary>Identity of names, implementations, schemas, configuration, and declared inputs.</summary>
    public string Fingerprint { get; }

    internal bool TryGet(string name, out MarkdownComponentRegistration registration) => byName.TryGetValue(name, out registration!);
    internal bool HasReferenceRules => Registrations.Any(static registration => registration.SemanticRules?.Rules
        .Any(static rule => rule.Kind == DocumentSemanticRuleKind.Reference) == true);
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>A component and typed binder selected explicitly by trusted site configuration.</summary>
public abstract class MarkdownComponentRegistration
{
    private protected MarkdownComponentRegistration(string name, Type propsType, string? schemaJson,
        string fingerprint, IReadOnlyList<ContentDependency> dependencies, string? dependencyRoot, DocumentSemanticRuleSet? semanticRules)
    {
        Name = name;
        PropsType = propsType;
        SchemaJson = schemaJson;
        Fingerprint = fingerprint;
        Dependencies = dependencies;
        DependencyRoot = dependencyRoot;
        SemanticRules = semanticRules;
    }

    /// <summary>The ordinal name selected by <c>:::component name="..."</c>.</summary>
    public string Name { get; }
    /// <summary>The declared props type. Editors use the schema instead of inspecting or instantiating this type.</summary>
    public Type PropsType { get; }
    /// <summary>Static schema supplied by a generated binder, or null when static validation is unavailable.</summary>
    public string? SchemaJson { get; }
    /// <summary>Versioned identity, including the explicit configuration fingerprint.</summary>
    public string Fingerprint { get; }
    /// <summary>Inputs consumed by binding or rendering.</summary>
    public IReadOnlyList<ContentDependency> Dependencies { get; }
    /// <summary>Explicit root for relative file dependencies, or null when no files are consumed.</summary>
    public string? DependencyRoot { get; }
    /// <summary>Declared constraints on literal props, evaluated against the current captured document catalog.</summary>
    public DocumentSemanticRuleSet? SemanticRules { get; }

    /// <summary>Registers a component and its typed binder without evaluating Markdown expressions.</summary>
    /// <param name="name">An ASCII letter followed by letters, digits, underscores, or hyphens.</param>
    /// <param name="component">The trusted static HTML component.</param>
    /// <param name="binder">The props binder. A schema provider also enables static editor validation.</param>
    /// <param name="configurationFingerprint">A stable identity for all captured configuration. Change it when that configuration changes.</param>
    /// <param name="dependencies">Additional values, files, or registered assets used by binding or rendering.</param>
    /// <param name="dependencyRoot">The explicit root for file dependencies. Required when files are declared.</param>
    /// <param name="semanticRules">Declarative prop reference rules. Rendering requires the current document identity and captured catalog.</param>
    public static MarkdownComponentRegistration Create<TProps>(string name, ISiteComponent<TProps> component,
        IContentFrontMatterBinder<TProps> binder, string configurationFingerprint,
        IEnumerable<ContentDependency>? dependencies = null, string? dependencyRoot = null,
        DocumentSemanticRuleSet? semanticRules = null) where TProps : notnull
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        var inputs = ContentDependency.Snapshot(dependencies);
        if (inputs.Any(static input => input.Kind == ContentDependencyKind.File) && string.IsNullOrWhiteSpace(dependencyRoot))
            throw new ArgumentException("File dependencies require an explicit dependency root.", nameof(dependencyRoot));
        if (dependencyRoot is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(dependencyRoot);
        var root = dependencyRoot is null ? null : Path.GetFullPath(dependencyRoot);
        var schema = (binder as IContentFrontMatterSchemaProvider)?.SchemaJson;
        var fingerprint = MarkdownComponentRegistry.Hash(JsonSerializer.Serialize(new
        {
            Name = name,
            Props = Identity(typeof(TProps)),
            Component = Identity(component.GetType()),
            Binder = Identity(binder.GetType()),
            Schema = schema,
            Configuration = configurationFingerprint,
            SemanticRules = semanticRules?.ToJson(),
            DependencyRoot = root,
            Inputs = inputs.Select(static input => new { input.Kind, input.Key, input.Value, input.PhysicalFilePath }),
        }));
        return new TypedRegistration<TProps>(name, component, binder, schema, fingerprint, inputs, root, semanticRules);
    }

    private static bool Letter(char character) => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Letter(name[0]) || name.Any(static character => !Letter(character) && !(character is >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("A component name must contain ASCII letters, digits, underscores, or hyphens and start with a letter.", nameof(name));
    }
    private static string Identity(Type type) => $"{type.AssemblyQualifiedName}/{type.Module.ModuleVersionId:N}";

    internal abstract ContentParseResult<string> Render(IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? location, ComponentRenderingContext context);

    private sealed class TypedRegistration<TProps>(string name, ISiteComponent<TProps> component,
        IContentFrontMatterBinder<TProps> binder, string? schema, string fingerprint,
        IReadOnlyList<ContentDependency> dependencies, string? dependencyRoot, DocumentSemanticRuleSet? semanticRules)
        : MarkdownComponentRegistration(name, typeof(TProps), schema, fingerprint, dependencies, dependencyRoot, semanticRules) where TProps : notnull
    {
        private readonly object gate = new();

        internal override ContentParseResult<string> Render(IReadOnlyDictionary<string, object?> values,
            SiteSourceLocation? location, ComponentRenderingContext context)
        {
            lock (gate)
            {
                var bound = binder.Bind(values, location);
                if (!bound.IsSuccess)
                    return ContentParseResult<string>.Failure(bound.Diagnostics);
                if (bound.Value is null)
                    throw new InvalidOperationException($"Component binder '{Name}' returned no props.");
                return ContentParseResult<string>.Success(context.Render(component, bound.Value).ToHtmlString(), bound.Diagnostics);
            }
        }
    }
}

/// <summary>Static component metadata. It contains no component instance, binder, or factory.</summary>
public sealed class MarkdownComponentSchema
{
    /// <summary>Creates metadata for one explicitly acquired component registration.</summary>
    public MarkdownComponentSchema(string name, string propsTypeName, string? schemaJson, string fingerprint,
        string? semanticRulesJson = null)
    {
        MarkdownComponentRegistration.ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(propsTypeName);
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length != 64 || fingerprint.Any(static character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A component fingerprint must be a lowercase SHA-256 value.", nameof(fingerprint));
        Name = name;
        PropsTypeName = propsTypeName;
        SchemaJson = schemaJson;
        Fingerprint = fingerprint;
        if (semanticRulesJson is not null)
            _ = DocumentSemanticRuleSet.ParseJson(semanticRulesJson);
        SemanticRulesJson = semanticRulesJson;
    }

    public string Name { get; }
    public string PropsTypeName { get; }
    public string? SchemaJson { get; }
    public string Fingerprint { get; }
    /// <summary>Serializable declarative prop constraints without component or binder instances.</summary>
    public string? SemanticRulesJson { get; }
}
