using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using EncounterScope.Core;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace EncounterScope;

internal sealed unsafe class ObjectFrame
{
    private readonly Dictionary<ulong, IGameObject> byGameObjectId = [];
    private readonly Dictionary<uint, IGameObject> byEntityId = [];
    private readonly List<VisibleCastSnapshot> casts = [];
    private readonly HashSet<ulong> presentBattleActorIds = [];
    private readonly List<IBattleChara> battleActors = [];

    public IReadOnlyList<VisibleCastSnapshot> Casts => casts;
    public IReadOnlySet<ulong> PresentBattleActorIds => presentBattleActorIds;

    public void Refresh(IObjectTable objectTable)
    {
        byGameObjectId.Clear();
        byEntityId.Clear();
        casts.Clear();
        presentBattleActorIds.Clear();
        battleActors.Clear();

        foreach (var gameObject in objectTable)
        {
            if (gameObject is null)
                continue;

            byGameObjectId[gameObject.GameObjectId] = gameObject;
            if (gameObject.EntityId != 0)
                byEntityId[gameObject.EntityId] = gameObject;

            if (gameObject is IBattleChara battleChara)
                battleActors.Add(battleChara);
        }

        foreach (var battleChara in battleActors)
        {
            if (!IsEncounterActor(battleChara))
                continue;

            presentBattleActorIds.Add(battleChara.GameObjectId);

            var nativeBattleChara = (NativeBattleChara*)battleChara.Address;
            if (nativeBattleChara == null)
                continue;

            // GetCastInfo can legitimately be null while an actor is in certain scripted states.
            // Dalamud's IBattleChara cast convenience properties dereference it unconditionally.
            var castInfo = nativeBattleChara->GetCastInfo();
            if (castInfo == null)
                continue;

            casts.Add(new(
                battleChara.GameObjectId,
                castInfo->IsCasting,
                (byte)castInfo->ActionType,
                castInfo->ActionId,
                castInfo->TargetId,
                castInfo->CurrentCastTime,
                castInfo->BaseCastTime,
                castInfo->TotalCastTime,
                castInfo->Interruptible));
        }
    }

    public IGameObject? FindByGameObjectId(ulong id) => byGameObjectId.GetValueOrDefault(id);
    public IGameObject? FindByEntityId(uint id) => byEntityId.GetValueOrDefault(id);

    public bool IsEncounterActor(IGameObject gameObject)
    {
        if (gameObject.ObjectKind != ObjectKind.BattleNpc)
            return false;

        var owner = FindByEntityId(gameObject.OwnerId);
        return owner?.ObjectKind != ObjectKind.Pc;
    }
}
