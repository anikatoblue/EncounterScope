namespace EncounterScope.Core;

public readonly record struct VisibleStatusSnapshot(
    ulong TargetGameObjectId,
    int SlotIndex,
    uint StatusId,
    uint SourceEntityId,
    ushort Parameter,
    byte? StackCount,
    float RemainingDurationSeconds);

public abstract record StatusTransition(
    long StatusObservationId,
    VisibleStatusSnapshot Snapshot,
    double? PredictedExpirationSeconds,
    bool ObservedMidStatus);

public sealed record StatusGained(
    long StatusObservationId,
    VisibleStatusSnapshot Snapshot,
    double? PredictedExpirationSeconds,
    bool ObservedMidStatus)
    : StatusTransition(StatusObservationId, Snapshot, PredictedExpirationSeconds, ObservedMidStatus);

public sealed record StatusUpdated(
    long StatusObservationId,
    VisibleStatusSnapshot Snapshot,
    double? PredictedExpirationSeconds,
    bool ObservedMidStatus,
    IReadOnlyList<string> Changes)
    : StatusTransition(StatusObservationId, Snapshot, PredictedExpirationSeconds, ObservedMidStatus);

public sealed record StatusRemoved(
    long StatusObservationId,
    VisibleStatusSnapshot Snapshot,
    double? PredictedExpirationSeconds,
    bool ObservedMidStatus,
    string Reason)
    : StatusTransition(StatusObservationId, Snapshot, PredictedExpirationSeconds, ObservedMidStatus);

public sealed class StatusTracker
{
    public const double ExpirationToleranceSeconds = 0.5;

    private readonly record struct StatusKey(ulong TargetGameObjectId, int SlotIndex);

    private sealed record ActiveStatus(
        long ObservationId,
        VisibleStatusSnapshot Snapshot,
        double? PredictedExpirationSeconds,
        bool ObservedMidStatus);

    private readonly Dictionary<StatusKey, ActiveStatus> active = [];
    private readonly HashSet<ulong> observedActors = [];
    private long nextObservationId;

    public IReadOnlyList<StatusTransition> Update(
        IReadOnlyList<VisibleStatusSnapshot> snapshots,
        IReadOnlySet<ulong> presentActorIds,
        double observedAtSeconds,
        IReadOnlySet<ulong>? statusSnapshotActorIds = null)
    {
        var transitions = new List<StatusTransition>();
        var currentKeys = new HashSet<StatusKey>();
        statusSnapshotActorIds ??= presentActorIds;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.StatusId == 0)
                continue;

            var key = new StatusKey(snapshot.TargetGameObjectId, snapshot.SlotIndex);
            currentKeys.Add(key);
            double? predictedExpiration = snapshot.RemainingDurationSeconds > 0 &&
                float.IsFinite(snapshot.RemainingDurationSeconds)
                ? observedAtSeconds + snapshot.RemainingDurationSeconds
                : null;

            if (!active.TryGetValue(key, out var previous))
            {
                var gained = Begin(snapshot, predictedExpiration, !observedActors.Contains(snapshot.TargetGameObjectId));
                active[key] = gained;
                transitions.Add(new StatusGained(
                    gained.ObservationId,
                    gained.Snapshot,
                    gained.PredictedExpirationSeconds,
                    gained.ObservedMidStatus));
                continue;
            }

            if (previous.Snapshot.StatusId != snapshot.StatusId)
            {
                transitions.Add(Remove(previous, "replaced"));
                var gained = Begin(snapshot, predictedExpiration, observedMidStatus: false);
                active[key] = gained;
                transitions.Add(new StatusGained(
                    gained.ObservationId,
                    gained.Snapshot,
                    gained.PredictedExpirationSeconds,
                    gained.ObservedMidStatus));
                continue;
            }

            var changes = GetChanges(previous, snapshot, predictedExpiration);
            var storedExpiration = changes.Contains("refreshed", StringComparer.Ordinal) ||
                changes.Any(change => change.StartsWith("expiration_", StringComparison.Ordinal))
                ? predictedExpiration
                : previous.PredictedExpirationSeconds;
            var updated = previous with
            {
                Snapshot = snapshot,
                PredictedExpirationSeconds = storedExpiration,
            };
            active[key] = updated;

            if (changes.Count > 0)
            {
                transitions.Add(new StatusUpdated(
                    updated.ObservationId,
                    updated.Snapshot,
                    updated.PredictedExpirationSeconds,
                    updated.ObservedMidStatus,
                    changes));
            }
        }

        foreach (var (key, previous) in active.ToArray())
        {
            if (currentKeys.Contains(key))
                continue;

            if (presentActorIds.Contains(key.TargetGameObjectId) &&
                !statusSnapshotActorIds.Contains(key.TargetGameObjectId))
                continue;

            var reason = !presentActorIds.Contains(key.TargetGameObjectId)
                ? "actor_lost"
                : previous.PredictedExpirationSeconds is { } expiration &&
                    Math.Abs(observedAtSeconds - expiration) <= ExpirationToleranceSeconds
                    ? "natural_expiration"
                    : "removed";
            transitions.Add(Remove(previous, reason));
            active.Remove(key);
        }

        observedActors.IntersectWith(presentActorIds);
        observedActors.UnionWith(presentActorIds);
        return transitions;
    }

    public IReadOnlyList<StatusTransition> EndAll(string reason)
    {
        var transitions = active.Values
            .OrderBy(status => status.ObservationId)
            .Select(status => (StatusTransition)Remove(status, reason))
            .ToArray();
        active.Clear();
        observedActors.Clear();
        return transitions;
    }

    public void Clear()
    {
        active.Clear();
        observedActors.Clear();
    }

    private ActiveStatus Begin(
        VisibleStatusSnapshot snapshot,
        double? predictedExpiration,
        bool observedMidStatus) =>
        new(++nextObservationId, snapshot, predictedExpiration, observedMidStatus);

    private static List<string> GetChanges(
        ActiveStatus previous,
        VisibleStatusSnapshot snapshot,
        double? predictedExpiration)
    {
        var changes = new List<string>(4);
        if (previous.Snapshot.SourceEntityId != snapshot.SourceEntityId)
            changes.Add("source_changed");
        if (previous.Snapshot.Parameter != snapshot.Parameter)
            changes.Add("parameter_changed");
        if (previous.Snapshot.StackCount != snapshot.StackCount)
            changes.Add("stack_changed");

        if (predictedExpiration is { } currentExpiration &&
            previous.PredictedExpirationSeconds is { } previousExpiration)
        {
            var expirationDelta = currentExpiration - previousExpiration;
            if (expirationDelta > ExpirationToleranceSeconds)
                changes.Add("refreshed");
            else if (expirationDelta < -ExpirationToleranceSeconds)
                changes.Add("expiration_changed");
        }
        else if (predictedExpiration != previous.PredictedExpirationSeconds)
        {
            changes.Add(predictedExpiration is null ? "expiration_unavailable" : "expiration_observed");
        }
        return changes;
    }

    private static StatusRemoved Remove(ActiveStatus status, string reason) =>
        new(
            status.ObservationId,
            status.Snapshot,
            status.PredictedExpirationSeconds,
            status.ObservedMidStatus,
            reason);
}
