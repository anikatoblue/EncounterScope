namespace EncounterScope.Core;

public readonly record struct VisibleCastSnapshot(
    ulong GameObjectId,
    bool IsCasting,
    byte ActionType,
    uint ActionId,
    ulong TargetGameObjectId,
    float CurrentCastSeconds,
    float BaseCastSeconds,
    float TotalCastSeconds,
    bool Interruptible);

public sealed record CastBegan(VisibleCastSnapshot Snapshot, bool ObservedMidCast);

public sealed class CastTracker
{
    private readonly HashSet<ulong> knownActors = [];
    private readonly HashSet<ulong> presentActors = [];
    private readonly Dictionary<ulong, (byte Type, uint Id)> activeCasts = [];
    private readonly List<ulong> missingActors = [];
    private readonly List<CastBegan> startedCasts = [];

    public IReadOnlyList<CastBegan> Update(IEnumerable<VisibleCastSnapshot> snapshots)
    {
        presentActors.Clear();
        missingActors.Clear();
        startedCasts.Clear();

        foreach (var snapshot in snapshots)
        {
            presentActors.Add(snapshot.GameObjectId);
            var wasKnown = knownActors.Add(snapshot.GameObjectId) == false;

            if (!snapshot.IsCasting || snapshot.ActionId == 0)
            {
                activeCasts.Remove(snapshot.GameObjectId);
                continue;
            }

            var identity = (snapshot.ActionType, snapshot.ActionId);
            if (activeCasts.TryGetValue(snapshot.GameObjectId, out var previous) && previous == identity)
                continue;

            activeCasts[snapshot.GameObjectId] = identity;
            startedCasts.Add(new(snapshot, !wasKnown && snapshot.CurrentCastSeconds > 0));
        }

        foreach (var actorId in knownActors)
        {
            if (!presentActors.Contains(actorId))
                missingActors.Add(actorId);
        }

        foreach (var actorId in missingActors)
        {
            knownActors.Remove(actorId);
            activeCasts.Remove(actorId);
        }

        return startedCasts;
    }

    public void Clear()
    {
        knownActors.Clear();
        presentActors.Clear();
        activeCasts.Clear();
        missingActors.Clear();
        startedCasts.Clear();
    }
}
