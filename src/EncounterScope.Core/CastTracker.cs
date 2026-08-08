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

public readonly record struct ResolvedCastKey(ulong SourceGameObjectId, byte ActionType, uint ActionId);

public abstract record CastTransition(long CastObservationId, VisibleCastSnapshot Snapshot, bool ObservedMidCast);

public sealed record CastBegan(
    long CastObservationId,
    VisibleCastSnapshot Snapshot,
    bool ObservedMidCast)
    : CastTransition(CastObservationId, Snapshot, ObservedMidCast);

public sealed record CastEnded(
    long CastObservationId,
    VisibleCastSnapshot Snapshot,
    bool ObservedMidCast,
    double ObservedDurationSeconds,
    string RecordType,
    string Reason)
    : CastTransition(CastObservationId, Snapshot, ObservedMidCast);

public sealed class CastTracker
{
    private sealed class ActiveCast(
        long castObservationId,
        VisibleCastSnapshot snapshot,
        bool observedMidCast,
        double firstObservedSeconds,
        double expectedEndSeconds)
    {
        public long CastObservationId { get; } = castObservationId;
        public VisibleCastSnapshot Snapshot { get; set; } = snapshot;
        public bool ObservedMidCast { get; } = observedMidCast;
        public double FirstObservedSeconds { get; } = firstObservedSeconds;
        public double LastObservedSeconds { get; set; } = firstObservedSeconds;
        public double ExpectedEndSeconds { get; set; } = expectedEndSeconds;
    }

    private const double TimerEpsilonSeconds = 0.001;
    private readonly Dictionary<ulong, ActiveCast> activeCasts = [];
    private readonly HashSet<ulong> knownActors = [];
    private readonly List<CastTransition> transitions = [];
    private readonly List<ulong> endedActors = [];
    private long castObservationCounter;

    public IReadOnlyList<CastTransition> Update(
        IEnumerable<VisibleCastSnapshot> snapshots,
        IReadOnlySet<ulong> presentActorIds,
        double observedAtSeconds,
        IReadOnlySet<ResolvedCastKey>? resolvedActions = null)
    {
        transitions.Clear();
        endedActors.Clear();

        foreach (var snapshot in snapshots)
        {
            var actorWasKnown = knownActors.Contains(snapshot.GameObjectId);

            if (!snapshot.IsCasting || snapshot.ActionId == 0)
            {
                EndActive(snapshot.GameObjectId, observedAtSeconds, resolvedActions, "casting_stopped");
                continue;
            }

            if (activeCasts.TryGetValue(snapshot.GameObjectId, out var active))
            {
                if (active.Snapshot.ActionType == snapshot.ActionType && active.Snapshot.ActionId == snapshot.ActionId)
                {
                    active.Snapshot = snapshot;
                    active.LastObservedSeconds = observedAtSeconds;
                    active.ExpectedEndSeconds = ExpectedEnd(snapshot, observedAtSeconds);
                    continue;
                }

                EndActive(snapshot.GameObjectId, observedAtSeconds, resolvedActions, "action_changed");
            }

            var observedMidCast = !actorWasKnown && snapshot.CurrentCastSeconds > 0;
            var created = new ActiveCast(
                ++castObservationCounter,
                snapshot,
                observedMidCast,
                observedAtSeconds,
                ExpectedEnd(snapshot, observedAtSeconds));
            activeCasts.Add(snapshot.GameObjectId, created);
            transitions.Add(new CastBegan(created.CastObservationId, snapshot, observedMidCast));
        }

        foreach (var (actorId, _) in activeCasts)
        {
            if (!presentActorIds.Contains(actorId))
                endedActors.Add(actorId);
        }

        foreach (var actorId in endedActors)
            EndActive(actorId, observedAtSeconds, resolvedActions, "actor_lost");

        knownActors.IntersectWith(presentActorIds);
        knownActors.UnionWith(presentActorIds);

        return transitions;
    }

    public IReadOnlyList<CastTransition> EndAll(double observedAtSeconds, string reason)
    {
        transitions.Clear();
        endedActors.Clear();
        endedActors.AddRange(activeCasts.Keys);
        foreach (var actorId in endedActors)
            EndActive(actorId, observedAtSeconds, null, reason);
        return transitions;
    }

    public void Clear()
    {
        activeCasts.Clear();
        knownActors.Clear();
        transitions.Clear();
        endedActors.Clear();
        castObservationCounter = 0;
    }

    private void EndActive(
        ulong actorId,
        double observedAtSeconds,
        IReadOnlySet<ResolvedCastKey>? resolvedActions,
        string cancellationReason)
    {
        if (!activeCasts.Remove(actorId, out var active))
            return;

        var key = new ResolvedCastKey(
            active.Snapshot.GameObjectId,
            active.Snapshot.ActionType,
            active.Snapshot.ActionId);
        var resolved = resolvedActions?.Contains(key) == true;
        var timerElapsed = active.Snapshot.TotalCastSeconds > 0 &&
            observedAtSeconds + TimerEpsilonSeconds >= active.ExpectedEndSeconds;
        var recordType = resolved || timerElapsed
            ? RecordTypes.CastCompleted
            : RecordTypes.CastCancelled;
        transitions.Add(new CastEnded(
            active.CastObservationId,
            active.Snapshot,
            active.ObservedMidCast,
            Math.Max(0, observedAtSeconds - active.FirstObservedSeconds),
            recordType,
            resolved ? "action_resolved" : timerElapsed ? "timer_elapsed" : cancellationReason));
    }

    private static double ExpectedEnd(VisibleCastSnapshot snapshot, double observedAtSeconds) =>
        observedAtSeconds + Math.Max(0, snapshot.TotalCastSeconds - snapshot.CurrentCastSeconds);
}
