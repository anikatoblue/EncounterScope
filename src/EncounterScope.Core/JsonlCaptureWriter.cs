using System.Diagnostics;
using System.Text.Json;

namespace EncounterScope.Core;

public sealed class JsonlCaptureWriter : IDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly CaptureStorage storage;
    private readonly string sessionId;
    private readonly DateTimeOffset sessionStartedUtc;
    private readonly Func<string, object, ObservedGameEvent> createSystemEvent;
    private readonly BoundedDropQueue<ObservedGameEvent> queue;
    private readonly SemaphoreSlim signal = new(0);
    private readonly Task worker;
    private FileStream? stream;
    private string? activePath;
    private int segmentIndex;
    private int recordsSinceFlush;
    private long segmentBytes;
    private long sessionBytes;
    private long nextSequence;
    private long lastFlushTimestamp;
    private int completing;
    private int disposed;
    private int completed;
    private Exception? failure;

    public JsonlCaptureWriter(
        CaptureStorage storage,
        string sessionId,
        DateTimeOffset sessionStartedUtc,
        Func<string, object, ObservedGameEvent> createSystemEvent)
    {
        this.storage = storage;
        this.sessionId = sessionId;
        this.sessionStartedUtc = sessionStartedUtc;
        this.createSystemEvent = createSystemEvent;
        queue = new(storage.Options.QueueCapacity);
        storage.RegisterSession(sessionId);
        worker = Task.Run(Run);
    }

    public string? CurrentPath => Volatile.Read(ref activePath);
    public long SessionBytes => Interlocked.Read(ref sessionBytes);
    public long DroppedEvents => queue.Dropped;
    public Exception? Failure => Volatile.Read(ref failure);
    public bool IsCompleted => Volatile.Read(ref completed) != 0;
    public Task Completion => worker;

    public bool TryWrite(ObservedGameEvent gameEvent)
    {
        if (Volatile.Read(ref completing) != 0 || Failure is not null)
            return false;

        if (!queue.TryEnqueue(gameEvent))
            return false;

        if (queue.Count == 1)
            signal.Release();
        return true;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref completing, 1) == 0)
            signal.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Complete();
        try
        {
            if (worker.Wait(TimeSpan.FromSeconds(5)))
                signal.Dispose();
        }
        catch
        {
            // Failure is exposed through the worker status and the active partial is recoverable.
        }
    }

    private void Run()
    {
        try
        {
            lastFlushTimestamp = Stopwatch.GetTimestamp();
            OpenSegment();
            while (Volatile.Read(ref completing) == 0 || queue.Count > 0)
            {
                var elapsed = Stopwatch.GetElapsedTime(lastFlushTimestamp);
                var wait = storage.Options.EffectiveFlushInterval - elapsed;
                signal.Wait(wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                DrainQueue();
                if (Stopwatch.GetElapsedTime(lastFlushTimestamp) >= storage.Options.EffectiveFlushInterval)
                    FlushAndCheckCapacity();
            }

            DrainQueue();
            CloseSegment(clean: true);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref failure, exception);
            try
            {
                CloseSegment(clean: false);
                if (activePath is not null && File.Exists(activePath))
                    Volatile.Write(ref activePath, storage.PreserveIncompleteSegment(activePath));
            }
            catch
            {
                // Leave the partial in place for startup recovery when preservation also fails.
            }
        }
        finally
        {
            storage.UnregisterSession(sessionId);
            try
            {
                if (Failure is null)
                    storage.EnforceRetention();
            }
            catch
            {
                // The original writer failure, if any, is the actionable error.
            }
            Volatile.Write(ref completed, 1);
        }
    }

    private void DrainQueue()
    {
        while (queue.TryDequeue(out var gameEvent))
            WriteEvent(gameEvent!, allowRotation: true);
    }

    private void OpenSegment()
    {
        segmentIndex++;
        var path = storage.GetActiveSegmentPath(sessionStartedUtc, sessionId, segmentIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        Volatile.Write(ref activePath, path);
        segmentBytes = 0;
        recordsSinceFlush = 0;
        WriteEvent(
            createSystemEvent(RecordTypes.SegmentStart, new SegmentBoundaryPayload(segmentIndex, Path.GetFileName(path))),
            allowRotation: false);
    }

    private void CloseSegment(bool clean)
    {
        if (stream is null)
            return;

        if (clean)
        {
            WriteEvent(
                createSystemEvent(
                    RecordTypes.SegmentEnd,
                    new SegmentBoundaryPayload(segmentIndex, Path.GetFileName(activePath!))),
                allowRotation: false);
        }

        try
        {
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            stream.Dispose();
            stream = null;
        }

        if (clean && activePath is not null)
            Volatile.Write(ref activePath, storage.FinalizeSegment(activePath));
    }

    private void RotateSegment()
    {
        CloseSegment(clean: true);
        var total = storage.EnforceRetention();
        if (total > storage.Options.TotalLimitBytes)
            throw new CaptureCapacityExceededException(total, storage.Options.TotalLimitBytes);
        OpenSegment();
    }

    private void WriteEvent(ObservedGameEvent gameEvent, bool allowRotation)
    {
        if (stream is null)
            throw new InvalidOperationException("The capture segment is not open.");

        gameEvent = gameEvent with { Sequence = (ulong)Interlocked.Increment(ref nextSequence) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(gameEvent, JsonOptions);
        var incomingBytes = bytes.LongLength + NewLine.LongLength;

        if (allowRotation && segmentBytes > 0 && segmentBytes + incomingBytes > storage.Options.SegmentLimitBytes)
        {
            RotateSegment();
            gameEvent = gameEvent with { Sequence = (ulong)Interlocked.Increment(ref nextSequence) };
            bytes = JsonSerializer.SerializeToUtf8Bytes(gameEvent, JsonOptions);
            incomingBytes = bytes.LongLength + NewLine.LongLength;
        }

        stream.Write(bytes);
        stream.Write(NewLine);
        segmentBytes += incomingBytes;
        Interlocked.Add(ref sessionBytes, incomingBytes);
        recordsSinceFlush++;

        if (recordsSinceFlush >= storage.Options.FlushRecordCount)
            FlushAndCheckCapacity();
    }

    private void FlushAndCheckCapacity()
    {
        if (stream is null || recordsSinceFlush == 0)
            return;

        stream.Flush();
        recordsSinceFlush = 0;
        lastFlushTimestamp = Stopwatch.GetTimestamp();
        var total = storage.EnforceRetention();
        if (total > storage.Options.TotalLimitBytes)
            throw new CaptureCapacityExceededException(total, storage.Options.TotalLimitBytes);
    }
}
