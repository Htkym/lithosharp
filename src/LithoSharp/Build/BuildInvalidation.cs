using System.Collections.ObjectModel;

namespace LithoSharp.Build;

public sealed class BuildInvalidation
{
    internal BuildInvalidation(BuildNodeId nodeId, IEnumerable<string> reasons)
    {
        NodeId = nodeId;
        Reasons = new ReadOnlyCollection<string>(reasons.ToArray());
    }

    public BuildNodeId NodeId { get; }

    public IReadOnlyList<string> Reasons { get; }
}
