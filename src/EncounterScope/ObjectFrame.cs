using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using EncounterScope.Core;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;

namespace EncounterScope;

internal sealed unsafe class ObjectFrame
{
    private readonly Dictionary<ulong, IGameObject> byGameObjectId = [];
    private readonly Dictionary<uint, IGameObject> byEntityId = [];
    private readonly List<VisibleCastSnapshot> casts = [];

    public IReadOnlyList<VisibleCastSnapshot> Casts => casts;

    public void Refresh(IObjectTable objectTable)
    {
        byGameObjectId.Clear();
        byEntityId.Clear();
        casts.Clear();

        foreach (var gameObject in objectTable)
        {
            if (gameObject is null)
                continue;

            byGameObjectId[gameObject.GameObjectId] = gameObject;
            if (gameObject.EntityId != 0)
                byEntityId[gameObject.EntityId] = gameObject;

            if (gameObject is not IBattleChara battleChara)
                continue;

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
}
