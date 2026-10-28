using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BossRematch;

internal sealed class ReplayGraph : IDisposable
{
    private readonly Dictionary<int, CutsceneResolverData> clones = new();
    private int outros;
    internal CutsceneResolverData Build(CutsceneResolverData source, int branch)
    {
        var graph = Clone(source, 0);
        if (outros == 0) throw new InvalidOperationException("The encounter has no supported BossOutro cleanup graph.");
        if (branch < 0 || branch >= graph.Nodes.Length) throw new InvalidOperationException("Invalid fight branch.");
        if (graph.StartIndexes.Length != 1) throw new InvalidOperationException("Unexpected encounter start nodes.");
        var start = graph.Nodes[graph.StartIndexes[0]].TryCast<ResolverStartNodeData>();
        if (start == null) throw new InvalidOperationException("The encounter start node is missing.");
        start.NextNodes = new[] { branch };
        return graph;
    }

    private CutsceneResolverData Clone(CutsceneResolverData source, int depth)
    {
        if (depth > 16) throw new InvalidOperationException("Encounter graph nesting exceeds the supported limit.");
        if (clones.TryGetValue(source.GetInstanceID(), out var existing)) return existing;
        var clone = Object.Instantiate(source);
        clones.Add(source.GetInstanceID(), clone);
        // Unity must clone SerializeReference nodes as well as the ScriptableObject.
        // Refuse to edit if this Unity version leaves any node shared.
        if (clone == source || clone.Nodes.Length != source.Nodes.Length)
            throw new InvalidOperationException("Could not make a private encounter graph.");
        for (var i = 0; i < clone.Nodes.Length; i++)
            if (clone.Nodes[i].Pointer == source.Nodes[i].Pointer)
                throw new InvalidOperationException("Encounter nodes are shared with the original graph.");

        if (source.name == "BossOutro")
        {
            RewriteOutro(clone);
            outros++;
            return clone;
        }
        for (var i = 0; i < clone.Nodes.Length; i++)
        {
            var node = clone.Nodes[i];
            var sub = node.TryCast<ResolverPlaySubResolverNodeData>();
            if (sub != null && sub.SubResolverData)
            {
                var wasOutro = sub.SubResolverData.name == "BossOutro";
                sub.SubResolverData = Clone(sub.SubResolverData, depth + 1).Cast<CutsceneSubResolverData>();
                // After cleanup, don't continue into key rewards, gates or quests.
                if (wasOutro) sub.NextNodes = Array.Empty<int>();
            }
            if (node.TryCast<ResolverSaveNodeData>() != null)
                clone.Nodes[i] = new ResolverStartNodeData { NextNodes = node.NextNodes };
        }
        return clone;
    }

    private static void RewriteOutro(CutsceneResolverData graph)
    {
        // Keep the game's state push/pop and End/Clear cleanup. The orb, loot,
        // absorption cutscene and character-state changes are unreachable.
        var nodes = graph.Nodes;
        if (nodes.Length != 12 || nodes[0].TryCast<ResolverStartNodeData>() == null
            || nodes[1].TryCast<ResolverGameStateNodeData>() == null
            || nodes[2].TryCast<ResolverEndBossFightNodeData>() == null
            || nodes[10].TryCast<ResolverClearBossFightNodeData>() == null
            || nodes[11].TryCast<ResolverGameStateNodeData>() == null)
            throw new InvalidOperationException("BossOutro layout differs from the audited game build.");
        var push = nodes[1].Cast<ResolverGameStateNodeData>();
        var pop = nodes[11].Cast<ResolverGameStateNodeData>();
        if ((int)push.Action != 0 || (int)pop.Action != 1 || push.TargetState != pop.TargetState)
            throw new InvalidOperationException("BossOutro game states are not a balanced push/pop pair.");
        nodes[0].NextNodes = new[] { 1 };
        nodes[1].NextNodes = new[] { 2 };
        nodes[2].NextNodes = new[] { 10 };
        nodes[10].NextNodes = new[] { 11 };
        nodes[11].NextNodes = Array.Empty<int>();
    }

    public void Dispose()
    {
        foreach (var clone in clones.Values) if (clone) Object.Destroy(clone);
        clones.Clear();
    }
}
