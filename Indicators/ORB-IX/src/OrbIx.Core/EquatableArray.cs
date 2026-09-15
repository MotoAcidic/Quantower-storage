using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace OrbIx.Core;

/// <summary>
/// An immutable sequence that compares by value.
///
/// This exists because §13's V1 requires that replaying the same recording twice produces
/// identical module output, and the natural way to check that is record equality. A record
/// holding a plain array or <see cref="IReadOnlyList{T}"/> compares that member by
/// reference, so two snapshots with identical contents built from separately-loaded
/// configuration compare unequal — a golden-file comparison would report a regression that
/// is not one, and, worse, could be "fixed" by loosening the comparison until it stopped
/// catching real changes.
///
/// Fixing the container rather than the comparison keeps record equality meaning what it
/// looks like it means.
/// </summary>
public readonly struct EquatableArray<T> : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly T[] items;

    /// <summary>
    /// Copies <paramref name="items"/>. The parameter is annotated nullable because the
    /// guard below exists for a reason: this type is reachable from configuration binding
    /// and from callers that are not nullable-aware, so null genuinely can arrive and is
    /// refused rather than turned into an empty sequence.
    /// </summary>
    public EquatableArray(IEnumerable<T>? items)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));

        this.items = items.ToArray();
    }

    /// <summary>An empty sequence. Equal to any other empty sequence of the same type.</summary>
    public static EquatableArray<T> Empty => new(Array.Empty<T>());

    public int Count => this.items?.Length ?? 0;

    public T this[int index] => this.items is null
        ? throw new IndexOutOfRangeException("The sequence is empty.")
        : this.items[index];

    public bool Equals(EquatableArray<T> other)
    {
        var mine = this.items ?? Array.Empty<T>();
        var theirs = other.items ?? Array.Empty<T>();

        if (mine.Length != theirs.Length)
            return false;

        for (var i = 0; i < mine.Length; i++)
        {
            if (!mine[i].Equals(theirs[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var item in this.items ?? Array.Empty<T>())
            hash.Add(item);

        return hash.ToHashCode();
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(this.items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", this.items ?? Array.Empty<T>()) + "]";
}
