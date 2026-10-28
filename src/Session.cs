using MotherBase.ApplicationFlowManager;
using MotherBase.RoomDesigner;
using UnityEngine;

namespace BossRematch;

internal sealed class Session
{
    internal static Session? Current;
    internal static Encounter? Pending;
    private static float pendingDeadline;
    private static bool checkpointWritten;
    private static string? pendingSaveName;
    private static float readySince = float.NaN;
    private static int readyFrame;
    internal readonly ReplayState State = new();
    internal readonly Encounter Target;
    private readonly Vector3 position;
    private readonly LevelID level;
    private readonly int slot;
    private readonly ReplayGraph graph = new();
    private bool ownsForcePosition;
    private bool expectedLoad;
    private bool returnRequested;
    private bool graphPrepared;
    private bool started;
    private BossFight? awaitingFight;
    private float startDeadline;
    internal CutsceneDirector? Director;

    private Session(Encounter target, Vector3 position, LevelID level, int slot)
    {
        Target = target;
        this.position = position;
        this.level = level;
        this.slot = slot;
        State.Characters[target.Boss.characterID.guid] = (int)CharacterProgressionStep.Found;
        if (target.Boss.defeatedProgressionKey)
            State.Keys[target.Boss.defeatedProgressionKey.guid] = false;
        startDeadline = Time.realtimeSinceStartup + 45;
    }

    internal static bool IsGameplay()
    {
        if (ApplicationFlowManager.IsLoading || !MainCharacterSingleton.TryGetEntity(out var player) || !player) return false;
        var state = GameStateManager.GetInstance();
        var ui = UIManager.GetInstance();
        return state && state.IsGameplayState() && ui
            && (!ui.TryGetMenuManager(out var menu) || !menu || !menu.IsShowing())
            && (!ui.confirmationPopup || !ui.confirmationPopup.visible);
    }

    internal static void Request(Encounter target)
    {
        if (Current != null || Pending != null || ArenaTravel.Busy || !IsGameplay()) return;
        var current = Encounters.Find();
        if (current == null || current.RoomId != target.RoomId) return;
        using (var validation = new ReplayGraph()) validation.Build(current.Director.ResolverData, current.Branch);
        Pending = current;
        checkpointWritten = false;
        pendingSaveName = SaveLogic.FormatSaveNameFromIndex(SaveLogic.Get().GetCurrentSaveIndex());
        pendingDeadline = Time.realtimeSinceStartup + 15;
        readySince = float.NaN;
        try { SaveLogic.Get().SaveNoPosition(MainCharacterSingleton.GetEntity(), SaveOrigin.Flow); }
        catch { Pending = null; throw; }
        Plugin.Logger.LogInfo($"Preparing rematch checkpoint: {target.Label}.");
    }

    internal static void OnSaved(string name, bool success)
    {
        if (Pending != null && success && name == pendingSaveName) checkpointWritten = true;
    }

    internal static void Tick()
    {
        if (Pending != null)
        {
            if (ApplicationFlowManager.IsLoading || Time.realtimeSinceStartup > pendingDeadline)
            {
                Pending = null;
                ReplayUI.Notice("Rematch cancelled: the checkpoint was not ready.");
                return;
            }
            var ready = checkpointWritten && IsGameplay();
            if (!ready) readySince = float.NaN;
            else if (float.IsNaN(readySince))
            {
                readySince = Time.realtimeSinceStartup;
                readyFrame = Time.frameCount;
            }
            if (ready && Time.realtimeSinceStartup - readySince >= 0.2f && Time.frameCount - readyFrame >= 2)
            {
                var target = Pending;
                Pending = null;
                var current = Encounters.Find();
                if (current == null || current.RoomId != target.RoomId) return;
                var player = MainCharacterSingleton.GetEntity();
                var session = new Session(current, player.transform.position, LevelManager.Get().currentLevelVariable.CurrentValue,
                    SaveLogic.Get().GetCurrentSaveIndex());
                Current = session;
                try { session.Load(); }
                catch { session.Release(); throw; }
            }
        }
        var active = Current;
        if (active == null || ApplicationFlowManager.IsLoading) return;
        if (active.returnRequested)
        {
            active.returnRequested = false;
            active.ReturnToCheckpoint();
        }
        else if (active.awaitingFight && active.awaitingFight!._currentBossVariable
            && active.awaitingFight._currentBossVariable.CurrentValue)
        {
            var fight = active.awaitingFight;
            active.awaitingFight = null;
            fight.StartBossFight();
        }
        else if (!active.started && active.State.Phase == ReplayState.Stage.Fighting
            && Time.realtimeSinceStartup > active.startDeadline)
        {
            ReplayUI.Notice("The encounter did not start. Returning to the arena.");
            active.RequestReturn();
        }
    }

    private void Load()
    {
        expectedLoad = true;
        ownsForcePosition = true;
        MainCharacterSpawnLogic.Get().SetForcePosition(position, null);
        if (!LevelManager.Get().TryLoadLevelState(level, MainGameLoadingType.FastTravel, null))
            throw new InvalidOperationException("The game refused the rematch transition.");
    }

    internal void OnInitialize(MainCharacterSpawnLogic spawn)
    {
        if (expectedLoad)
        {
            expectedLoad = false;
            spawn.SetForcePosition(position, null);
        }
        else
        {
            // Death, quitting or another mod's travel ends the replay. Do not
            // override that transition's destination or native retry location.
            State.Return();
            ClearForcePosition();
            Plugin.Logger.LogInfo("Rematch interrupted by an external load.");
        }
    }

    internal void OnArrived()
    {
        ClearForcePosition();
        if (State.Phase == ReplayState.Stage.Returning)
        {
            Release();
            return;
        }
        var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
        if (!room || room!.worldRoomData.id != Target.RoomId || !graphPrepared)
        {
            Plugin.Logger.LogWarning("The rematch arena or replay graph was unavailable; restoring the checkpoint.");
            RequestReturn();
            return;
        }
        State.Arrived();
        startDeadline = Time.realtimeSinceStartup + 45;
        Plugin.Logger.LogInfo($"Rematch arena loaded: {Target.Label}; room={Target.RoomId}.");
    }

    internal void PrepareDirector(CutsceneDirector director)
    {
        if (!State.CanVirtualize || director.ResolverData?.name != Target.ResolverName) return;
        var room = director.GetComponentInParent<RoomInstance>();
        if (!room || room.worldRoomData.id != Target.RoomId) return;
        if (graphPrepared) throw new InvalidOperationException("The replay director was initialized twice.");
        var branch = Encounters.FindBranch(director.ResolverData, Target.Boss.characterID);
        var clone = graph.Build(director.ResolverData, branch);
        Director = director;
        director.ResolverData = clone;
        graphPrepared = true;
        Plugin.Logger.LogInfo($"Created private replay graph: {Target.ResolverName}, branch={branch}.");
    }

    internal bool AllowDirector(CutsceneDirector director)
    {
        if (!State.CanVirtualize) return true;
        var room = director.GetComponentInParent<RoomInstance>();
        return !room || room.worldRoomData.id != Target.RoomId || (Director && director == Director);
    }

    internal bool Owns(BossFight fight)
    {
        var room = fight.GetComponentInParent<RoomInstance>();
        return State.CanVirtualize && room && room.worldRoomData.id == Target.RoomId;
    }


    internal bool PrepareSpawner(BaseMobsSpawner source)
    {
        if (!State.CanVirtualize) return false;
        var spawner = source.TryCast<MobsSpawner>();
        var room = source.GetComponentInParent<RoomInstance>();
        if (spawner == null || !spawner || room == null || !room || room.worldRoomData.id != Target.RoomId
            || spawner.mobsToSpawn == null || spawner.mobsToSpawn.Count != 1) return false;
        var boss = spawner.mobsToSpawn[0].mobData?.TryCast<BossData>();
        if (boss == null || !boss || !boss.characterID || boss.characterID.guid != Target.Boss.characterID.guid
            || !State.TryPrepareSpawner(spawner.GetInstanceID())) return false;
        // The native initializer restores only surviving mobs when HasSpawned
        // is set. A completed save therefore restores no boss. A full reset
        // selects SpawnInit for this single matching boss spawner instead.
        spawner.ignoreForcedFullReset = false;
        var progression = MainCharacterSingleton.GetComponent<CharacterProgressionComponent>();
        Plugin.Logger.LogInfo($"Resetting replay boss spawner: {Target.Label}, previouslySpawned={spawner.persistentData.HasSpawned}, initialized={spawner.initialized}, spawned={spawner.spawnedMobsCount}, initial={spawner.initialySpawnedMobsCount}, progression={progression?.GetProgressionStep(boss.characterID)}.");
        return true;
    }

    internal void OnSpawnerInitialized(BaseMobsSpawner source)
    {
        var spawner = source.TryCast<MobsSpawner>();
        if (spawner == null) return;
        Plugin.Logger.LogInfo($"Replay boss spawn result: {Target.Label}, spawned={spawner.spawnedMobsCount}, alive={spawner.aliveSpawnedMobs?.Count ?? 0}, queued={spawner.waitingMobsToSpawn?.Count ?? 0}.");
    }

    internal void OnFightStarted(BossFight fight)
    {
        if (!Owns(fight)) return;
        if (!fight._currentBossVariable || !fight._currentBossVariable.CurrentValue)
        {
            if (!awaitingFight) Plugin.Logger.LogWarning("Boss start requested before its entity was ready; waiting for spawn.");
            awaitingFight = fight;
            return;
        }
        awaitingFight = null;
        started = true;
        Plugin.Logger.LogInfo($"Rematch started: {Target.Label}.");
    }

    internal void OnFightCleared(BossFight fight)
    {
        if (!Owns(fight) || !started) return;
        Plugin.Logger.LogInfo($"Rematch won: {Target.Label}. Restoring checkpoint.");
        RequestReturn();
    }

    internal void RequestReturn() => returnRequested = true;

    private void ReturnToCheckpoint()
    {
        State.Return();
        // Read the confirmed pre-fight checkpoint from disk so transient world,
        // inventory, health and cutscene state are discarded as well.
        SaveLogic.Get().LoadSaveAsCurrent(slot);
        try { Load(); }
        catch
        {
            // Keep the write barrier if restoring fails. The Echo can retry the return.
            ClearForcePosition();
            ReplayUI.Notice("The memory will not fade. Speak to the Echo to try leaving again, or reload your save.");
            throw;
        }
    }

    private void ClearForcePosition()
    {
        if (!ownsForcePosition) return;
        MainCharacterSpawnLogic.Get().ClearForcePosition();
        ownsForcePosition = false;
    }

    private void Release()
    {
        ClearForcePosition();
        Current = null;
        graph.Dispose();
        Plugin.Logger.LogInfo("Rematch session closed; normal saving restored.");
    }
}
