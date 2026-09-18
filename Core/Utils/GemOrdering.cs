namespace Magic.Utils;

/// <summary>
/// Dependency ordering shared by gems (who exports what another gem's constructor needs) and by the parts
/// within a gem (the gem class and its exports).
/// </summary>
internal static class GemOrdering
{
    /// <summary>
    /// Orders <paramref name="items"/> so that every item comes after the items providing what it requires.
    /// Requirements the host already satisfies (<paramref name="hostProvides"/>) create no edge. Items whose
    /// requirements nobody provides, items depending (transitively) on such an item, and items in a cycle are
    /// reported through <paramref name="skip"/> and left out. Among items that are ready at the same time the
    /// lowest <paramref name="priority"/> goes first, which is how logger gems get loaded before the rest.
    /// </summary>
    public static List<T> TopoSort<T>(
        IReadOnlyList<T> items,
        Func<T, IEnumerable<Type>> provides,
        Func<T, IEnumerable<Type>> requires,
        Func<Type, bool> hostProvides,
        Func<T, (int Rank, string Name)> priority,
        Action<T, string> skip)
        where T : notnull
    {
        Dictionary<Type, T> provider = [];
        foreach (T item in items)
        {
            foreach (Type contract in provides(item))
            {
                if (!provider.TryAdd(contract, item))
                    Log.Warn($"{contract} is provided by both {priority(provider[contract]).Name} and {priority(item).Name}; the first one wins.");
            }
        }

        Dictionary<T, HashSet<T>> dependencies = items.ToDictionary(i => i, _ => new HashSet<T>());
        Dictionary<T, HashSet<T>> dependents = items.ToDictionary(i => i, _ => new HashSet<T>());
        HashSet<T> skipped = [];
        foreach (T item in items)
        {
            foreach (Type needed in requires(item))
            {
                if (hostProvides(needed))
                    continue;
                if (provider.TryGetValue(needed, out T? source) && !ReferenceEquals(source, item))
                {
                    dependencies[item].Add(source);
                    dependents[source].Add(item);
                }
                else if (skipped.Add(item))
                {
                    skip(item, $"nothing provides {needed}");
                }
            }
        }

        // Anything depending on a skipped item is skipped too.
        Queue<T> pending = new(skipped);
        while (pending.TryDequeue(out T? gone))
        {
            foreach (T dependent in dependents[gone])
            {
                if (skipped.Add(dependent))
                {
                    skip(dependent, $"depends on {priority(gone).Name}, which was skipped");
                    pending.Enqueue(dependent);
                }
            }
        }

        // Kahn's algorithm; the ready set is a priority queue so loggers surface first.
        Dictionary<T, int> remaining = items
            .Where(i => !skipped.Contains(i))
            .ToDictionary(i => i, i => dependencies[i].Count(d => !skipped.Contains(d)));
        PriorityQueue<T, (int, string)> ready = new();
        foreach ((T item, int count) in remaining)
        {
            if (count == 0)
                ready.Enqueue(item, priority(item));
        }

        List<T> order = [];
        while (ready.TryDequeue(out T? item, out _))
        {
            order.Add(item);
            foreach (T dependent in dependents[item])
            {
                if (remaining.ContainsKey(dependent) && --remaining[dependent] == 0)
                    ready.Enqueue(dependent, priority(dependent));
            }
        }

        foreach (T item in remaining.Keys)
        {
            if (!order.Contains(item))
                skip(item, "it is part of a dependency cycle");
        }

        return order;
    }
}
