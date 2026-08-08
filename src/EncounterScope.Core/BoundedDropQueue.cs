using System.Collections.Concurrent;

namespace EncounterScope.Core;

public sealed class BoundedDropQueue<T>
{
    private readonly ConcurrentQueue<T> queue = new();
    private readonly int capacity;
    private int count;
    private long dropped;

    public BoundedDropQueue(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public int Count => Volatile.Read(ref count);
    public long Dropped => Interlocked.Read(ref dropped);

    public bool TryEnqueue(T item)
    {
        if (Interlocked.Increment(ref count) > capacity)
        {
            Interlocked.Decrement(ref count);
            Interlocked.Increment(ref dropped);
            return false;
        }

        queue.Enqueue(item);
        return true;
    }

    public bool TryDequeue(out T? item)
    {
        if (!queue.TryDequeue(out item))
            return false;

        Interlocked.Decrement(ref count);
        return true;
    }

    public IReadOnlyList<T> Drain()
    {
        var items = new List<T>();
        while (TryDequeue(out var item))
            items.Add(item!);
        return items;
    }
}
