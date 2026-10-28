using HarmonyLib;
using MotherBase.RoomDesigner;
using RedWine.ResourceManagement;
using UnityEngine;

namespace BossRematch;

internal sealed record ArenaDestination(string RoomId, string Label, string CharacterGuid, string LevelGuid, Vector3 RoomCenter);

internal static class BossMap
{
    internal static bool CanShow(LevelMapCharacterItem item)
    {
        if (!Plugin.Ready || !Plugin.ShowBossIcons.Value || !item || !item.linkedCharacter
            || Session.Current != null
            || !MainCharacterSingleton.TryGetEntity(out var player) || !player) return false;
        if (item.itemWorldRoomId == NotreDameTrials.Hall)
            return item.linkedCharacter.guid == NotreDameTrials.CursedRose && NotreDameTrials.CanTravel;
        // The hall's existing portrait is the one shared destination for the
        // four trials and Cursed Rose, outside their isolated challenge rooms.
        if (!Encounters.Supported.ContainsKey(item.itemWorldRoomId)
            || item.itemWorldRoomId == NotreDameTrials.Supported[^1].RoomId) return false;
        var progression = MainCharacterSingleton.GetComponent<CharacterProgressionComponent>();
        return progression && progression.GetProgressionStep(item.linkedCharacter) == CharacterProgressionStep.Absorbed
            && WasVisited(item.itemWorldRoomId);
    }

    internal static bool WasVisited(string roomId)
    {
        var explorer = MainCharacterSingleton.GetComponent<ExplorerComponent>();
        return explorer && explorer.worldRoomExplorationStates.TryGetValue(roomId, out var state) && state != null
            && (state.ExploredAtLeastOneChunk() || state.usedRoomSavePoint || state.enteredZonesIds?.Count > 0);
    }

    internal static ArenaDestination? Find(LevelMapItem? selected)
    {
        var item = selected?.TryCast<LevelMapCharacterItem>();
        if (item == null || !Plugin.ArenaFastTravel.Value || !CanShow(item) || ArenaTravel.Busy || Session.Pending != null) return null;
        var level = item.context?.level;
        var manager = LevelManager.Get();
        if (!level || !manager || !manager.worldDataDictionary.TryGetItem(level, out var reference)) return null;
        var world = ResourceManager.LoadAssetByRef<WorldData>(ResourceContext.UI_GAMEMENU, reference);
        if (!world) return null;
        foreach (var room in world.GetRooms())
            if (room.id == item.itemWorldRoomId)
                return new(room.id, room.id == NotreDameTrials.Hall ? "Notre-Dame challenges" : Encounters.Supported[room.id].Label,
                    item.linkedCharacter!.guid, level!.guid,
                    RoomDesignerExtensions.GetRoomCenterPosition(room));
        return null;
    }

    internal static bool CanTravel(ArenaDestination destination)
    {
        if (!Plugin.ArenaFastTravel.Value || !Plugin.ShowBossIcons.Value || Session.Current != null || Session.Pending != null
            || !WasVisited(destination.RoomId)) return false;
        if (destination.RoomId == NotreDameTrials.Hall)
            return destination.CharacterGuid == NotreDameTrials.CursedRose && NotreDameTrials.CanTravel;
        if (!Encounters.Supported.ContainsKey(destination.RoomId)) return false;
        var progression = MainCharacterSingleton.GetComponent<CharacterProgressionComponent>();
        if (!progression) return false;
        foreach (var pair in progression.characterProgressionSteps)
            if (pair.Key && pair.Key.guid == destination.CharacterGuid)
                return pair.Value == CharacterProgressionStep.Absorbed;
        return false;
    }
}

[HarmonyPatch(typeof(LevelMapCharacterItem), nameof(LevelMapCharacterItem.Refresh))]
internal static class ShowDefeatedBossMapIcons
{
    private static void Postfix(LevelMapCharacterItem __instance)
    {
        try
        {
            if (!BossMap.CanShow(__instance) || !__instance.display) return;
            // Refresh inlines its visibility calculation, so update the private
            // display state rather than patching the unused ProcessVisibility.
            var state = __instance._currentCharacterItemState;
            state.isCharacterVisible = true;
            state.characterProgressionStep = CharacterProgressionStep.Found;
            __instance._currentCharacterItemState = state;
            __instance.selectableItem = true;
            // The generated SetDisplayData<T> wrapper passes this IL2CPP value
            // type's boxed object pointer without unboxing it. Native code then
            // reads the object header as character data and can crash the game.
            // The field setter handles unboxing; RefreshDisplay takes no data.
            __instance.display.currentDisplayData = state;
            __instance.display.RefreshDisplay();
        }
        catch (Exception e) { Plugin.Logger.LogError($"Could not show boss map icon: {e}"); }
    }
}

// GetVisibility and IsSelectableInternal share one native function. Hook once.
[HarmonyPatch(typeof(LevelMapCharacterItem), nameof(LevelMapCharacterItem.GetVisibility))]
internal static class SelectDefeatedBossMapIcons
{
    private static void Postfix(LevelMapCharacterItem __instance, ref bool __result)
    {
        if (BossMap.CanShow(__instance)) __result = true;
    }
}

[HarmonyPatch(typeof(LevelMapController), nameof(LevelMapController.UpdateCursorControls))]
internal static class TravelToBossMapIcon
{
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("ollieatkinson.mirrorfasttravel")]
    private static bool Prefix(LevelMapController __instance)
    {
        try
        {
            if (__instance._importantInputProcessedForFrame || !__instance.AreControlsEnabled()
                || !__instance.interactInput || !__instance.interactInput.action.WasPerformedThisFrame()) return true;
            var destination = BossMap.Find(__instance.cursor.GetCurrentSelection());
            if (destination == null || ReplayUI.Instance == null || !ReplayUI.Instance.ConfirmArenaTravel(destination)) return true;
            __instance._importantInputProcessedForFrame = true;
            return false;
        }
        catch (Exception e) { Plugin.Logger.LogError($"Could not select boss arena travel: {e}"); return true; }
    }
}

[HarmonyPatch(typeof(UIMapLevelManager), nameof(UIMapLevelManager.RefreshInteractFootnoteGroup))]
internal static class BossMapTravelPrompt
{
    private static UILocalizedString? label;
    private static bool wasEnabled;
    private static string? previous;

    internal static void Restore()
    {
        if (label)
        {
            label!.enabled = wasEnabled;
            if (label.textDisplay && previous != null) label.textDisplay.text = previous;
        }
        label = null;
    }

    [HarmonyPriority(Priority.First)]
    private static void Prefix() => Restore();

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UIMapLevelManager __instance, LevelMapItem __0, bool __1)
    {
        if (BossMap.Find(__0) == null) return;
        var text = __instance.interactFootnoteText;
        if (!text || !text.textDisplay) return;
        label = text;
        wasEnabled = text.enabled;
        previous = text.textDisplay.text;
        text.enabled = false;
        text.textDisplay.text = __0?.itemWorldRoomId == NotreDameTrials.Hall ? "Travel to challenge hall" : "Travel to arena";
        var view = __1 ? __instance.hasInteractFootnoteInitBooleanView : __instance.hasInteractFootnoteBooleanView;
        if (view) view.SetBool(true);
    }
}

[HarmonyPatch(typeof(UIMapLevelManager), nameof(UIMapLevelManager.OnDisable))]
internal static class RestoreBossMapPrompt
{
    private static void Prefix() => BossMapTravelPrompt.Restore();
}
