using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace BossRematch;

public sealed class ReplayUI : MonoBehaviour
{
    internal static ReplayUI? Instance;
    private ReplayEcho? echo;
    private ReplayEcho? talkRequest;
    private float nextScan;
    private bool confirmationOpen;
    private Encounter? confirmed;
    private Session? confirmedReturn;
    private ConfirmationPopup? popup;
    private readonly List<(UILocalizedString Label, bool Enabled, string Text, string Replacement)> labels = new();
    private static string? pendingNotice;

    public ReplayUI(IntPtr pointer) : base(pointer) { }

    [HideFromIl2Cpp]
    internal static void Notice(string text)
    {
        pendingNotice = text;
        Plugin.Logger.LogWarning(text);
    }

    public void Awake() => Instance = this;

    public void Update()
    {
        try
        {
            RefreshPopup();
            if (ApplicationLoading())
            {
                DestroyEcho();
                return;
            }
            if (!confirmationOpen && Session.IsGameplay())
            {
                if (confirmed != null)
                {
                    var target = confirmed;
                    confirmed = null;
                    RestorePopup();
                    Session.Request(target);
                }
                if (confirmedReturn != null)
                {
                    if (ReferenceEquals(confirmedReturn, Session.Current)) confirmedReturn.RequestReturn();
                    confirmedReturn = null;
                    RestorePopup();
                }
            }
            Session.Tick();
            ArenaTravel.Tick();
            if (ApplicationLoading()) { DestroyEcho(); return; }
            if (!MainCharacterSingleton.TryGetEntity(out var player) || !player) { DestroyEcho(); return; }
            var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
            if (echo != null && (!room || !echo.Alive || room!.GetInstanceID() != echo.RoomInstanceId)) DestroyEcho();
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 0.5f;
                RefreshEcho();
            }
            echo?.SetAvailable(!ArenaTravel.Busy && Session.Pending == null && !confirmationOpen && confirmed == null && confirmedReturn == null && Session.IsGameplay());
            if (!confirmationOpen && Session.IsGameplay())
            {
                var talk = talkRequest;
                talkRequest = null;
                if (talk != null && ReferenceEquals(talk, echo) && talk.Alive) Talk(talk);
                else if (pendingNotice != null)
                {
                    var text = pendingNotice;
                    pendingNotice = null;
                    OpenPopup("Echo", text, "Continue", () => { });
                }
            }
        }
        catch (Exception e)
        {
            nextScan = Time.realtimeSinceStartup + 5;
            DestroyEcho();
            Plugin.Logger.LogError($"Boss Rematch: {e}");
        }
    }

    [HideFromIl2Cpp]
    private static bool ApplicationLoading() => MotherBase.ApplicationFlowManager.ApplicationFlowManager.IsLoading;

    [HideFromIl2Cpp]
    private void RefreshEcho()
    {
        var active = Session.Current;
        var room = MainCharacterSingleton.GetComponent<CurrentRoomInstance>()?.currentRoom;
        // Keep the arena clear throughout the intro and fight. Only a failed
        // return may need an Echo while a session still exists, to retry leaving.
        if (!room || (active != null && active.State.Phase != ReplayState.Stage.Returning)) { DestroyEcho(); return; }
        var target = active?.Target ?? Encounters.Find(requireGameplay: false);
        if (target == null || room!.worldRoomData.id != target.RoomId) { DestroyEcho(); return; }
        var isReturn = active != null;
        if (echo != null && echo.Alive && echo.RoomInstanceId == room.GetInstanceID() && echo.IsReturn == isReturn) return;
        DestroyEcho();
        echo = new ReplayEcho(target, room, isReturn, QueueEchoTalk);
    }

    [HideFromIl2Cpp]
    private void QueueEchoTalk(ReplayEcho request)
    {
        // Keep managed-only parameters out of the injected IL2CPP type. Open
        // UI after the native interaction callback returns to the game.
        if (!confirmationOpen && Session.Pending == null && Session.IsGameplay()) talkRequest = request;
    }

    [HideFromIl2Cpp]
    internal bool ConfirmArenaTravel(ArenaDestination destination)
    {
        var ui = UIManager.GetInstance();
        if (confirmationOpen || !ui || !ui.TryGetMenuManager(out var menu) || !menu) return false;
        OpenPopup($"{destination.Label} — Arena", "Travel to the Echo in this arena?", "Travel", () =>
        {
            if (ArenaTravel.Queue(destination)) menu.Close();
        });
        return confirmationOpen;
    }

    [HideFromIl2Cpp]
    private void Talk(ReplayEcho speaker)
    {
        if (speaker.IsReturn)
        {
            var active = Session.Current;
            if (active == null) return;
            OpenPopup($"Echo of {speaker.Target.Label}", "Shall we let this memory fade?", "Leave the memory",
                () => confirmedReturn = active);
        }
        else
        {
            var target = Encounters.Find();
            if (target == null || target.RoomId != speaker.Target.RoomId) return;
            OpenPopup($"Echo of {target.Label}", "This place remembers our battle.\nWill you face me once more?", "Relive the battle",
                () => confirmed = target);
        }
    }

    [HideFromIl2Cpp]
    private void OpenPopup(string title, string message, string accept, Action onConfirm)
    {
        var ui = UIManager.GetInstance();
        if (confirmationOpen || !ui || !ui.confirmationPopup || ui.confirmationPopup.visible || !ui.fastTravelMenuManager) return;
        RestorePopup();
        confirmationOpen = true;
        try
        {
            // Keep native focus, confirm/back actions and dynamic button glyphs.
            ui.OpenConfirmPopup(ui.fastTravelMenuManager.confirmationPopupTexts,
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() => confirmationOpen = false)),
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)(() =>
                {
                    confirmationOpen = false;
                    onConfirm();
                })));
            popup = ui.confirmationPopup;
            Replace(popup.titleLabel, title);
            Replace(popup.messageLabel, message);
            Replace(popup.confirmLabel, accept);
            Replace(popup.cancelLabel, "Not now");
            RefreshPopup();
        }
        catch
        {
            confirmationOpen = false;
            RestorePopup();
            throw;
        }
    }

    [HideFromIl2Cpp]
    private void Replace(UILocalizedString field, string text)
    {
        if (!field || !field.textDisplay) return;
        labels.Add((field, field.enabled, field.textDisplay.text, text));
        field.enabled = false;
    }

    [HideFromIl2Cpp]
    private void RefreshPopup()
    {
        if (popup == null) return;
        if (!popup)
        {
            confirmationOpen = false;
            RestorePopup();
            return;
        }
        if (!popup!.visible && !confirmationOpen) { RestorePopup(); return; }
        foreach (var saved in labels)
            if (saved.Label && saved.Label.textDisplay) saved.Label.textDisplay.text = saved.Replacement;
    }

    [HideFromIl2Cpp]
    private void RestorePopup()
    {
        foreach (var saved in labels)
        {
            if (!saved.Label) continue;
            saved.Label.enabled = saved.Enabled;
            if (saved.Label.textDisplay) saved.Label.textDisplay.text = saved.Text;
        }
        labels.Clear();
        popup = null;
    }

    [HideFromIl2Cpp]
    private void DestroyEcho()
    {
        talkRequest = null;
        echo?.Dispose();
        echo = null;
    }

    public void OnDestroy()
    {
        DestroyEcho();
        RestorePopup();
        Instance = null;
    }
}
