namespace EncounterScope.Core;

public sealed class CaptureTimeline
{
    private sealed record CombatEpoch(int Id, long StartedAt);

    private readonly IEventClock clock;
    private readonly long sessionStartedAt;
    private CombatEpoch? combat;
    private int combatCounter;

    public CaptureTimeline(IEventClock clock, string? sessionId = null)
    {
        this.clock = clock;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        sessionStartedAt = clock.Timestamp;
    }

    public string SessionId { get; }
    public bool IsInCombat => Volatile.Read(ref combat) is not null;
    public int? CurrentCombatId => Volatile.Read(ref combat)?.Id;

    public ObservationStamp Observe(CaptureContext context)
    {
        var now = clock.Timestamp;
        var utcNow = clock.UtcNow;
        var currentCombat = Volatile.Read(ref combat);
        return CreateStamp(now, utcNow, context, currentCombat);
    }

    public ObservedGameEvent Create(string recordType, object payload, CaptureContext context) =>
        ObservedGameEvent.From(Observe(context), recordType, payload);

    public ObservedGameEvent? StartCombat(string reason, bool observedMidCombat, CaptureContext context)
    {
        if (Volatile.Read(ref combat) is not null)
            return null;

        var now = clock.Timestamp;
        var utcNow = clock.UtcNow;
        var epoch = new CombatEpoch(Interlocked.Increment(ref combatCounter), now);
        if (Interlocked.CompareExchange(ref combat, epoch, null) is not null)
            return null;

        var stamp = CreateStamp(now, utcNow, context, epoch, combatElapsedOverride: 0);
        return ObservedGameEvent.From(
            stamp,
            RecordTypes.CombatStarted,
            new CombatBoundaryPayload(reason, observedMidCombat));
    }

    public ObservedGameEvent? EndCombat(string reason, CaptureContext context)
    {
        var epoch = Interlocked.Exchange(ref combat, null);
        if (epoch is null)
            return null;

        var now = clock.Timestamp;
        var utcNow = clock.UtcNow;
        var stamp = CreateStamp(now, utcNow, context, epoch);
        return ObservedGameEvent.From(
            stamp,
            RecordTypes.CombatEnded,
            new CombatBoundaryPayload(reason, false));
    }

    private ObservationStamp CreateStamp(
        long now,
        DateTimeOffset utcNow,
        CaptureContext context,
        CombatEpoch? epoch,
        double? combatElapsedOverride = null) =>
        new(
            SessionId,
            TimestampFormatting.Utc(utcNow),
            TimestampFormatting.ElapsedSeconds(sessionStartedAt, now, clock.Frequency),
            epoch is null
                ? null
                : combatElapsedOverride ?? TimestampFormatting.ElapsedSeconds(epoch.StartedAt, now, clock.Frequency),
            epoch?.Id,
            context.TerritoryId,
            context.ContentFinderConditionId);
}
