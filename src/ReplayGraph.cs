using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BossRematch;

internal sealed class ReplayGraph : IDisposable
{
    private readonly Dictionary<int, CutsceneResolverData> clones = new();
    private readonly List<CutsceneData> cutscenes = new();
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

    internal CutsceneResolverData BuildStrengthTrial(CutsceneResolverData source)
    {
        var clone = Clone(source, 0);
        var nodes = clone.Nodes;
        if (source.name != "Strenght_Challenge" || nodes.Length != 20
            || nodes[0].TryCast<ResolverStartNodeData>() == null
            || nodes[5].TryCast<ResolverConditionHasWatchedCutsceneNodeData>() == null
            || nodes[7].TryCast<ResolverStartBossFightNodeData>() == null
            || nodes[8].TryCast<ResolverWaitForProgressionKeyNodeData>() == null
            || nodes[9].TryCast<ResolverEndBossFightNodeData>() == null
            || nodes[14].TryCast<ResolverClearBossFightNodeData>() == null)
            throw new InvalidOperationException("The Strength trial layout differs from the audited game build.");
        // Start at the authored repeat intro, bypassing the completed-virtue
        // check and entrance crossing. End/Clear skip the reward bell and quest.
        nodes[0].NextNodes = new[] { 5 };
        nodes[9].NextNodes = new[] { 14 };
        return clone;
    }

    internal static void ValidateCourse(CutsceneResolverData source)
    {
        var nodes = source.Nodes;
        if (nodes.Length != 3 || source.StartIndexes.Length != 1 || source.StartIndexes[0] != 0
            || nodes[0].TryCast<ResolverStartNodeData>() == null
            || nodes[1].TryCast<ResolverConditionHasWatchedCutsceneNodeData>() == null
            || nodes[2].TryCast<ResolverPlayCutsceneNodeData>() == null
            || nodes[0].NextNodes.Length != 1 || nodes[0].NextNodes[0] != 1
            || nodes[1].NextNodes.Length != 0 || nodes[2].NextNodes.Length != 0)
            throw new InvalidOperationException("The course introduction has an unsupported layout.");
        var condition = nodes[1].Cast<ResolverConditionHasWatchedCutsceneNodeData>();
        if (condition.ElseNodes.Length != 1 || condition.ElseNodes[0] != 2)
            throw new InvalidOperationException("The course introduction has an unsupported branch.");
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

        if (source.name == "The_Self_Encounter") RewriteRoseRepeatIntro(source, clone);
        if (source.name == "Notre_Dame_Sacred_Chapel_Sacred_Sword") RewriteArthurSetup(clone);
        if (source.name == "BossOutro")
        {
            RewriteOutro(clone);
            outros++;
            return clone;
        }
        if (source.name == "BossIntro") RewriteIntro(clone);
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

    private static void RewriteArthurSetup(CutsceneResolverData graph)
    {
        var nodes = graph.Nodes;
        var intro = nodes.Length == 15 ? nodes[4].TryCast<ResolverPlaySubResolverNodeData>() : null;
        if (intro?.SubResolverData?.name != "BossIntro"
            || nodes[2].TryCast<ResolverConditionProgressionKeyNodeData>() == null
            || nodes[3].TryCast<ResolverTriggerCutsceneEventNodeData>() == null
            || nodes[8].TryCast<ResolverFadeToBackNodeData>() == null
            || !nodes[2].NextNodes.SequenceEqual(new[] { 3, 4, 8 }))
            throw new InvalidOperationException("The Arthur encounter setup has changed.");
        var variables = intro.VariablesContainer;
        var fight = variables.GetVariable("1a7381d9-225e-4f6d-ab89-04964e6a9559")?.TryCast<BossFightVariable>();
        var anchor = variables.GetVariable("b64a3f06-a8d2-4275-89de-c3110b858a5f")?.TryCast<CutsceneAnchorVariable>();
        var direction = variables.GetVariable("f1ecd04c-fef8-4631-b891-814628d723b1")?.TryCast<SpawnLookDirectionVariable>();
        var action = variables.GetVariable("1a70a449-6b46-42d7-88bc-068dfe91d8b2")?.TryCast<ActionTriggerVariable>();
        if (fight == null || string.IsNullOrEmpty(fight.Value) || anchor == null || string.IsNullOrEmpty(anchor.Value)
            || direction == null || action == null || !action.Value || action.Value.name != "Arthur_BaseMovement")
            throw new InvalidOperationException("Arthur's authored boss setup is unavailable.");
        // The story branch starts a window-breaking Sequence in parallel with
        // the intro. That Sequence owns a game state and can outlive its action
        // on an already-broken window. Replays only need the boss's native setup.
        nodes[2] = new ResolverSetupBossNodeData
        {
            BossFight = new BossFightWithRefData { Value = fight.Value },
            AnchorData = new CutsceneAnchorWithRefData { Value = anchor.Value },
            DirectionData = new SpawnLookDirectionWithRefData { Value = direction.Value },
            ActionData = new ActionTriggerWithRefData { Value = action.Value },
            NextNodes = new[] { 4 }
        };
    }

    private void RewriteRoseRepeatIntro(CutsceneResolverData source, CutsceneResolverData clone)
    {
        const string introId = "6fa2bd2e-1ef5-4329-bc3b-bd025f0d6eb6";
        const string repeatId = "165c9075-defb-4b49-874b-eb606a801c9e";
        var original = source.Nodes.Length == 8 ? source.Nodes[4].TryCast<ResolverPlaySubResolverNodeData>() : null;
        var replay = clone.Nodes.Length == 8 ? clone.Nodes[4].TryCast<ResolverPlaySubResolverNodeData>() : null;
        if (original?.SubResolverData?.name != "BossIntroNoTrigger" || replay == null)
            throw new InvalidOperationException("The Cursed Rose introduction has an unsupported layout.");
        var intro = original.VariablesContainer.GetVariable(introId)?.TryCast<CutsceneDataVariable>()?.Value;
        var repeat = original.VariablesContainer.GetVariable(repeatId)?.TryCast<CutsceneDataVariable>();
        var privateRepeat = replay.VariablesContainer.GetVariable(repeatId)?.TryCast<CutsceneDataVariable>();
        if (!intro || intro!.name != "Notre_Dame_Prophecy_Self_Intro" || intro.Nodes.Length != 116
            || repeat == null || privateRepeat == null || repeat.Pointer == privateRepeat.Pointer
            || !repeat.Value || repeat.Value.name != "Notre_Dame_Boss_Doppelganger_Intro_Random")
            throw new InvalidOperationException("Could not isolate the Cursed Rose repeat introduction.");
        var arenaCamera = intro.Nodes[88].TryCast<CutsceneEnableCameraNodeData>();
        if (arenaCamera == null || !arenaCamera.Enable || !arenaCamera.OverrideTransition
            || arenaCamera.CameraID != "2d57bbb4-2ce1-40ed-b66a-8c893bf9580b")
            throw new InvalidOperationException("The Cursed Rose arena camera has changed.");

        var cutscene = Object.Instantiate(repeat.Value);
        cutscenes.Add(cutscene);
        var nodes = cutscene.Nodes;
        if (cutscene == repeat.Value || nodes.Length != 19 || cutscene.StartIndexes.Length != 1
            || cutscene.StartIndexes[0] != 0 || nodes[0].TryCast<CutsceneStartNodeData>() == null
            || nodes[18].TryCast<CutsceneStopCameraBlendsData>() == null || nodes[18].NextNodes.Length != 0)
            throw new InvalidOperationException("The Cursed Rose repeat cutscene has changed.");
        for (var i = 0; i < nodes.Length; i++)
            if (nodes[i].Pointer == repeat.Value.Nodes[i].Pointer)
                throw new InvalidOperationException("Cursed Rose cutscene nodes are shared with the original.");

        // The first intro enables B3, the room's combat camera. The repeat
        // intro assumes it is still enabled after a death, but a fresh replay
        // loads the room with B3 disabled. Enable that same authored camera
        // under the intro's black fade, retaining native placement and music.
        var expanded = new Il2CppReferenceArray<NodeDataBase>(nodes.Length + 1);
        for (var i = 0; i < nodes.Length; i++) expanded[i] = nodes[i];
        expanded[nodes.Length] = new CutsceneEnableCameraNodeData
        {
            CameraID = arenaCamera.CameraID,
            Enable = arenaCamera.Enable,
            OverrideTransition = arenaCamera.OverrideTransition,
            Transition = arenaCamera.Transition,
            NextNodes = Array.Empty<int>()
        };
        nodes[18].NextNodes = new[] { nodes.Length };
        cutscene.Nodes = expanded;
        privateRepeat.Value = cutscene;
        Plugin.Logger.LogInfo("Prepared Cursed Rose repeat intro with its authored arena camera.");
    }

    private static void RewriteIntro(CutsceneResolverData graph)
    {
        // Speaking to the Echo already starts the encounter. The normal intro
        // waits for an entrance crossing that a player loaded inside the arena
        // may never make, leaving pre-intro camera settings active indefinitely.
        // Keep the authored intro, setup, fight start and completion wait.
        var nodes = graph.Nodes;
        if (nodes.Length != 8 || graph.StartIndexes.Length != 1 || graph.StartIndexes[0] != 0
            || nodes[0].TryCast<ResolverStartNodeData>() == null
            || nodes[1].TryCast<ResolverActivateTriggerNodeData>() == null
            || nodes[2].TryCast<ResolverConditionHasWatchedCutsceneNodeData>() == null
            || nodes[4].TryCast<ResolverStartBossFightNodeData>() == null
            || nodes[6].TryCast<ResolverWaitForProgressionKeyNodeData>() == null
            || nodes[0].NextNodes.Length != 1 || nodes[0].NextNodes[0] != 1
            || nodes[1].NextNodes.Length != 1 || nodes[1].NextNodes[0] != 2)
            throw new InvalidOperationException("BossIntro layout differs from the audited game build.");
        nodes[0].NextNodes = new[] { 2 };
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
        foreach (var cutscene in cutscenes) if (cutscene) Object.Destroy(cutscene);
        cutscenes.Clear();
    }
}
