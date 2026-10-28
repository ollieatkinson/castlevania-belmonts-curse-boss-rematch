using MotherBase.ApplicationFlowManager;
using MotherBase.RoomDesigner;
using RedWine.ResourceManagement;
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
    private bool returnPrepared;
    private bool closingReturnMenu;
    private readonly ArenaTravelGate returnGate = new();
    private bool graphPrepared;
    private bool started;
    private bool trialHallReady;
    private bool trialDoorEntered;
    private bool trialArrived;
    private bool trialCompleted;
    private readonly ArenaTravelGate trialGate = new();
    private BossFight? awaitingFight;
    private float startDeadline;
    internal CutsceneDirector? Director;

    private Session(Encounter target, Vector3 position, LevelID level, int slot)
    {
        Target = target;
        this.position = position;
        this.level = level;
        this.slot = slot;
        if (target.Trial is { IsBoss: false }) State.Virtues[target.CharacterGuid] = false;
        else State.Characters[target.CharacterGuid] = (int)CharacterProgressionStep.Found;
        if (target.Boss && target.Boss!.defeatedProgressionKey)
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
        var current = Encounters.Find(key: target.Key);
        if (current == null || !NotreDameTrials.Equipped(current)) return;
        if (current.Trial == null)
            using (var validation = new ReplayGraph()) validation.Build(current.Director!.ResolverData, current.Branch);
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
                var current = Encounters.Find(key: target.Key);
                if (current == null || !NotreDameTrials.Equipped(current)) return;
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
        if (active.Target.Trial != null && active.trialArrived
            && MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom?.worldRoomData.id != active.Target.FightRoomId
            && !MainCharacterSingleton.GetEntity().HasComponentData<EnteredZDoor>()) active.RequestReturn();
        if (active.returnRequested)
        {
            active.TickReturn();
        }
        else if (active.Target.Trial != null && !active.trialArrived)
        {
            active.TickTrialEntry();
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
        if (Target.Trial != null)
        {
            var hall = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
            if (!hall || hall!.worldRoomData.id != NotreDameTrials.Hall)
            { RequestReturn(); return; }
            trialHallReady = true;
            startDeadline = Time.realtimeSinceStartup + 45;
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
        if (!room || room.worldRoomData.id != Target.FightRoomId) return;
        if (graphPrepared) throw new InvalidOperationException("The replay director was initialized twice.");
        Director = director;
        var branch = Target.Branch;
        if (Target.Trial is { IsBoss: false })
        {
            if (Target.Trial.DoorId == 300) director.ResolverData = graph.BuildStrengthTrial(director.ResolverData);
            // The other courses only have an optional introductory line. Their
            // mechanics, cameras, restrictions and validation stay native.
        }
        else
        {
            var boss = Target.Boss;
            if (!boss)
            {
                var fights = room.GetComponentsInChildren<BossFight>(true);
                if (fights.Length != 1) throw new InvalidOperationException("The trial boss is ambiguous.");
                boss = ResourceManager.LoadAssetByRef<BossData>(ResourceContext.Room, fights[0]._bossDataRef);
            }
            if (!boss || !boss!.characterID || boss.characterID.guid != Target.CharacterGuid)
                throw new InvalidOperationException("The trial boss does not match the chosen memory.");
            branch = Encounters.FindBranch(director.ResolverData, boss.characterID);
            director.ResolverData = graph.Build(director.ResolverData, branch);
        }
        graphPrepared = true;
        Plugin.Logger.LogInfo($"Created private replay graph: {Target.ResolverName}, branch={branch}.");
    }

    internal bool AllowDirector(CutsceneDirector director)
    {
        if (!State.CanVirtualize) return true;
        var room = director.GetComponentInParent<RoomInstance>();
        if (!room) return true;
        if (Target.Trial != null && room.worldRoomData.id == NotreDameTrials.Hall) return false;
        return room.worldRoomData.id != Target.FightRoomId || (Director && director == Director);
    }

    internal bool Owns(BossFight fight)
    {
        var room = fight.GetComponentInParent<RoomInstance>();
        return State.CanVirtualize && room && room.worldRoomData.id == Target.FightRoomId;
    }


    internal bool PrepareSpawner(BaseMobsSpawner source)
    {
        if (!State.CanVirtualize) return false;
        var spawner = source.TryCast<MobsSpawner>();
        var room = source.GetComponentInParent<RoomInstance>();
        if (spawner == null || !spawner || room == null || !room || room.worldRoomData.id != Target.FightRoomId
            || spawner.mobsToSpawn == null || spawner.mobsToSpawn.Count == 0) return false;
        var boss = spawner.mobsToSpawn.Count == 1 ? spawner.mobsToSpawn[0].mobData?.TryCast<BossData>() : null;
        var matchingBoss = boss && boss!.characterID && boss.characterID.guid == Target.CharacterGuid;
        if ((!matchingBoss && Target.Trial == null) || !State.TryPrepareSpawner(spawner.GetInstanceID())) return false;
        if (matchingBoss && boss!.defeatedProgressionKey) State.Keys[boss.defeatedProgressionKey.guid] = false;
        // The native initializer restores only surviving mobs when HasSpawned
        // is set. A completed save therefore restores no boss. A full reset
        // selects SpawnInit for this single matching boss spawner instead.
        spawner.ignoreForcedFullReset = false;
        Plugin.Logger.LogInfo($"Resetting replay spawner: {Target.Label}, previouslySpawned={spawner.persistentData.HasSpawned}, initialized={spawner.initialized}, spawned={spawner.spawnedMobsCount}, initial={spawner.initialySpawnedMobsCount}, boss={matchingBoss}.");
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

    internal void RequestReturn()
    {
        if (returnRequested) return;
        returnRequested = true;
        returnPrepared = false;
        closingReturnMenu = false;
        returnGate.Reset();
    }

    private void TickTrialEntry()
    {
        if (Time.realtimeSinceStartup > startDeadline)
        {
            ReplayUI.Notice("The challenge did not open. Returning to the challenge hall.");
            RequestReturn();
            return;
        }
        var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
        if (!room) return;
        if (trialDoorEntered && room!.worldRoomData.id == Target.FightRoomId)
        {
            if (!graphPrepared || !trialGate.CanStart(IsGameplay(), Time.realtimeSinceStartup, Time.frameCount)) return;
            trialArrived = true;
            State.Arrived();
            started = Target.Trial!.DoorId != 300 && !Target.Trial.IsBoss;
            startDeadline = Time.realtimeSinceStartup + 45;
            Plugin.Logger.LogInfo($"Challenge loaded: {Target.Label}; room={Target.FightRoomId}.");
            return;
        }
        if (!trialHallReady || trialDoorEntered || room!.worldRoomData.id != NotreDameTrials.Hall
            || !trialGate.CanStart(IsGameplay(), Time.realtimeSinceStartup, Time.frameCount)) return;
        if (!NotreDameTrials.Equipped(Target with { Room = room })) { RequestReturn(); return; }
        var door = NotreDameTrials.Door(room, Target.Trial!);
        if (!door) { RequestReturn(); return; }
        trialDoorEntered = true;
        trialGate.Reset();
        door!.OnPlayerInteract(MainCharacterSingleton.GetEntity());
        Plugin.Logger.LogInfo($"Entering challenge through its authored portal: {Target.Label}, door={door.id}.");
    }

    internal void TrialCompleted()
    {
        if (trialCompleted) return;
        trialCompleted = true;
        // The bell grants its virtue at frame zero, then teleports the player
        // at frame 30. Let that native action remove its reaction affect and
        // finish the portal transition before the hall checkpoint is restored.
        // Tick observes departure from the course for both victory and exits.
        Plugin.Logger.LogInfo($"Challenge completed: {Target.Label}. Waiting for the native return to the hall.");
    }

    private void TickReturn()
    {
        var ui = UIManager.GetInstance();
        if (!ui || !ui.TryGetMenuManager(out var menu) || !menu)
        {
            returnGate.Reset();
            return;
        }
        if (menu.IsShowing())
        {
            // A timeout can occur while the player is looking at the map.
            // Unloading beneath that menu can pop the wrong game state and
            // leave the next camera at its prefab position.
            if (!closingReturnMenu)
            {
                closingReturnMenu = true;
                menu.Close();
            }
            returnGate.Reset();
            return;
        }
        closingReturnMenu = false;
        var player = MainCharacterSingleton.GetEntity();
        var ready = (!ui.confirmationPopup || !ui.confirmationPopup.visible)
            && !player.HasComponentData<EnteredZDoor>();
        if (!returnPrepared)
        {
            if (!returnGate.CanStart(ready, Time.realtimeSinceStartup, Time.frameCount)) return;
            // End our native cutscene players while their actors and cameras
            // still exist, before the level load pushes its own game state.
            if (Director && Director!._resolver != null) Director.ForceEnd();
            returnPrepared = true;
            returnGate.Reset();
            Plugin.Logger.LogInfo($"Replay return prepared: {Target.Label}; waiting for settled gameplay.");
            return;
        }
        if (!returnGate.CanStart(ready && IsGameplay(), Time.realtimeSinceStartup, Time.frameCount)) return;
        returnRequested = false;
        returnGate.Reset();
        ReturnToCheckpoint();
    }

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
