using HarmonyLib;
using MotherBase.ActionFramework;
using MotherBase.RoomDesigner;
using System.Reflection;
using UnityEngine;

namespace BossRematch;

internal sealed record TrialSpec(string RoomId, string Label, string Resolver, string CharacterGuid, int DoorId,
    float SpiritOffset, bool IsBoss = false);

internal static class NotreDameTrials
{
    internal const string Hall = "World_Biome_05_NotreDame_NotreDame_MVP_Room_165_uid90";
    internal const string CursedRose = "53500253a3c9f3049837a73294af9236";
    internal static readonly TrialSpec[] Supported =
    {
        new("World_Biome_05_NotreDame_NotreDame_MVP_Room_300_uid128", "Justice", "Justice_Challenge",
            "44ae5a8c951ccfd48947002ec28baf42", 100, -2.25f),
        new("World_Biome_05_NotreDame_NotreDame_MVP_Room_302_uid133", "Temperance", "Temperance_Challenge",
            "f302873f7d791ff4db8298dd137524f6", 400, 2.25f),
        new("World_Biome_05_NotreDame_NotreDame_MVP_Room_303_uid134", "Prudence", "Prudence_Challenge",
            "df983c362202bfb498c5f540259a97c4", 200, 2.25f),
        new("World_Biome_05_NotreDame_NotreDame_MVP_Room_304_uid143", "Strength — Joan of Arc", "Strenght_Challenge",
            "c780f2bb5d95b6d4e88196839e50ecb9", 300, -2.25f),
        new("World_Biome_05_NotreDame_NotreDame_MVP_BossRoom_Doppelganger_uid144", "Cursed Rose", "The_Self_Encounter",
            CursedRose, 700, 3f, true),
    };

    // Read the saved collections directly: an active replay deliberately makes
    // one completed trial look unfinished to the native portal and its course.
    internal static bool Completed(TrialSpec spec)
    {
        var progression = MainCharacterSingleton.GetComponent<CharacterProgressionComponent>();
        if (!progression) return false;
        if (spec.IsBoss)
        {
            foreach (var pair in progression.characterProgressionSteps)
                if (pair.Key && pair.Key.guid == spec.CharacterGuid)
                    return pair.Value == CharacterProgressionStep.Absorbed;
            return false;
        }
        foreach (var character in progression.obtainedVirtuesCharacters)
            if (character && character.guid == spec.CharacterGuid) return true;
        return false;
    }

    internal static bool CanTravel => BossMap.WasVisited(Hall) && Supported.Any(Completed);

    internal static GPE_VirtuousZDoorEnter? Door(RoomInstance room, TrialSpec spec)
    {
        foreach (var door in room.GetComponentsInChildren<GPE_VirtuousZDoorEnter>(true))
            if (door && door.id == spec.DoorId
                && door.linkedVirtuousComponent?.linkedArcanaCharacter?.guid == spec.CharacterGuid)
                return door;
        return null;
    }

    internal static bool Equipped(Encounter target)
    {
        if (target.Trial == null || target.Trial.IsBoss) return true;
        var door = Door(target.Room, target.Trial);
        var gate = door?.GetComponent<VirtuousEntranceComponent>();
        return gate && gate!.IsCharacterSpellEquipped();
    }

    internal static IEnumerable<Encounter> Find(bool requireGameplay)
    {
        if (!Plugin.Ready || Session.Current != null || MotherBase.ApplicationFlowManager.ApplicationFlowManager.IsLoading
            || (requireGameplay && !Session.IsGameplay()) || !MainCharacterSingleton.TryGetEntity(out var player) || !player)
            yield break;
        var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
        if (!room || room!.worldRoomData.id != Hall) yield break;
        foreach (var spec in Supported)
        {
            if (!Completed(spec)) continue;
            var door = Door(room, spec);
            if (door && TryStandingPosition(door!.transform.position + new Vector3(spec.SpiritOffset, 0, 0), out var point))
                yield return new(Hall, spec.Label, spec.Resolver, null, null, room, -1, point, spec);
        }
    }

    internal static bool TryTravelAnchor(RoomInstance room, out Vector3 point)
    {
        // A single arrival beside the central portal serves all five memories.
        var door = Door(room, Supported[^1]);
        point = default;
        return door && TryStandingPosition(door!.transform.position + new Vector3(3, 0, 0), out point);
    }

    private static bool TryStandingPosition(Vector3 anchor, out Vector3 point)
    {
        point = default;
        var motor = MainCharacterSingleton.GetComponent<CharacterMotor>();
        if (!motor) return false;
        Physics.SyncTransforms();
        var player = MainCharacterSingleton.GetEntity();
        var candidate = anchor + new Vector3(0, 2, 0);
        candidate.z = player.transform.position.z;
        var ground = new CharacterGroundingReport();
        motor.ProbeGround(ref candidate, player.transform.rotation, 5f, ref ground);
        if (!ground.isStableOnGround || !ground.groundCollider) return false;
        candidate.y += 0.05f;
        if (motor.CharacterCollisionsOverlap(candidate, player.transform.rotation, new Collider[16], 0f, false) != 0) return false;
        point = candidate;
        return true;
    }
}

[HarmonyPatch]
internal static class ResetReplayCourseMechanisms
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in new[] { typeof(StateMachineSavableStateTranslator), typeof(DoorSavableStateTranslator),
            typeof(ActivableSavableStateTranslator), typeof(GradualStateSavableStateTranslator),
            typeof(AngleRotationSavableStateTranslator), typeof(EncounterGateStateTranslator) })
            yield return AccessTools.Method(type, nameof(StateMachineSavableStateTranslator.LoadSaveStateString));
    }

    private static bool Prefix(SavableEntityBase __1)
    {
        var session = Session.Current;
        if (session?.State.CanVirtualize != true || session.Target.Trial is not { IsBoss: false } || !__1) return true;
        var room = __1.GetComponentInParent<RoomInstance>();
        // Keep the newly loaded course's authored lever, gate and mechanism
        // values instead of hydrating their completed save records. Consumed
        // pickups and portal state still use their real save translators.
        return !room || room!.worldRoomData.id != session.Target.FightRoomId;
    }
}

[HarmonyPatch(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.ObtainedArcanaVirtue))]
internal static class ReadReplayVirtue
{
    private static bool Prefix(CharacterID __0, ref bool __result)
    {
        var session = Session.Current;
        if (session?.State.CanVirtualize == true && __0
            && session.State.Virtues.TryGetValue(__0.guid, out var value))
        { __result = value; return false; }
        return true;
    }
}

[HarmonyPatch(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.SetCharacterVirtueObtained))]
internal static class NoReplayVirtueReward
{
    private static bool Prefix(CharacterID __0)
    {
        var session = Session.Current;
        if (session?.State.CanVirtualize != true || !__0) return true;
        session.State.Virtues[__0.guid] = true;
        if (session.Target.Trial is { IsBoss: false } trial && trial.CharacterGuid == __0.guid)
            session.TrialCompleted();
        return false;
    }
}
