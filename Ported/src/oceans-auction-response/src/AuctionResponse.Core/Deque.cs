namespace AuctionResponse.Core;

/// <summary>
/// Minimal array-backed double-ended queue with indexed access. Used for the feature
/// windows and the monotonic extrema queues (Section 16) so that a decision tick costs
/// O(1) amortised per event rather than rescanning the path.
/// </summary>
public sealed class Deque<T>
{
    private T[] _items;
    private int _head;
    private int _count;

    public Deque(int capacity = 16) { _items = new T[Math.Max(4, capacity)]; }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length];
        }
    }

    public T Front => _count > 0 ? this[0] : throw new InvalidOperationException("Deque is empty.");
    public T Back => _count > 0 ? this[_count - 1] : throw new InvalidOperationException("Deque is empty.");

    public void PushBack(T item)
    {
        if (_count == _items.Length) Grow();
        _items[(_head + _count) % _items.Length] = item;
        _count++;
    }

    public T PopFront()
    {
        if (_count == 0) throw new InvalidOperationException("Deque is empty.");
        var item = _items[_head];
        _items[_head] = default!;
        _head = (_head + 1) % _items.Length;
        _count--;
        return item;
    }

    public T PopBack()
    {
        if (_count == 0) throw new InvalidOperationException("Deque is empty.");
        var idx = (_head + _count - 1) % _items.Length;
        var item = _items[idx];
        _items[idx] = default!;
        _count--;
        return item;
    }

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0; _count = 0;
    }

    private void Grow()
    {
        var next = new T[_items.Length * 2];
        for (var i = 0; i < _count; i++) next[i] = _items[(_head + i) % _items.Length];
        _items = next; _head = 0;
    }

    public IEnumerable<T> Items()
    {
        for (var i = 0; i < _count; i++) yield return this[i];
    }
}
