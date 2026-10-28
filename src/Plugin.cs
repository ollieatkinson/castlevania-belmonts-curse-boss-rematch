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
        using var file = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
        using var sha256 = SHA256.Create();
        var hash = Convert.ToHexString(sha256.ComputeHash(file));
        Log.LogInfo($"Boss Rematch {BuildInfo.Version}; GameAssembly SHA-256: {hash}.");
        if (!hash.Equals(BuildCompatibility.GameAssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.LogError("This game build is not supported. No hooks installed.");
            return;
        }
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
            Session.Current?.RequestReturn();
        }
    }
}

[HarmonyPatch(typeof(CutsceneDirector), nameof(CutsceneDirector.Resolve))]
internal static class PauseOtherRoomScripts
{
    private static bool Prefix(CutsceneDirector __instance) => Session.Current?.AllowDirector(__instance) ?? true;
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
