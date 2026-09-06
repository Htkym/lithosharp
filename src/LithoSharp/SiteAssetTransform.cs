using System.Collections.ObjectModel;

namespace LithoSharp;

/// <summary>Declares one transformed asset output.</summary>
public sealed class SiteAssetOutput
{
    /// <summary>Creates a named output with a contained relative path before fingerprinting.</summary>
    /// <exception cref="ArgumentException">The identifier or relative path is invalid.</exception>
    public SiteAssetOutput(string id, string relativeOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = new Build.BuildNodeId(id).Value;
        RelativeOutputPath = Routing.SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
    }
    /// <summary>Stable output identifier.</summary>
    public string Id { get; }
    /// <summary>Relative output path.</summary>
    public string RelativeOutputPath { get; }
}

/// <summary>Declares a deterministic asset transformation.</summary>
public sealed class SiteAssetTransform
{
    /// <summary>Declares a deterministic handler and all files it reads and writes.</summary>
    /// <param name="id">Stable transform identifier.</param>
    /// <param name="implementationFingerprint">Implementation, configuration and tool version fingerprint.</param>
    /// <param name="inputs">Registered source asset declarations.</param>
    /// <param name="outputs">Outputs that the handler must write exactly once.</param>
    /// <param name="handler">Handler limited to declared snapshot inputs and outputs.</param>
    /// <param name="validateInputs">Optional preflight run before every cache lookup, for example to reject a replaced external tool.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Identifiers are invalid, outputs are empty, or declarations repeat or contain null.</exception>
    public SiteAssetTransform(string id, string implementationFingerprint, IReadOnlyList<SiteAsset> inputs, IReadOnlyList<SiteAssetOutput> outputs, Func<SiteAssetTransformContext, CancellationToken, Task> handler, Func<CancellationToken, Task>? validateInputs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(implementationFingerprint);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(handler);
        if (inputs.Any(static input => input is null) || outputs.Any(static output => output is null))
            throw new ArgumentException("Transform declarations must not contain null entries.");
        if (outputs.Count == 0 || inputs.Select(input => input.Id).Distinct(StringComparer.Ordinal).Count() != inputs.Count
            || outputs.Select(output => output.Id).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
            throw new ArgumentException("Declare distinct inputs and at least one distinct output.");
        Id = new Build.BuildNodeId(id).Value;
        ImplementationFingerprint = implementationFingerprint;
        Inputs = new ReadOnlyCollection<SiteAsset>(inputs.ToArray());
        Outputs = new ReadOnlyCollection<SiteAssetOutput>(outputs.ToArray());
        Handler = handler;
        ValidateInputs = validateInputs;
    }
    /// <summary>Stable transform identifier.</summary>
    public string Id { get; }
    /// <summary>Fingerprint of implementation, settings and tool dependencies.</summary>
    public string ImplementationFingerprint { get; }
    /// <summary>Declared source assets.</summary>
    public IReadOnlyList<SiteAsset> Inputs { get; }
    /// <summary>Declared output files.</summary>
    public IReadOnlyList<SiteAssetOutput> Outputs { get; }
    internal Func<SiteAssetTransformContext, CancellationToken, Task> Handler { get; }
    internal Func<CancellationToken, Task>? ValidateInputs { get; }
}

/// <summary>Provides isolated input and output access to a transformation.</summary>
public sealed class SiteAssetTransformContext
{
    private readonly IReadOnlyDictionary<SiteAsset, byte[]> inputs;
    private readonly IReadOnlyDictionary<SiteAsset, AssetUrl> urls;
    private readonly IReadOnlyDictionary<string, SiteAssetOutput> outputs;
    private readonly Dictionary<string, byte[]> written = new(StringComparer.Ordinal);
    internal SiteAssetTransformContext(IReadOnlyDictionary<SiteAsset, byte[]> inputs,
        IReadOnlyList<SiteAssetOutput> outputs, IReadOnlyDictionary<SiteAsset, AssetUrl> urls)
    {
        this.inputs = inputs;
        this.urls = urls;
        this.outputs = outputs.ToDictionary(output => output.Id, StringComparer.Ordinal);
    }
    /// <summary>Opens a read-only snapshot of a declared input.</summary>
    /// <exception cref="ArgumentException">The input is not declared by this transformation.</exception>
    public Stream OpenRead(SiteAsset input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return inputs.TryGetValue(input, out var bytes) ? new MemoryStream(bytes, writable: false)
            : throw new ArgumentException("The input is not declared by this transformation.", nameof(input));
    }
    /// <summary>Gets the URL for a declared input.</summary>
    /// <exception cref="ArgumentException">The input is not declared by this transformation.</exception>
    public AssetUrl GetUrl(SiteAsset input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return urls.TryGetValue(input, out var url) ? url
            : throw new ArgumentException("The input is not declared by this transformation.", nameof(input));
    }
    /// <summary>Writes one output exactly once.</summary>
    /// <exception cref="ArgumentException">The output is not declared by this transformation.</exception>
    /// <exception cref="InvalidOperationException">The output was already written.</exception>
    public Task WriteAsync(SiteAssetOutput output, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        if (!outputs.TryGetValue(output.Id, out var declared) || !ReferenceEquals(declared, output))
            throw new ArgumentException("The output is not declared by this transformation.", nameof(output));
        if (!written.TryAdd(output.Id, bytes.ToArray()))
            throw new InvalidOperationException("A transform output can only be written once.");
        return Task.CompletedTask;
    }
    internal IReadOnlyDictionary<string, byte[]> Written => written;
}
