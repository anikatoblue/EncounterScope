using EncounterScope.Core;

namespace EncounterScope;

internal sealed class CaptureRuntime
{
    private long rawEventsDropped;
    private long normalizedEventsDropped;
    private long hookErrors;

    public CaptureRuntime(
        SessionContextState context,
        CaptureTimeline timeline,
        JsonlCaptureWriter writer)
    {
        Context = context;
        Timeline = timeline;
        Writer = writer;
    }

    public SessionContextState Context { get; }
    public CaptureTimeline Timeline { get; }
    public JsonlCaptureWriter Writer { get; }
    public CastTracker CastTracker { get; } = new();
    public long RawEventsDropped => Interlocked.Read(ref rawEventsDropped);
    public long NormalizedEventsDropped => Interlocked.Read(ref normalizedEventsDropped);
    public long HookErrors => Interlocked.Read(ref hookErrors);

    public void IncrementRawDrop() => Interlocked.Increment(ref rawEventsDropped);
    public void IncrementNormalizedDrop() => Interlocked.Increment(ref normalizedEventsDropped);
    public void IncrementHookError() => Interlocked.Increment(ref hookErrors);

    public bool Publish(ObservedGameEvent gameEvent)
    {
        if (Writer.TryWrite(gameEvent))
            return true;

        IncrementNormalizedDrop();
        return false;
    }

    public bool Publish(string recordType, object payload) =>
        Publish(Timeline.Create(recordType, payload, Context.Snapshot));
}
