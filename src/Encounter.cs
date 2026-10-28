using MotherBase.RoomDesigner;
using RedWine.ResourceManagement;
using UnityEngine;

namespace BossRematch;

internal sealed record Encounter(string RoomId, string Label, string ResolverName, BossData? Boss,
    CutsceneDirector? Director, RoomInstance Room, int Branch, Vector3 EchoPosition, TrialSpec? Trial = null)
{
    internal string Key => Trial != null ? $"trial:{Trial.RoomId}" : RoomId;
    internal string FightRoomId => Trial?.RoomId ?? RoomId;
    internal string CharacterGuid => Trial?.CharacterGuid ?? Boss!.characterID.guid;
}

internal static class Encounters
{
    private static readonly HashSet<string> disabled = new(StringComparer.Ordinal);
    internal static bool IsDisabled(string key) => disabled.Contains(key);
    internal static void Disable(Encounter target, Exception error)
    {
        if (disabled.Add(target.Key))
            Plugin.Logger.LogWarning($"Disabled incompatible encounter for this game session: {target.Label}. {error.Message}");
    }

    // Authored encounter graphs inspected on Steam build 25975867. Endings and
    // non-combat "FakeBossFight" scenes have different lifecycles.
    internal static readonly Dictionary<string, (string Label, string Resolver)> Supported = new()
    {
        ["World_Biome_01_Streets_Poor_Room_Gym_Boss_JeanneDArc_uid69"] = ("Jeanne d’Arc", "Joan_Arena"),
        ["World_Biome_01_Streets_Poor_StreetsPoor_Room_Boss_Leon_MainRoom_uid80"] = ("Leon", "Catacombs_Door_SaveForLater"),
        ["World_Biome_02_InnocentsCemetery_Gym_Boss_Medusa_BossRoom_uid101"] = ("Medusa", "Medusa_Arena"),
        ["World_Biome_03_Catacombs_MillersBridge_13_BossRoom_uid105"] = ("Mammon", "Catacombs_Mammon_Arena"),
        ["World_Biome_05_NotreDame_NotreDame_MVP_BossRoom_Doppelganger_uid144"] = ("Cursed Rose", "The_Self_Encounter"),
        ["World_Biome_05_NotreDame_NotreDame_MVP_Room_Boss_Arthur_uid121"] = ("Arthur", "Notre_Dame_Sacred_Chapel_Sacred_Sword"),
        ["World_Biome_08_Library_Gym_Boss_Carmilla_Room01_uid133"] = ("Carmilla", "Castle_Library_Observatory_Carmilla"),
        ["World_Biome_09_ClockTower_ClockTower_MVP_Room34_uid134"] = ("Talos", "Castle_ClockTower_Architect_Quarters_Talos"),
        ["World_Biome_09_ClockTower_ClockTower_MVP_Room47_uid150"] = ("Isaac", "Castle_ClockTower_Isaac_Arena"),
        ["World_Biome_10_HallOfMirrors_Gym_Boss_SuccubusAgnes_uid144"] = ("Agnes", "Castle_Hall_Mirrors_Rose_Room"),
    };

    internal static IEnumerable<Encounter> FindAll(bool requireGameplay = true)
    {
        var boss = FindBoss(requireGameplay);
        if (boss != null && !IsDisabled(boss.Key)) yield return boss;
        foreach (var trial in NotreDameTrials.Find(requireGameplay))
            if (!IsDisabled(trial.Key)) yield return trial;
    }

    internal static Encounter? Find(bool requireGameplay = true, string? key = null)
        => FindAll(requireGameplay).FirstOrDefault(e => key == null || e.Key == key);

    private static Encounter? FindBoss(bool requireGameplay)
    {
        if (!Plugin.Ready || Session.Current != null || MotherBase.ApplicationFlowManager.ApplicationFlowManager.IsLoading
            || (requireGameplay && !Session.IsGameplay()) || !MainCharacterSingleton.TryGetEntity(out var player) || !player) return null;
        var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
        if (!room || room!.worldRoomData == null || !Supported.TryGetValue(room.worldRoomData.id, out var spec)) return null;
        var fights = room.GetComponentsInChildren<BossFight>(true);
        if (fights.Length != 1) return null;
        var boss = ResourceManager.LoadAssetByRef<BossData>(ResourceContext.Room, fights[0]._bossDataRef);
        if (!boss || !boss.characterID || boss.bossFightContextType != BossFightContextType.ArcanaAbsorption) return null;
        var progression = MainCharacterSingleton.GetComponent<CharacterProgressionComponent>();
        if (!progression || progression.GetProgressionStep(boss.characterID) != CharacterProgressionStep.Absorbed) return null;
        foreach (var director in room.GetComponentsInChildren<CutsceneDirector>(true))
        {
            var graph = director.ResolverData;
            if (!graph || graph.name != spec.Resolver) continue;
            var branch = FindBranch(graph, boss.characterID);
            if (branch >= 0 && ReplayEcho.TryFindPosition(director, out var position))
                return new(room.worldRoomData.id, spec.Label, spec.Resolver, boss, director, room, branch, position);
        }
        return null;
    }

    internal static int FindBranch(CutsceneResolverData graph, CharacterID character)
    {
        for (var i = 0; i < graph.Nodes.Length; i++)
        {
            var condition = graph.Nodes[i].TryCast<ResolverConditionArcanaStateNodeData>();
            if (condition != null && !condition.Character.UseRef && condition.Character.Value == character
                && condition.ArcanaState == CharacterProgressionStep.Absorbed && condition.ElseNodes.Length == 1)
                return condition.ElseNodes[0];
        }
        return -1;
    }
}
