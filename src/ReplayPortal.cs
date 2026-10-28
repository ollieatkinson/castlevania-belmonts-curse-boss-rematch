using MotherBase.ActionFramework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BossRematch;

internal sealed class ReplayPortal : IDisposable
{
    private readonly List<Object> copies = new();
    private ZDoorEntrance? door;
    private ActionTrigger? original;
    private CutsceneUIManager? coverUI;

    internal void Prepare(ZDoorEntrance entrance)
    {
        var action = entrance.enterZDoorAction?.TryCast<ActionData>();
        if (!action || action!.name != "MC_VirtuousEntrance_EnterZDoor_Final_NoAnim")
            throw new InvalidOperationException("The Cursed Rose portal action has changed.");
        var privateAction = Copy(action);
        if (privateAction.actionFeatures.Pointer == action.actionFeatures.Pointer)
            throw new InvalidOperationException("Portal action features are shared with the original.");
        var transitions = 0;
        for (var i = 0; i < action.actionFeatures.Count; i++)
        {
            var launcher = action.actionFeatures[i].TryCast<FeedbackLauncherFeature>();
            var name = launcher?.feedbackAsset?.name;
            if (name != "MC_VirtuousEntrance_TransitionZdoor_Common_Feedback"
                && name != "MC_VirtuousEntrance_EnterZDoor_Final_Feedback") continue;
            var transition = name == "MC_VirtuousEntrance_TransitionZdoor_Common_Feedback" ? 1 : 2;
            if ((transitions & transition) != 0)
                throw new InvalidOperationException("The portal transition feedback is ambiguous.");
            var privateLauncher = Copy(launcher!);
            var feedback = launcher!.feedbackAsset;
            var privateFeedback = Copy(feedback);
            if (privateFeedback.subAssets.Pointer == feedback.subAssets.Pointer)
                throw new InvalidOperationException("Portal feedback features are shared with the original.");
            var effects = 0;
            for (var j = 0; j < feedback.subAssets.Count; j++)
            {
                if (name == "MC_VirtuousEntrance_TransitionZdoor_Common_Feedback")
                {
                    var exposure = feedback.subAssets[j].TryCast<PostExposureAdjustmentFeedbackFeature>();
                    if (exposure == null) continue;
                    var privateExposure = Copy(exposure);
                    privateExposure.postExposureMaxValue = 0;
                    privateFeedback.subAssets[j] = privateExposure;
                }
                else
                {
                    var glass = feedback.subAssets[j].TryCast<DeathBreakScreenEffectFeedbackFeature>();
                    if (glass == null) continue;
                    var privateGlass = Copy(glass);
                    privateGlass.SetActive(false);
                    privateFeedback.subAssets[j] = privateGlass;
                }
                effects++;
            }
            if (effects != 1) throw new InvalidOperationException("The portal transition effect has changed.");
            privateLauncher.feedbackAsset = privateFeedback;
            privateAction.actionFeatures[i] = privateLauncher;
            transitions |= transition;
        }
        if (transitions != 3) throw new InvalidOperationException("The portal transition feedback has changed.");
        // Both the glass shader and a separate exposure pulse can flash white.
        // Keep the native timing, controls, HUD fade, audio and teleport; the
        // boss intro supplies its own black fade after the portal has settled.
        door = entrance;
        original = entrance.enterZDoorAction;
        entrance.enterZDoorAction = privateAction;
        coverUI = CutsceneUIManager.Instance;
        if (!coverUI) throw new InvalidOperationException("The portal transition fade is unavailable.");
        // Removing the glass animation exposes the room/camera swap. Cover it
        // without taking another game state; the repeat intro's own black
        // fade-out reveals the arena after placement and camera setup finish.
        coverUI.Fade(FadeType.FadeIn, Color.black, AnimationCurve.Linear(0, 0, 1, 1), 0.15f);
        Plugin.Logger.LogInfo("Prepared Cursed Rose portal without its stained-glass shader or exposure pulse.");
    }

    internal void ReleaseCover()
    {
        if (coverUI && CutsceneUIManager.Instance == coverUI)
            coverUI!.ForceEndFade(FadeType.FadeOut);
        coverUI = null;
    }

    private T Copy<T>(T source) where T : Object
    {
        var copy = Object.Instantiate(source);
        if (copy == source) throw new InvalidOperationException("Could not isolate a portal asset.");
        copies.Add(copy);
        return copy;
    }

    public void Dispose()
    {
        ReleaseCover();
        if (door) door!.enterZDoorAction = original;
        foreach (var copy in copies) if (copy) Object.Destroy(copy);
        copies.Clear();
    }
}
