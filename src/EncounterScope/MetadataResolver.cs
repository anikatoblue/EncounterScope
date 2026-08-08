using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using EncounterScope.Core;
using GameActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;

namespace EncounterScope;

internal sealed class MetadataResolver
{
    private readonly ExcelSheet<Lumina.Excel.Sheets.Action> actions;
    private readonly ExcelSheet<BNpcName> battleNpcNames;

    public MetadataResolver(IDataManager dataManager)
    {
        actions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English);
        battleNpcNames = dataManager.GetExcelSheet<BNpcName>(ClientLanguage.English);
    }

    public ActionReference ResolveAction(byte typeId, uint actionId)
    {
        var type = (GameActionType)typeId;
        string? name = null;
        if (type == GameActionType.Action && actions.TryGetRow(actionId, out var action))
        {
            var candidate = action.Name.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
                name = candidate;
        }

        return new(typeId, type.ToString(), actionId, name);
    }

    public ActorReference ResolveActor(
        IGameObject? gameObject,
        ulong fallbackGameObjectId,
        uint? fallbackEntityId = null)
    {
        if (gameObject is null)
            return fallbackEntityId is { } entityId
                ? ActorReference.Unknown(fallbackGameObjectId, entityId)
                : ActorReference.Unknown(fallbackGameObjectId);

        uint? classJobId = gameObject is ICharacter character ? character.ClassJob.RowId : null;
        string? npcName = null;
        if (gameObject.ObjectKind == ObjectKind.BattleNpc && gameObject is ICharacter battleNpc && battleNpc.NameId != 0 &&
            battleNpcNames.TryGetRow(battleNpc.NameId, out var nameRow))
        {
            var candidate = nameRow.Singular.ToString();
            if (!string.IsNullOrWhiteSpace(candidate))
                npcName = candidate;
        }

        var position = gameObject.Position;
        return new(
            IdFormatting.GameObjectId(gameObject.GameObjectId),
            IdFormatting.EntityId(gameObject.EntityId),
            gameObject.BaseId,
            gameObject.ObjectKind.ToString(),
            classJobId,
            new(position.X, position.Y, position.Z),
            gameObject.Rotation,
            npcName);
    }
}
