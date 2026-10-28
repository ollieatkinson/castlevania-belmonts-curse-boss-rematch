using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Security.Cryptography;

namespace BossRematch;

[BepInPlugin(Id, "Boss Rematch", BuildInfo.Version)]
public sealed class Plugin : BasePlugin
{
    public const string Id = "ollieatkinson.bossrematch";
    internal static ManualLogSource Logger = null!;
    internal static bool Ready;
    internal static ConfigEntry<bool> ShowBossIcons = null!;
    internal static ConfigEntry<bool> ArenaFastTravel = null!;

    public override void Load()
    {
        Logger = Log;
        ShowBossIcons = Config.Bind("Map", "ShowBossIcons", true, "Show selectable boss portraits in visited arenas after defeating the boss. Restart after changing.");
        ArenaFastTravel = Config.Bind("Map", "FastTravelToArenas", true, "Allow travel to defeated boss arenas using the map and normal interact button. Arriving does not start a rematch.");
        LogBuildDiagnostics();
        var harmony = new Harmony(Id);
        try
        {
            // Saving and progression barriers are mandatory. If any hook fails,
            // remove all hooks before exposing the challenge control.
            harmony.PatchAll(typeof(Plugin).Assembly);
            AddComponent<ReplayUI>();
            Ready = true;
            Log.LogInfo("Boss Rematch loaded. Speak to the Echo in a cleared boss arena using the normal interact button.");
        }
        catch (Exception e)
        {
            harmony.UnpatchSelf();
            Log.LogError($"Boss Rematch could not install its required hooks; disabled. {e}");
        }
    }

    private void LogBuildDiagnostics()
    {
        try
        {
            Log.LogInfo($"Boss Rematch {BuildInfo.Version}; game version: {UnityEngine.Application.version}.");
            using var file = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            using var sha256 = SHA256.Create();
            var hash = Convert.ToHexString(sha256.ComputeHash(file));
            Log.LogInfo($"GameAssembly SHA-256: {hash}.");
            if (!hash.Equals(BuildCompatibility.ReferenceGameAssemblySha256, StringComparison.OrdinalIgnoreCase))
                Log.LogWarning("GameAssembly differs from the last verified build. Continuing with required hook checks and encounter-layout validation; include this log when reporting compatibility problems.");
        }
        catch (Exception e) { Log.LogWarning($"Could not read build diagnostics; continuing with required hook checks: {e.Message}"); }
    }
}

[HarmonyPatch(typeof(CutsceneDirector), nameof(CutsceneDirector.CreateResolver))]
internal static class PrepareReplayDirector
{
    private static void Prefix(CutsceneDirector __instance)
    {
        try { Session.Current?.PrepareDirector(__instance); }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Could not prepare replay graph: {e}");
            Session.Current?.FailEncounter(e);
        }
    }
}

[HarmonyPatch(typeof(CutsceneResolver), nameof(CutsceneResolver.Resolve),
    new[] { typeof(float), typeof(CutsceneDirector), typeof(bool) },
    new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
internal static class PauseOtherRoomScripts
{
    private static bool Prefix(CutsceneDirector __1, ref bool __2)
    {
        // UpdateDirectors inlines CutsceneDirector.Resolve and calls the
        // resolver directly. Hook that actual call, leaving a paused graph
        // unfinished so it can resume after loading and actor initialization.
        if (Session.Current?.AllowDirector(__1) != false) return true;
        __2 = false;
        return false;
    }
}

[HarmonyPatch(typeof(CutsceneResolver), nameof(CutsceneResolver.PreStart), new[] { typeof(CutsceneDirector) })]
internal static class WaitForReplayIntroActors
{
    private static bool Prefix(CutsceneResolver __instance, CutsceneDirector __0)
        => !(Session.Current?.DeferIntro(__instance, __0) ?? false);
}

[HarmonyPatch(typeof(MainCharacterSpawnLogic), nameof(MainCharacterSpawnLogic.Initialize))]
internal static class ReplaySpawn
{
    private static void Prefix(MainCharacterSpawnLogic __instance)
    {
        Session.Current?.OnInitialize(__instance);
        ArenaTravel.PrepareArrival(__instance);
    }
}

[HarmonyPatch(typeof(MainCharacterSpawnLogic), nameof(MainCharacterSpawnLogic.OnAFMEndLoadState))]
internal static class ReplayArrival
{
    private static void Postfix()
    {
        Session.Current?.OnArrived();
        ArenaTravel.CheckArrival();
    }
}

[HarmonyPatch(typeof(BossFight), nameof(BossFight.StartBossFight))]
internal static class FightStarted
{
    private static void Postfix(BossFight __instance) => Session.Current?.OnFightStarted(__instance);
}

[HarmonyPatch(typeof(BossFight), nameof(BossFight.ClearBossFight))]
internal static class FightCleared
{
    private static void Postfix(BossFight __instance) => Session.Current?.OnFightCleared(__instance);
}

[HarmonyPatch(typeof(PlaceholderMCDeathFeature), nameof(PlaceholderMCDeathFeature.GameplayStateUpdate))]
internal static class NoReplayBossRetry
{
    private static void Prefix(MotherBase.EntityFramework.Entity __0) => Session.Current?.OnPlayerDeath(__0);
}

[HarmonyPatch(typeof(BossFight), nameof(BossFight.OnGiveLoot))]
internal static class NoReplayLoot
{
    private static bool Prefix(BossFight __instance) => !(Session.Current?.Owns(__instance) ?? false);
}

[HarmonyPatch(typeof(ResolverGiveBossLootNode), nameof(ResolverGiveBossLootNode.Begin))]
internal static class NoReplayRewardNode
{
    private static bool Prefix(CutsceneDirector __0)
    {
        var session = Session.Current;
        return session == null || !session.State.CanVirtualize || session.Director != __0;
    }
}

[HarmonyPatch(typeof(BaseMobsSpawner), nameof(BaseMobsSpawner.InitializeSpawner))]
internal static class ResetReplayBossSpawner
{
    private static void Prefix(BaseMobsSpawner __instance, ref bool __2, out bool __state)
    {
        __state = Session.Current?.PrepareSpawner(__instance) == true;
        if (__state) __2 = true;
    }

    private static void Postfix(BaseMobsSpawner __instance, bool __state)
    {
        if (__state) Session.Current?.OnSpawnerInitialized(__instance);
    }
}
