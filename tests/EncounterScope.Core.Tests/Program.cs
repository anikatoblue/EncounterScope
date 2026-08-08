using System.Globalization;
using System.Text;
using System.Text.Json;
using EncounterScope.Core;

var tests = new (string Name, Action Test)[]
{
    ("duty gate handles observed, duplicate, disable, and re-enable transitions", TestDutyGate),
    ("combat clock resets each pull and ignores wall-clock jumps", TestCombatClock),
    ("cast tracker reports transitions and mid-cast actors", TestCastTracker),
    ("bounded queues drop without exceeding capacity", TestBoundedQueue),
    ("duplicate labels preserve distinct action identities", TestDuplicateActionNames),
    ("writer emits required timestamps, ordered sequences, and privacy-safe JSONL", TestJsonlContract),
    ("writer rotates segments while preserving global sequence order", TestRotation),
    ("startup recovery preserves stale partial captures", TestRecovery),
    ("retention deletes only the oldest completed managed session", TestRetention),
    ("active-only capacity exhaustion fails safely and preserves an incomplete file", TestCapacityFailure),
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void TestDutyGate()
{
    var gate = new DutyCaptureGate(enabled: true);
    var start = gate.SetDutyBound(true, observedExistingState: true);
    Equal(DutyCaptureTransitionKind.Start, start?.Kind);
    Equal("observed_mid_duty", start?.Reason);
    Assert(start?.ObservedMidDuty == true);
    Assert(gate.SetDutyBound(true) is null);

    var disabled = gate.SetEnabled(false);
    Equal(DutyCaptureTransitionKind.Stop, disabled?.Kind);
    Equal("disabled", disabled?.Reason);
    Assert(gate.SetEnabled(false) is null);

    var enabled = gate.SetEnabled(true);
    Equal(DutyCaptureTransitionKind.Start, enabled?.Kind);
    Equal("enabled_mid_duty", enabled?.Reason);

    var exit = gate.SetDutyBound(false);
    Equal(DutyCaptureTransitionKind.Stop, exit?.Kind);
    Equal("duty_exit", exit?.Reason);
    Assert(gate.StopForUnload() is null);
}

static void TestCombatClock()
{
    var clock = new ManualClock(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
    var timeline = new CaptureTimeline(clock, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    var context = new CaptureContext(123, 456);

    var outside = timeline.Observe(context);
    Equal(0d, outside.SessionElapsedSeconds);
    Assert(outside.CombatId is null && outside.CombatElapsedSeconds is null);
    Assert(DateTimeOffset.TryParse(outside.TimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed));
    Equal(TimeSpan.Zero, parsed.Offset);

    clock.Advance(TimeSpan.FromSeconds(1.25));
    var start1 = timeline.StartCombat("condition_enter", false, context)!;
    Equal(1, start1.CombatId);
    Equal(0d, start1.CombatElapsedSeconds);
    Equal(1.25d, start1.SessionElapsedSeconds);

    clock.Advance(TimeSpan.FromSeconds(2.5));
    var during1 = timeline.Observe(context);
    Equal(2.5d, during1.CombatElapsedSeconds);

    clock.JumpWallClock(TimeSpan.FromHours(-4));
    clock.AdvanceMonotonic(TimeSpan.FromSeconds(0.5));
    var afterWallClockJump = timeline.Observe(context);
    Equal(3d, afterWallClockJump.CombatElapsedSeconds);
    Equal(4.25d, afterWallClockJump.SessionElapsedSeconds);

    var end1 = timeline.EndCombat("wipe", context)!;
    Equal(1, end1.CombatId);
    Equal(3d, end1.CombatElapsedSeconds);
    Assert(timeline.Observe(context).CombatElapsedSeconds is null);

    clock.Advance(TimeSpan.FromSeconds(7));
    var start2 = timeline.StartCombat("condition_enter", false, context)!;
    Equal(2, start2.CombatId);
    Equal(0d, start2.CombatElapsedSeconds);
    Assert(timeline.StartCombat("duplicate", false, context) is null);
    Assert(timeline.EndCombat("duty_exit", context) is not null);
    Assert(timeline.EndCombat("duplicate", context) is null);
}

static void TestCastTracker()
{
    var tracker = new CastTracker();
    var idle = Cast(1, false, 0, 0);
    Assert(Update(tracker, [idle], 0).Count == 0);

    var first = Update(tracker, [Cast(1, true, 100, 0.1f)], 0.1);
    var began = Single<CastBegan>(first);
    Equal(1L, began.CastObservationId);
    Assert(!began.ObservedMidCast);
    Assert(Update(tracker, [Cast(1, true, 100, 1.5f)], 1.5).Count == 0);

    var completed = Update(tracker, [idle], 2.6);
    var timerEnd = Single<CastEnded>(completed);
    Equal(1L, timerEnd.CastObservationId);
    Equal(RecordTypes.CastCompleted, timerEnd.RecordType);
    Equal("timer_elapsed", timerEnd.Reason);

    var second = Single<CastBegan>(Update(tracker, [Cast(1, true, 101, 0.1f)], 3));
    Equal(2L, second.CastObservationId);
    var replacement = Update(tracker, [Cast(1, true, 102, 0.1f)], 3.2);
    Equal(2, replacement.Count);
    var replaced = (CastEnded)replacement[0];
    var replacementStart = (CastBegan)replacement[1];
    Equal(RecordTypes.CastCancelled, replaced.RecordType);
    Equal("action_changed", replaced.Reason);
    Equal(3L, replacementStart.CastObservationId);

    var resolved = new HashSet<ResolvedCastKey> { new(1, 1, 102) };
    var resolvedEnd = Single<CastEnded>(Update(tracker, [idle], 3.3, resolved));
    Equal(RecordTypes.CastCompleted, resolvedEnd.RecordType);
    Equal("action_resolved", resolvedEnd.Reason);

    var newActorMidCast = Single<CastBegan>(Update(tracker, [Cast(2, true, 200, 1.2f)], 4));
    Assert(newActorMidCast.ObservedMidCast);
    Assert(Update(tracker, [], 4.1, present: new HashSet<ulong> { 2 }).Count == 0);
    var actorLost = Single<CastEnded>(Update(tracker, [], 4.2));
    Equal(RecordTypes.CastCancelled, actorLost.RecordType);
    Equal("actor_lost", actorLost.Reason);

    var reappeared = Single<CastBegan>(Update(tracker, [Cast(2, true, 200, 1.2f)], 5));
    Assert(reappeared.ObservedMidCast);
    var cleanup = Single<CastEnded>(tracker.EndAll(5.1, "wipe"));
    Equal("wipe", cleanup.Reason);
    Assert(tracker.EndAll(5.2, "wipe").Count == 0);

    for (var i = 0; i < 10_000; i++)
    {
        var actor = (ulong)(1_000 + i);
        _ = Update(tracker, [Cast(actor, true, (uint)(1_000 + i), 0)], i + 10);
        _ = Update(tracker, [], i + 10.1);
    }
}

static IReadOnlyList<CastTransition> Update(
    CastTracker tracker,
    IReadOnlyList<VisibleCastSnapshot> casts,
    double observedAtSeconds,
    IReadOnlySet<ResolvedCastKey>? resolved = null,
    IReadOnlySet<ulong>? present = null) =>
    tracker.Update(
        casts,
        present ?? casts.Select(cast => cast.GameObjectId).ToHashSet(),
        observedAtSeconds,
        resolved);

static T Single<T>(IReadOnlyList<CastTransition> transitions) where T : CastTransition
{
    Equal(1, transitions.Count);
    Assert(transitions[0] is T);
    return (T)transitions[0];
}

static void TestBoundedQueue()
{
    var queue = new BoundedDropQueue<int>(2);
    Assert(queue.TryEnqueue(1));
    Assert(queue.TryEnqueue(2));
    Assert(!queue.TryEnqueue(3));
    Equal(1L, queue.Dropped);
    Equal(2, queue.Count);
    Assert(queue.TryDequeue(out var one) && one == 1);
    Assert(queue.TryEnqueue(4));
    SequenceEqual([2, 4], queue.Drain());
}

static void TestDuplicateActionNames()
{
    var first = new ActionReference(1, "Action", 7, "attack");
    var second = new ActionReference(1, "Action", 674, "attack");
    var unknown = new ActionReference(1, "Action", 999_999, null);
    Assert(first != second);
    Equal("attack", first.Name);
    Equal("attack", second.Name);
    Assert(first.Id != second.Id);
    Equal(999_999u, unknown.Id);
    Assert(unknown.Name is null);
}

static void TestJsonlContract()
{
    using var temp = new TempDirectory();
    var options = new CaptureWriterOptions(
        SegmentLimitBytes: 1_000_000,
        TotalLimitBytes: 10_000_000,
        QueueCapacity: 64,
        FlushRecordCount: 4,
        FlushInterval: TimeSpan.FromMilliseconds(10));
    var storage = new CaptureStorage(temp.Path, options);
    var clock = new ManualClock(new DateTimeOffset(2026, 8, 2, 13, 14, 15, TimeSpan.Zero));
    var timeline = new CaptureTimeline(clock, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    var context = new CaptureContext(777, 888);
    using var writer = new JsonlCaptureWriter(
        storage,
        timeline.SessionId,
        clock.UtcNow,
        (type, payload) => timeline.Create(type, payload, context));

    Assert(writer.TryWrite(timeline.Create(
        RecordTypes.SessionStart,
        new SessionStartPayload("test", "0.1.0.0", false, true, "English"),
        context)));
    var combatStart = timeline.StartCombat("condition_enter", false, context)!;
    Assert(writer.TryWrite(combatStart));
    clock.Advance(TimeSpan.FromSeconds(1.235));
    var playerWithoutName = new ActorReference(
        "0x0000000000000001",
        "0x00000001",
        0,
        "Pc",
        19,
        new(1, 2, 3),
        0.5f,
        null);
    Assert(writer.TryWrite(timeline.Create(
        RecordTypes.CastStarted,
        new CastStartedPayload(
            1,
            new(1, "Action", 7, "attack"),
            playerWithoutName,
            null,
            0.1f,
            2f,
            2.5f,
            true,
            false),
        context)));
    clock.Advance(TimeSpan.FromSeconds(2.5));
    Assert(writer.TryWrite(timeline.Create(
        RecordTypes.CastCompleted,
        new CastTerminalPayload(
            1,
            new(1, "Action", 7, "attack"),
            playerWithoutName,
            null,
            2.5,
            2.5f,
            2f,
            2.5f,
            false,
            "timer_elapsed"),
        context)));
    var npcSource = new ActorReference(
        "0x0000000000001000",
        "0x00001000",
        42,
        "BattleNpc",
        0,
        new(5, 6, 7),
        1.5f,
        "Test Boss");
    Assert(writer.TryWrite(timeline.Create(
        RecordTypes.ActionResolved,
        new ActionResolvedPayload(
            new(1, "Action", 674, "attack"),
            npcSource,
            new(
                "0x0000000000000001",
                1234,
                0.6f,
                "0xE0000000",
                55,
                123,
                0.25f,
                674,
                2,
                3,
                true,
                true,
                1,
                new(10, 11, 12)),
            [playerWithoutName]),
        context)));
    Assert(writer.TryWrite(timeline.EndCombat("condition_exit", context)!));
    Assert(writer.TryWrite(timeline.Create(
        RecordTypes.SessionEnd,
        new SessionEndPayload("test", 0, 0, null),
        context)));
    Complete(writer);

    var documents = ReadDocuments(temp.Path);
    Assert(documents.Count >= 8);
    ulong previousSequence = 0;
    foreach (var document in documents)
    {
        var root = document.RootElement;
        Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert(!string.IsNullOrWhiteSpace(root.GetProperty("recordType").GetString()));
        Equal(timeline.SessionId, root.GetProperty("sessionId").GetString());
        var sequence = root.GetProperty("sequence").GetUInt64();
        Assert(sequence > previousSequence);
        previousSequence = sequence;
        Assert(DateTimeOffset.TryParse(
            root.GetProperty("timestampUtc").GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp));
        Equal(TimeSpan.Zero, timestamp.Offset);
        Equal(JsonValueKind.Number, root.GetProperty("sessionElapsedSeconds").ValueKind);
        Assert(root.TryGetProperty("combatElapsedSeconds", out _));
        Assert(root.TryGetProperty("combatId", out _));
        Equal(777u, root.GetProperty("territoryId").GetUInt32());
        Equal(888u, root.GetProperty("contentFinderConditionId").GetUInt32());
    }

    var cast = documents.Single(document => document.RootElement.GetProperty("recordType").GetString() == RecordTypes.CastStarted);
    var castJson = cast.RootElement.GetRawText();
    Assert(!castJson.Contains("Alice", StringComparison.Ordinal));
    Assert(!castJson.Contains("playerName", StringComparison.Ordinal));
    Equal(1.235d, cast.RootElement.GetProperty("combatElapsedSeconds").GetDouble());
    Equal(1L, cast.RootElement.GetProperty("payload").GetProperty("castObservationId").GetInt64());

    var castEnd = documents.Single(document =>
        document.RootElement.GetProperty("recordType").GetString() == RecordTypes.CastCompleted);
    var castEndPayload = castEnd.RootElement.GetProperty("payload");
    Equal(1L, castEndPayload.GetProperty("castObservationId").GetInt64());
    Equal(2.5d, castEndPayload.GetProperty("observedDurationSeconds").GetDouble());
    Equal("timer_elapsed", castEndPayload.GetProperty("reason").GetString());
    Assert(!castEnd.RootElement.GetRawText().Contains("playerName", StringComparison.Ordinal));

    var resolved = documents.Single(document =>
        document.RootElement.GetProperty("recordType").GetString() == RecordTypes.ActionResolved);
    var resolvedJson = resolved.RootElement.GetRawText();
    Assert(resolvedJson.Contains("Test Boss", StringComparison.Ordinal));
    Assert(!resolvedJson.Contains("playerName", StringComparison.Ordinal));
    Assert(!resolvedJson.Contains("\"effects\"", StringComparison.Ordinal));
    Equal(
        674u,
        resolved.RootElement.GetProperty("payload").GetProperty("action").GetProperty("id").GetUInt32());
    Equal(
        2,
        resolved.RootElement.GetProperty("payload").GetProperty("header").GetProperty("animationVariation").GetByte());

    foreach (var document in documents)
        document.Dispose();
}

static void TestRotation()
{
    using var temp = new TempDirectory();
    var storage = new CaptureStorage(
        temp.Path,
        new(
            SegmentLimitBytes: 700,
            TotalLimitBytes: 1_000_000,
            QueueCapacity: 128,
            FlushRecordCount: 8,
            FlushInterval: TimeSpan.FromMilliseconds(10)));
    var clock = new ManualClock(DateTimeOffset.UtcNow);
    var timeline = new CaptureTimeline(clock, "cccccccccccccccccccccccccccccccc");
    var context = new CaptureContext(1, 2);
    using var writer = new JsonlCaptureWriter(
        storage,
        timeline.SessionId,
        clock.UtcNow,
        (type, payload) => timeline.Create(type, payload, context));

    for (var i = 0; i < 12; i++)
    {
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert(writer.TryWrite(timeline.Create(
            RecordTypes.Health,
            new HealthPayload(i, i, i, i * 100, new string('x', 120)),
            context)));
    }
    Complete(writer);

    var files = Directory.GetFiles(temp.Path, "*.jsonl").OrderBy(path => path).ToArray();
    Assert(files.Length > 1);
    Assert(Directory.GetFiles(temp.Path, "*.partial").Length == 0);
    var documents = ReadDocuments(temp.Path);
    ulong previous = 0;
    foreach (var document in documents)
    {
        var sequence = document.RootElement.GetProperty("sequence").GetUInt64();
        Assert(sequence > previous);
        previous = sequence;
        document.Dispose();
    }
}

static void TestRecovery()
{
    using var temp = new TempDirectory();
    var options = new CaptureWriterOptions(TotalLimitBytes: 1_000_000);
    var storage = new CaptureStorage(temp.Path, options);
    var session = "dddddddddddddddddddddddddddddddd";
    var partial = storage.GetActiveSegmentPath(DateTimeOffset.UtcNow, session, 1);
    File.WriteAllText(partial, "partial", Encoding.UTF8);

    _ = new CaptureStorage(temp.Path, options);
    Assert(!File.Exists(partial));
    Assert(Directory.GetFiles(temp.Path, "*.incomplete.jsonl").Length == 1);
}

static void TestRetention()
{
    using var temp = new TempDirectory();
    var storage = new CaptureStorage(
        temp.Path,
        new(SegmentLimitBytes: 1_000, TotalLimitBytes: 250, QueueCapacity: 4, FlushRecordCount: 1));
    var oldSession = "11111111111111111111111111111111";
    var newSession = "22222222222222222222222222222222";
    var activeSession = "33333333333333333333333333333333";
    var oldFile = CreateManagedCompleted(storage, oldSession, 100, DateTime.UtcNow.AddMinutes(-10));
    var newFile = CreateManagedCompleted(storage, newSession, 100, DateTime.UtcNow.AddMinutes(-5));
    storage.RegisterSession(activeSession);
    var activeFile = storage.GetActiveSegmentPath(DateTimeOffset.UtcNow, activeSession, 1);
    File.WriteAllBytes(activeFile, new byte[100]);
    var unrelated = Path.Combine(temp.Path, "unrelated.bin");
    File.WriteAllBytes(unrelated, new byte[1_000]);

    var total = storage.EnforceRetention();
    Assert(total <= 250);
    Assert(!File.Exists(oldFile));
    Assert(File.Exists(newFile));
    Assert(File.Exists(activeFile));
    Assert(File.Exists(unrelated));
    storage.UnregisterSession(activeSession);
}

static void TestCapacityFailure()
{
    using var temp = new TempDirectory();
    var storage = new CaptureStorage(
        temp.Path,
        new(
            SegmentLimitBytes: 10_000,
            TotalLimitBytes: 1,
            QueueCapacity: 4,
            FlushRecordCount: 1,
            FlushInterval: TimeSpan.FromMilliseconds(5)));
    var clock = new ManualClock(DateTimeOffset.UtcNow);
    var timeline = new CaptureTimeline(clock, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
    var context = new CaptureContext(1, null);
    using var writer = new JsonlCaptureWriter(
        storage,
        timeline.SessionId,
        clock.UtcNow,
        (type, payload) => timeline.Create(type, payload, context));

    writer.Complete();
    Assert(writer.Completion.Wait(TimeSpan.FromSeconds(5)));
    Assert(writer.Failure is CaptureCapacityExceededException);
    Assert(Directory.GetFiles(temp.Path, "*.incomplete.jsonl").Length == 1);
}

static VisibleCastSnapshot Cast(ulong actor, bool casting, uint action, float current) =>
    new(actor, casting, 1, action, 0, current, 2, 2.5f, true);

static string CreateManagedCompleted(
    CaptureStorage storage,
    string session,
    int bytes,
    DateTime lastWriteTimeUtc)
{
    var active = storage.GetActiveSegmentPath(DateTimeOffset.UtcNow, session, 1);
    File.WriteAllBytes(active, new byte[bytes]);
    var completed = storage.FinalizeSegment(active);
    File.SetLastWriteTimeUtc(completed, lastWriteTimeUtc);
    return completed;
}

static void Complete(JsonlCaptureWriter writer)
{
    writer.Complete();
    Assert(writer.Completion.Wait(TimeSpan.FromSeconds(5)));
    if (writer.Failure is not null)
        throw new InvalidOperationException("writer failed", writer.Failure);
}

static List<JsonDocument> ReadDocuments(string directory) =>
    Directory.GetFiles(directory, "*.jsonl")
        .OrderBy(path => path, StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonDocument.Parse(line))
        .ToList();

static void Assert(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("assertion failed");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
}

static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
}

file sealed class ManualClock(DateTimeOffset utcNow) : IEventClock
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;
    public long Timestamp { get; private set; }
    public long Frequency => 1_000;

    public void Advance(TimeSpan duration)
    {
        AdvanceMonotonic(duration);
        UtcNow = UtcNow.Add(duration);
    }

    public void AdvanceMonotonic(TimeSpan duration) =>
        Timestamp += (long)Math.Round(duration.TotalSeconds * Frequency);

    public void JumpWallClock(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}

file sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EncounterScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
