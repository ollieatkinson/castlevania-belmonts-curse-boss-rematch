using HarmonyLib;
using System.Reflection;

namespace BossRematch;

[HarmonyPatch]
internal static class ReadSavedProgression
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.ReadPlayerSave));
        yield return AccessTools.Method(typeof(ProgressionComponent), nameof(ProgressionComponent.ReadPlayerSave));
    }

    private static void Prefix(out ReplayState? __state)
    {
        __state = Session.Current?.State;
        __state?.BeginSaveRead();
    }

    // A finalizer restores the scope even if native save loading throws.
    // It leaves the original exception and the session's save barrier intact.
    private static void Finalizer(ReplayState? __state) => __state?.EndSaveRead();
}

[HarmonyPatch(typeof(ProgressionComponent), nameof(ProgressionComponent.HasProgressionKey))]
internal static class ReadReplayKeys
{
    private static bool Prefix(ProgressionKeyID __0, ref bool __result)
    {
        if (__0 && Session.Current?.State.TryReadKey(__0.guid, out var value) == true)
        { __result = value; return false; }
        return true;
    }
}

[HarmonyPatch(typeof(ProgressionComponent), nameof(ProgressionComponent.SetProgressionKey))]
internal static class SetReplayKeys
{
    private static bool Prefix(ProgressionComponent __instance, ProgressionKeyID __0, ref bool __result)
    {
        var state = Session.Current?.State;
        if (state?.CanVirtualize != true || !__0) return true;
        __result = state.WriteKey(__0.guid, true, __instance.progressionKeys.Contains(__0));
        return false;
    }
}

[HarmonyPatch(typeof(ProgressionComponent), nameof(ProgressionComponent.UnsetProgressionKey))]
internal static class UnsetReplayKeys
{
    private static bool Prefix(ProgressionComponent __instance, ProgressionKeyID __0, ref bool __result)
    {
        var state = Session.Current?.State;
        if (state?.CanVirtualize != true || !__0) return true;
        __result = state.WriteKey(__0.guid, false, __instance.progressionKeys.Contains(__0));
        return false;
    }
}

[HarmonyPatch(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.GetProgressionStep))]
internal static class ReadReplayCharacters
{
    private static bool Prefix(CharacterID __0, ref CharacterProgressionStep __result)
    {
        var state = Session.Current?.State;
        if (state?.CanVirtualize == true && __0 && state.Characters.TryGetValue(__0.guid, out var value))
        { __result = (CharacterProgressionStep)value; return false; }
        return true;
    }
}

[HarmonyPatch(typeof(CharacterProgressionComponent), nameof(CharacterProgressionComponent.SetProgressionStep))]
internal static class WriteReplayCharacters
{
    private static bool Prefix(CharacterProgressionComponent __instance, CharacterID __0, CharacterProgressionStep __1, bool __2)
    {
        var state = Session.Current?.State;
        if (state?.CanVirtualize != true || !__0) return true;
        var current = (int)__instance.GetProgressionStep(__0);
        state.Characters[__0.guid] = __2 ? (int)__1 : Math.Max(current, (int)__1);
        return false;
    }
}

[HarmonyPatch]
internal static class NoReplaySaves
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "RunWriteSaveRoutine", "TryWriteCurrentSave", "SaveRoom" })
            yield return AccessTools.Method(typeof(SaveLogic), name)
                ?? throw new MissingMethodException(typeof(SaveLogic).FullName, name);
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(MobsSpawner)).Where(m => m.Name == "SaveState"))
            yield return method;
    }
    private static bool Prefix() => Session.Current == null;
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.TrySave))]
internal static class SaveCheckpoint
{
    private static bool Prefix(ref bool __result)
    {
        if (Session.Current == null) return true;
        // A late queued save must also pass the barrier, even if it was created
        // before the rematch. Report handled to the game's save coroutine.
        __result = true;
        return false;
    }
    private static void Postfix(string __0, bool __result)
    {
        Session.OnSaved(__0, __result);
        ArenaTravel.OnSaved(__0, __result);
    }
}
