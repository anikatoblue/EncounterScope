using System.Text.RegularExpressions;

namespace EncounterScope.Core;

public sealed record CaptureWriterOptions(
    long SegmentLimitBytes = 100_000_000,
    long TotalLimitBytes = 3_000_000_000,
    int QueueCapacity = 16_384,
    int FlushRecordCount = 256,
    TimeSpan? FlushInterval = null)
{
    public TimeSpan EffectiveFlushInterval => FlushInterval ?? TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (SegmentLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(SegmentLimitBytes));
        if (TotalLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(TotalLimitBytes));
        if (QueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
        if (FlushRecordCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(FlushRecordCount));
        if (EffectiveFlushInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(FlushInterval));
    }
}

public sealed class CaptureCapacityExceededException(long totalBytes, long maximumBytes)
    : IOException($"EncounterScope capture storage is {totalBytes} bytes, above the {maximumBytes}-byte limit.")
{
    public long TotalBytes { get; } = totalBytes;
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed partial class CaptureStorage
{
    private readonly Lock sync = new();
    private readonly HashSet<string> activeSessions = new(StringComparer.OrdinalIgnoreCase);

    public CaptureStorage(string directoryPath, CaptureWriterOptions options)
    {
        options.Validate();
        DirectoryPath = directoryPath;
        Options = options;
        Directory.CreateDirectory(directoryPath);
        RecoverOrphans();
    }

    public string DirectoryPath { get; }
    public CaptureWriterOptions Options { get; }

    public void RegisterSession(string sessionId)
    {
        lock (sync)
            activeSessions.Add(sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        lock (sync)
            activeSessions.Remove(sessionId);
    }

    public string GetActiveSegmentPath(DateTimeOffset sessionStartedUtc, string sessionId, int segmentIndex)
    {
        var stamp = sessionStartedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'");
        var name = $"encounterscope_{stamp}_{sessionId}_part{segmentIndex:D3}.jsonl.partial";
        return Path.Combine(DirectoryPath, name);
    }

    public string FinalizeSegment(string activePath)
    {
        if (!IsManagedActiveFile(Path.GetFileName(activePath)))
            throw new InvalidOperationException("Refusing to finalize a file outside the EncounterScope pattern.");

        var completedPath = activePath[..^".partial".Length];
        lock (sync)
            File.Move(activePath, completedPath, overwrite: false);
        return completedPath;
    }

    public string PreserveIncompleteSegment(string activePath)
    {
        if (!IsManagedActiveFile(Path.GetFileName(activePath)))
            throw new InvalidOperationException("Refusing to preserve a file outside the EncounterScope pattern.");

        lock (sync)
            return PreserveIncompleteSegmentLocked(activePath);
    }

    public long EnforceRetention()
    {
        lock (sync)
        {
            var files = EnumerateManagedFiles();
            var total = files.Sum(file => file.Length);
            if (total <= Options.TotalLimitBytes)
                return total;

            var removableGroups = files
                .GroupBy(file => file.SessionId, StringComparer.OrdinalIgnoreCase)
                .Where(group => !activeSessions.Contains(group.Key) && group.All(file => !file.IsActive))
                .OrderBy(group => group.Min(file => file.LastWriteTimeUtc))
                .ToArray();

            foreach (var group in removableGroups)
            {
                foreach (var file in group)
                {
                    File.Delete(file.FullName);
                    total -= file.Length;
                }

                if (total <= Options.TotalLimitBytes)
                    break;
            }

            return total;
        }
    }

    public long GetManagedTotalBytes()
    {
        lock (sync)
            return EnumerateManagedFiles().Sum(file => file.Length);
    }

    private void RecoverOrphans()
    {
        lock (sync)
        {
            foreach (var activePath in Directory.EnumerateFiles(DirectoryPath, "encounterscope_*.jsonl.partial"))
            {
                if (!IsManagedActiveFile(Path.GetFileName(activePath)))
                    continue;

                PreserveIncompleteSegmentLocked(activePath);
            }
        }
    }

    private static string PreserveIncompleteSegmentLocked(string activePath)
    {
        var recoveredPath = activePath[..^".jsonl.partial".Length] + ".incomplete.jsonl";
        if (File.Exists(recoveredPath))
        {
            var suffix = 1;
            var stem = activePath[..^".jsonl.partial".Length];
            do
            {
                recoveredPath = $"{stem}_recovered{suffix:D3}.incomplete.jsonl";
                suffix++;
            }
            while (File.Exists(recoveredPath));
        }

        File.Move(activePath, recoveredPath, overwrite: false);
        return recoveredPath;
    }

    private List<ManagedCaptureFile> EnumerateManagedFiles()
    {
        var result = new List<ManagedCaptureFile>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "encounterscope_*"))
        {
            var fileName = Path.GetFileName(path);
            var match = ManagedFilePattern().Match(fileName);
            if (!match.Success)
                continue;

            var info = new FileInfo(path);
            result.Add(new(
                path,
                match.Groups["session"].Value,
                fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase),
                info.Length,
                info.LastWriteTimeUtc));
        }
        return result;
    }

    private static bool IsManagedActiveFile(string fileName) =>
        ManagedFilePattern().Match(fileName) is { Success: true } match &&
        match.Groups["active"].Success;

    private sealed record ManagedCaptureFile(
        string FullName,
        string SessionId,
        bool IsActive,
        long Length,
        DateTime LastWriteTimeUtc);

    [GeneratedRegex(
        "^encounterscope_\\d{8}T\\d{9}Z_(?<session>[0-9a-f]{32})_part\\d{3}(?:_recovered\\d{3})?\\.(?:(?<active>jsonl\\.partial)|jsonl|incomplete\\.jsonl)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ManagedFilePattern();
}
