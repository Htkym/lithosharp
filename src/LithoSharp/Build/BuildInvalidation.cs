using System.Collections.ObjectModel;

namespace LithoSharp.Build;

/// <summary>前回の検証済み計画と比較して無効化されたビルドノードと理由を表します。</summary>
public sealed class BuildInvalidation
{
    internal BuildInvalidation(BuildNodeId nodeId, IEnumerable<string> reasons)
    {
        NodeId = nodeId;
        Reasons = new ReadOnlyCollection<string>(reasons.ToArray());
    }

    /// <summary>無効化されたノードの識別子を取得します。</summary>
    public BuildNodeId NodeId { get; }

    /// <summary>序数順に並べられた人間が読める無効化理由を取得します。</summary>
    public IReadOnlyList<string> Reasons { get; }
}
