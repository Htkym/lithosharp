using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Build;

/// <summary>検証済みのノード、入力、依存関係、成果物を持つ不変のサイトビルド計画を表します。</summary>
public sealed class SiteBuildPlan
{
    private readonly IReadOnlyDictionary<BuildArtifactId, BuildNode> _ownersByArtifactId;

    private SiteBuildPlan(IReadOnlyList<BuildNode> nodes)
    {
        Nodes = Array.AsReadOnly(nodes.ToArray());
        Artifacts = Array.AsReadOnly(nodes
            .SelectMany(node => node.Artifacts)
            .OrderBy(artifact => artifact.Id.Value, StringComparer.Ordinal)
            .ToArray());
        _ownersByArtifactId = new ReadOnlyDictionary<BuildArtifactId, BuildNode>(
            nodes.SelectMany(node => node.Artifacts.Select(artifact => (artifact.Id, Node: node)))
                .ToDictionary(pair => pair.Id, pair => pair.Node));
    }

    /// <summary>識別子の序数順に並べられたビルドノードを取得します。</summary>
    public IReadOnlyList<BuildNode> Nodes { get; }

    /// <summary>識別子の序数順に並べられたすべての成果物を取得します。</summary>
    public IReadOnlyList<BuildArtifact> Artifacts { get; }

    /// <summary>宣言を検証し、成功した場合は不変のビルド計画を作成します。</summary>
    /// <param name="nodes">検証するビルドノード宣言。</param>
    /// <returns>検証済みのビルド計画。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> に <see langword="null"/> が含まれます。</exception>
    /// <exception cref="SiteBuildPlanValidationException">1 件以上のエラー診断が見つかりました。</exception>
    public static SiteBuildPlan Create(IEnumerable<BuildNode> nodes)
    {
        var result = Validate(nodes);
        if (!result.IsValid)
        {
            throw new SiteBuildPlanValidationException(result.Diagnostics);
        }

        return result.Plan!;
    }

    /// <summary>宣言を検証し、すべての検出可能な問題を診断として返します。</summary>
    /// <param name="nodes">検証するビルドノード宣言。</param>
    /// <returns>診断と、成功時の不変なビルド計画。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="nodes"/> に <see langword="null"/> が含まれます。</exception>
    public static SiteBuildPlanValidationResult Validate(IEnumerable<BuildNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var declarations = nodes.ToArray();
        if (declarations.Any(node => node is null))
        {
            throw new ArgumentException("Build nodes must not contain null.", nameof(nodes));
        }

        var orderedNodes = declarations
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .ThenBy(DescribeNode, StringComparer.Ordinal)
            .ToArray();
        var diagnostics = new List<SiteDiagnostic>();
        var normalizedPaths = new Dictionary<BuildArtifact, string>();

        AddDuplicateNodeDiagnostics(orderedNodes, diagnostics);
        AddArtifactDiagnostics(orderedNodes, normalizedPaths, diagnostics);
        AddDependencyDiagnostics(orderedNodes, diagnostics);
        AddCycleDiagnostics(orderedNodes, diagnostics);
        AddOutputCollisionDiagnostics(orderedNodes, normalizedPaths, diagnostics);

        var orderedDiagnostics = diagnostics
            .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.Line)
            .ThenBy(diagnostic => diagnostic.Location?.Column)
            .ToArray();
        if (orderedDiagnostics.Length != 0)
        {
            return new SiteBuildPlanValidationResult(plan: null, orderedDiagnostics);
        }

        return new SiteBuildPlanValidationResult(
            CreateNormalizedPlan(orderedNodes, normalizedPaths),
            orderedDiagnostics);
    }

    /// <summary>指定した成果物を所有するノードを取得します。</summary>
    /// <param name="artifactId">所有者を調べる成果物の識別子。</param>
    /// <returns>成果物の宣言入力と依存関係を含む所有ノード。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifactId"/> が <see langword="null"/> です。</exception>
    /// <exception cref="KeyNotFoundException">指定した成果物が計画に存在しません。</exception>
    public BuildNode GetArtifactOwner(BuildArtifactId artifactId)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        return _ownersByArtifactId.TryGetValue(artifactId, out var owner)
            ? owner
            : throw new KeyNotFoundException($"Build artifact '{artifactId}' does not exist.");
    }

    /// <summary>前回の検証済み計画と比較し、今回無効化されるノードと理由を返します。</summary>
    /// <param name="previousPlan">比較元となる前回の検証済み計画。</param>
    /// <returns>ノード識別子の序数順に並べられた無効化結果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previousPlan"/> が <see langword="null"/> です。</exception>
    public IReadOnlyList<BuildInvalidation> GetInvalidatedNodes(SiteBuildPlan previousPlan)
    {
        ArgumentNullException.ThrowIfNull(previousPlan);
        var previousNodes = previousPlan.Nodes.ToDictionary(node => node.Id);
        var reasonsByNode = new Dictionary<BuildNodeId, SortedSet<string>>();

        foreach (var node in Nodes)
        {
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            if (!previousNodes.TryGetValue(node.Id, out var previousNode))
            {
                reasons.Add("ノードが追加されました。");
            }
            else
            {
                AddInputChangeReasons(previousNode, node, reasons);
                if (!previousNode.Dependencies.SequenceEqual(node.Dependencies))
                {
                    reasons.Add("依存関係が変更されました。");
                }

                if (!ArtifactsEqual(previousNode.Artifacts, node.Artifacts))
                {
                    reasons.Add("成果物宣言が変更されました。");
                }
            }

            if (node.Inputs.Any(input =>
                    input.Kind == BuildInputKind.Value
                    && input.Key is "template.cachePolicy" or "content.cachePolicy" or "social.cachePolicy"
                    && input.Value == "always-rebuild"))
            {
                var cachePolicy = node.Inputs.First(input =>
                    input.Kind == BuildInputKind.Value
                    && input.Key is "template.cachePolicy" or "content.cachePolicy" or "social.cachePolicy"
                    && input.Value == "always-rebuild");
                reasons.Add($"入力 'Value:{cachePolicy.Key}' が常時再ビルドを要求します。");
            }

            if (reasons.Count > 0)
            {
                reasonsByNode.Add(node.Id, reasons);
            }
        }

        var dependentsByNode = Nodes
            .SelectMany(node => node.Dependencies.Select(dependency => (Dependency: dependency, Node: node)))
            .GroupBy(pair => pair.Dependency)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Node)
                    .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                    .ToArray());
        var pending = new SortedSet<BuildNodeId>(
            reasonsByNode.Keys,
            Comparer<BuildNodeId>.Create((left, right) =>
                StringComparer.Ordinal.Compare(left.Value, right.Value)));
        while (pending.Count > 0)
        {
            var dependency = pending.Min!;
            pending.Remove(dependency);
            if (!dependentsByNode.TryGetValue(dependency, out var dependents))
            {
                continue;
            }

            foreach (var node in dependents)
            {
                var wasInvalid = reasonsByNode.TryGetValue(node.Id, out var reasons);
                if (!wasInvalid)
                {
                    reasons = new SortedSet<string>(StringComparer.Ordinal);
                    reasonsByNode.Add(node.Id, reasons);
                }

                reasons!.Add($"依存ノード '{dependency}' が無効化されました。");
                if (!wasInvalid)
                {
                    pending.Add(node.Id);
                }
            }
        }

        return Array.AsReadOnly(Nodes
            .Where(node => reasonsByNode.ContainsKey(node.Id))
            .Select(node => new BuildInvalidation(node.Id, reasonsByNode[node.Id]))
            .ToArray());
    }

    private static void AddInputChangeReasons(
        BuildNode previousNode,
        BuildNode node,
        ICollection<string> reasons)
    {
        var previousInputs = previousNode.Inputs
            .GroupBy(InputIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(input => input.Value)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var currentInputs = node.Inputs
            .GroupBy(InputIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(input => input.Value)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        foreach (var identity in previousInputs.Keys
                     .Concat(currentInputs.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var hadPrevious = previousInputs.TryGetValue(identity, out var previousValues);
            var hasCurrent = currentInputs.TryGetValue(identity, out var currentValues);
            if (!hadPrevious)
            {
                reasons.Add($"入力 '{identity}' が追加されました。");
            }
            else if (!hasCurrent)
            {
                reasons.Add($"入力 '{identity}' が削除されました。");
            }
            else if (!previousValues!.SequenceEqual(currentValues!, StringComparer.Ordinal))
            {
                reasons.Add($"入力 '{identity}' が変更されました。");
            }
        }
    }

    private static string InputIdentity(BuildInput input) => $"{input.Kind}:{input.Key}";

    private static bool ArtifactsEqual(
        IReadOnlyList<BuildArtifact> left,
        IReadOnlyList<BuildArtifact> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Id.Equals(pair.Second.Id)
            && pair.First.OwnerNodeId.Equals(pair.Second.OwnerNodeId)
            && StringComparer.Ordinal.Equals(
                pair.First.RelativeOutputPath,
                pair.Second.RelativeOutputPath));

    private static void AddDuplicateNodeDiagnostics(
        IReadOnlyList<BuildNode> nodes,
        ICollection<SiteDiagnostic> diagnostics)
    {
        foreach (var group in nodes.GroupBy(node => node.Id))
        {
            if (group.Count() > 1)
            {
                diagnostics.Add(Error(
                    SiteBuildPlanDiagnosticIds.DuplicateNodeId,
                    $"ビルドノード識別子 '{group.Key}' が {group.Count()} 回宣言されています。"));
            }
        }
    }

    private static void AddArtifactDiagnostics(
        IReadOnlyList<BuildNode> nodes,
        IDictionary<BuildArtifact, string> normalizedPaths,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var artifacts = nodes
            .SelectMany(node => node.Artifacts.Select(artifact => (Node: node, Artifact: artifact)))
            .OrderBy(pair => pair.Artifact.Id.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.Node.Id.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.Artifact.RelativeOutputPath, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in artifacts.GroupBy(pair => pair.Artifact.Id))
        {
            if (group.Count() > 1)
            {
                diagnostics.Add(Error(
                    SiteBuildPlanDiagnosticIds.DuplicateArtifactId,
                    $"成果物識別子 '{group.Key}' が {group.Count()} 回宣言されています。"));
            }
        }

        foreach (var pair in artifacts)
        {
            if (!pair.Artifact.OwnerNodeId.Equals(pair.Node.Id))
            {
                diagnostics.Add(Error(
                    SiteBuildPlanDiagnosticIds.ArtifactOwnerMismatch,
                    $"成果物 '{pair.Artifact.Id}' の所有者 '{pair.Artifact.OwnerNodeId}' は格納先ノード '{pair.Node.Id}' と一致しません。"));
            }

            try
            {
                normalizedPaths[pair.Artifact] =
                    SiteRoute.NormalizeRelativeOutputPath(pair.Artifact.RelativeOutputPath);
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                diagnostics.Add(Error(
                    SiteBuildPlanDiagnosticIds.InvalidArtifactPath,
                    $"成果物 '{pair.Artifact.Id}' の出力パス '{pair.Artifact.RelativeOutputPath}' は無効です: {exception.Message}"));
            }
        }
    }

    private static void AddDependencyDiagnostics(
        IReadOnlyList<BuildNode> nodes,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        foreach (var node in nodes)
        {
            foreach (var dependency in node.Dependencies)
            {
                if (!nodeIds.Contains(dependency))
                {
                    diagnostics.Add(Error(
                        SiteBuildPlanDiagnosticIds.MissingDependency,
                        $"ビルドノード '{node.Id}' の依存先 '{dependency}' が存在しません。"));
                }
            }
        }
    }

    private static void AddCycleDiagnostics(
        IReadOnlyList<BuildNode> nodes,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var knownIds = nodes.Select(node => node.Id.Value).ToHashSet(StringComparer.Ordinal);
        var adjacency = nodes
            .GroupBy(node => node.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.SelectMany(node => node.Dependencies)
                    .Select(dependency => dependency.Value)
                    .Where(knownIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        foreach (var component in FindStronglyConnectedComponents(adjacency))
        {
            if (component.Count == 1
                && !adjacency[component[0]].Contains(component[0], StringComparer.Ordinal))
            {
                continue;
            }

            var cycle = FindCycle(component[0], component.ToHashSet(StringComparer.Ordinal), adjacency);
            diagnostics.Add(Error(
                SiteBuildPlanDiagnosticIds.DependencyCycle,
                $"ビルドノード依存関係に循環があります: {string.Join(" -> ", cycle.Select(id => $"'{id}'"))}。"));
        }
    }

    private static void AddOutputCollisionDiagnostics(
        IReadOnlyList<BuildNode> nodes,
        IReadOnlyDictionary<BuildArtifact, string> normalizedPaths,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var artifacts = nodes
            .SelectMany(node => node.Artifacts.Select(artifact => (Node: node, Artifact: artifact)))
            .Where(pair => normalizedPaths.ContainsKey(pair.Artifact))
            .Select(pair => new ArtifactPath(
                pair.Node.Id.Value,
                pair.Artifact.Id.Value,
                normalizedPaths[pair.Artifact]))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ThenBy(item => item.NodeId, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in artifacts.GroupBy(item => item.Path, StringComparer.Ordinal))
        {
            AddPairDiagnostics(group, (left, right) => diagnostics.Add(Error(
                SiteBuildPlanDiagnosticIds.DuplicateOutputPath,
                $"成果物 '{left.ArtifactId}' と '{right.ArtifactId}' の出力パス '{left.Path}' が重複しています。")));
        }

        foreach (var group in artifacts.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            AddPairDiagnostics(group, (left, right) =>
            {
                if (!StringComparer.Ordinal.Equals(left.Path, right.Path))
                {
                    diagnostics.Add(Error(
                        SiteBuildPlanDiagnosticIds.OutputPathCaseCollision,
                        $"成果物 '{left.ArtifactId}' の出力パス '{left.Path}' と成果物 '{right.ArtifactId}' の出力パス '{right.Path}' は大文字と小文字だけが異なります。"));
                }
            });
        }

        var routeTable = new SiteRouteTable();
        for (var index = 0; index < artifacts.Length; index++)
        {
            routeTable.ReserveOutputPath(
                artifacts[index].Path,
                $"node:{artifacts[index].NodeId};artifact:{artifacts[index].ArtifactId};index:{index:D8}");
        }

        foreach (var diagnostic in routeTable.Validate().Diagnostics
                     .Where(diagnostic =>
                         diagnostic.Id == SiteRouteDiagnosticIds.OutputPathAncestorConflict))
        {
            diagnostics.Add(Error(
                SiteBuildPlanDiagnosticIds.OutputPathAncestorConflict,
                diagnostic.Message));
        }
    }

    private static SiteBuildPlan CreateNormalizedPlan(
        IReadOnlyList<BuildNode> nodes,
        IReadOnlyDictionary<BuildArtifact, string> normalizedPaths)
    {
        var snapshots = nodes.Select(node => new BuildNode(
                node.Id,
                node.Inputs,
                node.Dependencies,
                node.Artifacts.Select(artifact => new BuildArtifact(
                    artifact.Id,
                    artifact.OwnerNodeId,
                    normalizedPaths[artifact]))))
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .ToArray();
        return new SiteBuildPlan(snapshots);
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindStronglyConnectedComponents(
        IReadOnlyDictionary<string, string[]> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finishOrder = new List<string>(adjacency.Count);

        foreach (var start in adjacency.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var traversal = new Stack<TraversalFrame>();
            traversal.Push(new TraversalFrame(start, 0));
            while (traversal.TryPop(out var frame))
            {
                if (frame.NextNeighborIndex < adjacency[frame.NodeId].Length)
                {
                    var dependency = adjacency[frame.NodeId][frame.NextNeighborIndex];
                    traversal.Push(frame with
                    {
                        NextNeighborIndex = frame.NextNeighborIndex + 1,
                    });
                    if (visited.Add(dependency))
                    {
                        traversal.Push(new TraversalFrame(dependency, 0));
                    }

                    continue;
                }

                finishOrder.Add(frame.NodeId);
            }
        }

        var reverseAdjacency = adjacency.Keys.ToDictionary(
            nodeId => nodeId,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var (nodeId, dependencies) in adjacency)
        {
            foreach (var dependency in dependencies)
            {
                reverseAdjacency[dependency].Add(nodeId);
            }
        }

        foreach (var dependents in reverseAdjacency.Values)
        {
            dependents.Sort(StringComparer.Ordinal);
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<IReadOnlyList<string>>();
        for (var index = finishOrder.Count - 1; index >= 0; index--)
        {
            var start = finishOrder[index];
            if (!assigned.Add(start))
            {
                continue;
            }

            var component = new List<string>();
            var traversal = new Stack<string>();
            traversal.Push(start);
            while (traversal.TryPop(out var nodeId))
            {
                component.Add(nodeId);
                var dependents = reverseAdjacency[nodeId];
                for (var dependencyIndex = dependents.Count - 1;
                     dependencyIndex >= 0;
                     dependencyIndex--)
                {
                    var dependent = dependents[dependencyIndex];
                    if (assigned.Add(dependent))
                    {
                        traversal.Push(dependent);
                    }
                }
            }

            component.Sort(StringComparer.Ordinal);
            components.Add(component);
        }

        return components
            .OrderBy(component => component[0], StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> FindCycle(
        string start,
        IReadOnlySet<string> component,
        IReadOnlyDictionary<string, string[]> adjacency)
    {
        var path = new List<string> { start };
        var onPath = new HashSet<string>(StringComparer.Ordinal) { start };
        var traversal = new Stack<TraversalFrame>();
        traversal.Push(new TraversalFrame(start, 0));
        while (traversal.TryPop(out var frame))
        {
            var dependencies = adjacency[frame.NodeId];
            if (frame.NextNeighborIndex >= dependencies.Length)
            {
                if (!StringComparer.Ordinal.Equals(frame.NodeId, start))
                {
                    path.RemoveAt(path.Count - 1);
                    onPath.Remove(frame.NodeId);
                }

                continue;
            }

            traversal.Push(frame with
            {
                NextNeighborIndex = frame.NextNeighborIndex + 1,
            });
            var dependency = dependencies[frame.NextNeighborIndex];
            if (!component.Contains(dependency))
            {
                continue;
            }

            if (StringComparer.Ordinal.Equals(dependency, start))
            {
                path.Add(start);
                return path;
            }

            if (onPath.Add(dependency))
            {
                path.Add(dependency);
                traversal.Push(new TraversalFrame(dependency, 0));
            }
        }

        throw new InvalidOperationException("A strongly connected component must contain a cycle.");
    }

    private static string DescribeNode(BuildNode node) =>
        string.Join(
            '\u001f',
            node.Dependencies.Select(dependency => dependency.Value)
                .Concat(node.Artifacts.Select(artifact =>
                    $"{artifact.Id.Value}\u001e{artifact.OwnerNodeId.Value}\u001e{artifact.RelativeOutputPath}")));

    private static void AddPairDiagnostics<T>(
        IEnumerable<T> values,
        Action<T, T> addDiagnostic)
    {
        var items = values.ToArray();
        for (var leftIndex = 0; leftIndex < items.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < items.Length; rightIndex++)
            {
                addDiagnostic(items[leftIndex], items[rightIndex]);
            }
        }
    }

    private static SiteDiagnostic Error(string id, string message) =>
        new(id, SiteDiagnosticSeverity.Error, message);

    private readonly record struct TraversalFrame(string NodeId, int NextNeighborIndex);

    private sealed record ArtifactPath(string NodeId, string ArtifactId, string Path);
}
