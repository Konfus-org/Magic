using System.Collections;

namespace Magic.Utils;

/// <summary>
/// Owns what it is given: disposing this disposes every item, in the order they were given. For a type that makes
/// several disposable things and lets go of them together; the build then checks that this is disposed, where it
/// cannot follow items into an array or a list. Not thread-safe.
/// </summary>
public sealed class Disposables<T>(params T[] items) : IDisposable, IReadOnlyList<T> where T : IDisposable
{
    private readonly List<T> _items = [.. items];

    public void Dispose()
    {
        foreach (T item in _items)
            item.Dispose();

        _items.Clear();
    }

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    /// <summary>
    /// Takes <paramref name="item"/> over, and answers it.
    /// </summary>
    public T Add(T item)
    {
        _items.Add(item);
        return item;
    }

    /// <summary>
    /// Disposes <paramref name="item"/> now and lets go of it; false when it was not here.
    /// </summary>
    public bool Remove(T item)
    {
        if (!_items.Remove(item))
            return false;

        item.Dispose();
        return true;
    }

    public List<T>.Enumerator GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _items.GetEnumerator();
    }
}
