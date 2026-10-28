using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MotherBase.EntityFramework;
using MotherBase.RoomDesigner;
using RedWine.ResourceManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace BossRematch;

// A native interactible entity with the game's existing spectral clue effect.
// No enemy, clue reward, AI, physics or save components are copied into it.
internal sealed class ReplayEcho : IDisposable
{
    private const string SpiritGuid = "1cfc1f54c390c8244a8fab7f0d73d948";
    private const string PlayerAnchorVariable = "b40d3151-b820-4019-a153-504781ed6d98";
    private GameObject? root;
    private Entity? entity;
    private Interactible? interaction;
    private UnityAction<Entity>? listener;
    private bool interactibleEnabled;
    internal readonly Encounter Target;
    internal readonly int RoomInstanceId;
    internal readonly bool IsReturn;
    internal bool Alive => entity && !entity!.destroyed && interaction;

    internal ReplayEcho(Encounter target, RoomInstance room, bool isReturn, Action<ReplayEcho> onTalk)
    {
        Target = target;
        RoomInstanceId = room.GetInstanceID();
        IsReturn = isReturn;
        try
        {
            var prefab = ResourceManager.LoadAssetByRef<GameObject>(ResourceContext.Room,
                new AssetReferenceT<GameObject>(SpiritGuid));
            if (!prefab) throw new InvalidOperationException("The game's spirit visual could not be loaded.");
            var player = MainCharacterSingleton.GetEntity();
            // The gameplay context has no entityTemplate in this build, so
            // InstantiateBlankEntity cannot be used. Create an inactive host;
            // assign the context before activation invokes Entity.OnEnable.
            root = new GameObject($"BossRematch_Echo_{target.Label}");
            root.SetActive(false);
            entity = root.AddComponent<Entity>();
            entity._context = player.context;
            entity.transform.SetParent(room.root, false);
            entity.transform.position = target.EchoPosition;
            root.SetActive(true);
            if (entity.context != player.context || entity.componentCache == null)
                throw new InvalidOperationException("The Echo entity did not initialize in the gameplay context.");
            Plugin.Logger.LogInfo($"Echo entity initialized: boss={target.Label}, context={entity.context.name}.");
            var roomComponent = entity.AddComponentData<CurrentRoomInstance>();
            roomComponent.currentRoom = room;
            roomComponent.dynamicUpdate = false;

            var visual = Object.Instantiate(prefab, entity.transform);
            visual.name = "EchoSpirit";
            visual.transform.localPosition = Vector3.zero;
            // This prefab contains particles/renderers only. Its silhouette and
            // wisps remain an ambient presence rather than a consumable pickup.
            foreach (var particles in visual.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (particles.name == "VFX_PS_ShadeSilhouette")
                {
                    main.loop = true;
                    main.maxParticles = 1;
                    main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.7f, 0.9f, 1f, 0.45f));
                }
                // Play each system once; recursively replaying each parent also
                // retriggers its child bursts and creates overlapping ghosts.
                particles.Play(false);
            }
            visual.SetActive(true);

            interaction = entity.AddComponentData<Interactible>();
            interaction.linkedEntity = entity;
            interaction.interactRadius = 1.4f;
            interaction.offset = new Vector3(0, 1f, 0);
            interaction.ignoreSightCheck = false;
            interaction.acceptedTags = InteractionTags.Player;
            interaction.interactOnlyOnce = false;
            interaction.blockInteractionDuringCombat = !isReturn;
            interaction.combinedConditions = new Il2CppReferenceArray<Interactible.CombinedConditionList>(0);
            interaction.focusingEntities = new Il2CppSystem.Collections.Generic.List<Entity>();
            // A null interactor action selects MC_Interact_Default through the
            // native InteractionActionTrigger, including controller bindings.
            interaction.interactorInteractionAction = null;
            interaction.selfInteractionAction = null;
            interaction.onInteractEvent = new UnityEvent<Entity>();
            listener = DelegateSupport.ConvertDelegate<UnityAction<Entity>>((Action<Entity>)(who =>
            {
                if (who == MainCharacterSingleton.GetEntity()) onTalk(this);
            }));
            interaction.onInteractEvent.AddListener(listener);
            player.context.afterForEach.EnableEntity(entity);
            interactibleEnabled = true;
            Plugin.Logger.LogInfo($"Echo created: boss={target.Label}, room={target.RoomId}, position={target.EchoPosition}, return={isReturn}.");
        }
        catch { Dispose(); throw; }
    }

    internal static bool TryFindPosition(CutsceneDirector director, out Vector3 position)
    {
        position = default;
        foreach (var node in director.ResolverData.Nodes)
        {
            var sub = node.TryCast<ResolverPlaySubResolverNodeData>();
            if (sub == null || !sub.SubResolverData || !sub.SubResolverData.name.StartsWith("BossFinalStraw", StringComparison.Ordinal)
                || sub.VariablesContainer == null || !sub.VariablesContainer.Contains(PlayerAnchorVariable)) continue;
            var anchorVariable = sub.VariablesContainer.GetVariable(PlayerAnchorVariable)?.TryCast<CutsceneAnchorVariable>();
            if (anchorVariable == null || string.IsNullOrEmpty(anchorVariable.Value)) continue;
            CutsceneAnchor? anchor = null;
            if (director._anchorBinder != null) director._anchorBinder.TryGetValue(anchorVariable.Value, out anchor);
            if (!anchor)
            {
                // Arrival runs before the room initializes the director binder.
                // Resolve the same authored ID directly from its scene registry.
                var room = director.GetComponentInParent<RoomInstance>();
                if (room)
                    foreach (var registry in room.GetComponentsInChildren<UniqueIDContext>(true))
                    {
                        var ids = registry.UniqueIDs;
                        if (ids?._items1 == null || ids._items2 == null) continue;
                        for (var i = 0; i < Math.Min(ids._items1.Length, ids._items2.Length); i++)
                            if (ids._items1[i] == anchorVariable.Value && ids._items2[i])
                                anchor = ids._items2[i].GetComponent<CutsceneAnchor>();
                    }
            }
            if (anchor == null || !anchor) continue;
            // Use the authored safe player standing position for the boss outro,
            // rather than an arbitrary room centre or the player's entry point.
            position = anchor.transform.position;
            return true;
        }
        return false;
    }

    internal void SetAvailable(bool available)
    {
        if (!Alive || available == interactibleEnabled) return;
        if (available) interaction!.EnableInteractible();
        else interaction!.DisableInteractible();
        interactibleEnabled = available;
    }

    public void Dispose()
    {
        if (interaction && listener != null) interaction!.onInteractEvent?.RemoveListener(listener);
        if (root) root!.SetActive(false);
        if (entity)
        {
            if (interaction) interaction!.DisableInteractible();
            if (MainCharacterSingleton.TryGetEntity(out var player) && player
                && player.TryGetComponentData<Interactor>(out var interactor) && interactor)
            {
                if (interactor.focusedInteractible == interaction)
                {
                    interactor.TryStopInteractibleFeedback(player);
                    interactor.focusedInteractible = null;
                }
                if (interactor.focusedInteractibleNoAccess == interaction) interactor.focusedInteractibleNoAccess = null;
                if (interactor.currentInteractionInteractible == interaction) interactor.currentInteractionInteractible = null;
            }
            entity!.Destroy();
        }
        else if (root) Object.Destroy(root);
        root = null;
        entity = null;
        interaction = null;
        listener = null;
    }
}
