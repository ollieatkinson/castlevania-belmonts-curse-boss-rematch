using HarmonyLib;
using MotherBase.ActionFramework;
using MotherBase.ApplicationFlowManager;
using RoomManager;
using UnityEngine;

namespace BossRematch;

internal static class ArenaTravel
{
    private static ArenaDestination? queued;
    private static ArenaDestination? loading;
    private static readonly ArenaTravelGate gate = new();
    private static bool saving;
    private static bool checkpointWritten;
    private static string? saveName;
    private static float deadline;
    private static bool ownsPosition;
    private static Vector3? arrival;
    private static bool returnToSave;
    internal static bool Busy => queued != null || loading != null || returnToSave;

    internal static bool Queue(ArenaDestination destination)
    {
        if (Busy || !BossMap.CanTravel(destination)) return false;
        queued = destination;
        saving = checkpointWritten = false;
        deadline = Time.realtimeSinceStartup + 20f;
        gate.Reset();
        return true;
    }

    internal static void OnSaved(string name, bool success)
    {
        if (queued != null && saving && success && name == saveName) checkpointWritten = true;
    }

    internal static void Tick()
    {
        if (returnToSave && !ApplicationFlowManager.IsLoading)
        {
            returnToSave = false;
            LevelManager.Get().ReloadSaveState(MainGameLoadingType.FastTravel);
            return;
        }
        if (queued == null) return;
        if (ApplicationFlowManager.IsLoading || Time.realtimeSinceStartup > deadline || !BossMap.CanTravel(queued))
        {
            queued = null;
            gate.Reset();
            Plugin.Logger.LogWarning("Arena travel cancelled: loading changed, checkpoint timed out, or destination became unavailable.");
            return;
        }
        if (!gate.CanStart(Session.IsGameplay(), Time.realtimeSinceStartup, Time.frameCount)) return;
        if (!saving)
        {
            saving = true;
            saveName = SaveLogic.FormatSaveNameFromIndex(SaveLogic.Get().GetCurrentSaveIndex());
            gate.Reset();
            SaveLogic.Get().SaveNoPosition(MainCharacterSingleton.GetEntity(), SaveOrigin.Flow);
            return;
        }
        if (!checkpointWritten) return;
        var target = queued;
        queued = null;
        gate.Reset();
        Start(target);
    }

    private static void Start(ArenaDestination target)
    {
        LevelID? level = null;
        foreach (var entry in LevelManager.Get().applicationStateDictionary.elements)
            if (entry.id && entry.id.guid == target.LevelGuid) { level = entry.id; break; }
        if (!level || !BossMap.CanTravel(target)) throw new InvalidOperationException("The arena destination is no longer available.");
        loading = target;
        arrival = null;
        try
        {
            ownsPosition = true;
            MainCharacterSpawnLogic.Get().SetForcePosition(target.RoomCenter, null);
            if (!LevelManager.Get().TryLoadLevelState(level, MainGameLoadingType.FastTravel, null))
                throw new InvalidOperationException("The game refused arena travel.");
            Plugin.Logger.LogInfo($"Arena travel requested: boss={target.Label}, room={target.RoomId}.");
        }
        catch { loading = null; ClearPosition(); throw; }
    }

    internal static void PrepareArrival(MainCharacterSpawnLogic spawn)
    {
        if (loading == null) return;
        var level = LevelManager.Get().loadingLevelVariable.CurrentValue;
        if (!level || level.guid != loading.LevelGuid)
        {
            loading = null;
            ClearPosition();
            return;
        }
        spawn.SetForcePosition(loading.RoomCenter, null);
    }

    internal static void Place(RoomManagerSystem manager, RoomStreamingHandle handle, ref RoomChunkID chunk)
    {
        if (loading == null) return;
        if (handle.worldRoomData.id != loading.RoomId || !handle.roomInstance)
            throw new InvalidOperationException("The loaded arena does not match the chosen destination.");
        Vector3? anchor = null;
        if (loading.RoomId == NotreDameTrials.Hall)
        {
            if (NotreDameTrials.TryTravelAnchor(handle.roomInstance, out var point)) anchor = point;
        }
        else
            foreach (var director in handle.roomInstance.GetComponentsInChildren<CutsceneDirector>(true))
                if (director.ResolverData?.name == Encounters.Supported[loading.RoomId].Resolver
                    && ReplayEcho.TryFindPosition(director, out var point)) { anchor = point; break; }
        if (anchor == null) throw new InvalidOperationException("The arena's safe Echo anchor is unavailable.");
        var player = MainCharacterSingleton.GetEntity();
        var motor = MainCharacterSingleton.GetComponent<CharacterMotor>();
        if (!motor) throw new InvalidOperationException("The player collision motor is unavailable.");
        Physics.SyncTransforms();
        Vector3? standing = null;
        foreach (var offset in new[] { 0f, -1f, 1f, -2f, 2f })
        {
            var point = anchor.Value + new Vector3(offset, 2, 0);
            point.z = player.transform.position.z;
            var ground = new CharacterGroundingReport();
            motor.ProbeGround(ref point, player.transform.rotation, 12f, ref ground);
            if (!ground.isStableOnGround || !ground.groundCollider) continue;
            point.y += 0.05f;
            if (motor.CharacterCollisionsOverlap(point, player.transform.rotation, new Collider[16], 0f, false) == 0)
            { standing = point; break; }
        }
        if (standing == null) throw new InvalidOperationException("No clear standing position was found beside the Echo.");
        MainCharacterSpawnLogic.Get().PlaceMainCharacter(standing.Value, player.transform.rotation);
        if (!manager.GetEntityRoomAndChunk(player.gameObject, out var room, out var foundChunk, out _)
            || room.worldRoomData.id != loading.RoomId)
            throw new InvalidOperationException("The arena arrival is outside the selected room.");
        chunk = foundChunk;
        arrival = standing;
    }

    internal static void CheckArrival()
    {
        if (loading == null) return;
        if (arrival == null || !MainCharacterSingleton.TryGetEntity(out var player) || !player
            || MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom?.worldRoomData.id != loading.RoomId
            || Vector3.Distance(player.transform.position, arrival.Value) > 3f)
        { Fail(new InvalidOperationException("Arena arrival could not be verified.")); return; }
        Plugin.Logger.LogInfo($"Arrived beside {loading.Label}'s Echo: room={loading.RoomId}, position={arrival.Value}. No rematch started.");
        loading = null;
        arrival = null;
        ClearPosition();
    }

    internal static void Fail(Exception error)
    {
        Plugin.Logger.LogError($"Arena travel failed; returning to the saved mirror: {error}");
        loading = null;
        arrival = null;
        ClearPosition();
        returnToSave = true;
    }

    private static void ClearPosition()
    {
        if (!ownsPosition) return;
        MainCharacterSpawnLogic.Get().ClearForcePosition();
        ownsPosition = false;
    }
}

[HarmonyPatch(typeof(RoomManagerSystem), nameof(RoomManagerSystem.OnFirstRoomLoaded))]
internal static class PlaceBesideArenaEcho
{
    private static void Prefix(RoomManagerSystem __instance, RoomStreamingHandle __0, ref RoomChunkID __1)
    {
        try { ArenaTravel.Place(__instance, __0, ref __1); }
        catch (Exception e) { ArenaTravel.Fail(e); }
    }
}
