using EncounterScope.Core;

namespace EncounterScope;

internal sealed class SessionContextState(uint territoryId, uint? contentFinderConditionId)
{
    private int territory = unchecked((int)territoryId);
    private long contentFinderCondition = contentFinderConditionId ?? 0;

    public CaptureContext Snapshot => new(
        unchecked((uint)Volatile.Read(ref territory)),
        Volatile.Read(ref contentFinderCondition) is var value && value != 0 ? unchecked((uint)value) : null);

    public void Update(uint newTerritoryId, uint? newContentFinderConditionId)
    {
        Volatile.Write(ref territory, unchecked((int)newTerritoryId));
        Volatile.Write(ref contentFinderCondition, newContentFinderConditionId ?? 0);
    }
}
