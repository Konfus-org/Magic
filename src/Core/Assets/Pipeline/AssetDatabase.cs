using System.Collections.Immutable;

namespace Magic.Assets.Pipeline;

/// <summary>
/// What assets there are, where they are, and how they depend on each other, in both directions. A value, replaced whole
/// under the assets' lock, and read by any thread as it is.
/// </summary>
internal sealed record AssetDatabase
{
    public static AssetDatabase Empty { get; } = new();

    /// <summary>
    /// The folders assets are found under, in order, the first winning a path two of them have.
    /// </summary>
    public ImmutableArray<string> Directories { get; init; } = [];

    public AssetIndex Index { get; init; } = AssetIndex.Empty;

    /// <summary>
    /// What each asset holds handles to or was made from: an asset is here once that is known, even if it is nothing,
    /// and leaves when its file changes.
    /// </summary>
    private ImmutableDictionary<ulong, ImmutableArray<ulong>> Dependencies { get; init; } = [];

    private ImmutableDictionary<ulong, ImmutableArray<ulong>> Dependents { get; init; } = [];

    /// <summary>
    /// What is reached from <paramref name="start"/>, down to <paramref name="levels"/> levels of
    /// <paramref name="neighbours"/>, each once.
    /// </summary>
    public static HashSet<ulong> Walk(IEnumerable<ulong> start, int levels, Func<ulong, IEnumerable<ulong>> neighbours)
    {
        HashSet<ulong> found = [];
        List<ulong> level = [.. start];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong id in level)
            {
                foreach (ulong neighbour in neighbours(id))
                {
                    if (found.Add(neighbour))
                        next.Add(neighbour);
                }
            }

            level = next;
        }

        return found;
    }

    /// <summary>
    /// What messages call asset <paramref name="id"/>: its path, or its id when no file has it.
    /// </summary>
    public string GetName(ulong id) =>
        Index.ById.TryGetValue(id, out IndexedFile? file) ? file.Path : $"The asset {id}";

    /// <summary>
    /// Whether what <paramref name="asset"/> depends on is known, even if that is nothing.
    /// </summary>
    public bool HasDependencies(ulong asset) => Dependencies.ContainsKey(asset);

    /// <summary>
    /// What <paramref name="asset"/> depends on, as far as is known, down to <paramref name="levels"/> levels: 1 for its
    /// own, <see cref="AssetManager.AllLevels"/> for everything under it.
    /// </summary>
    public HashSet<ulong> GetDependencies(ulong asset, int levels) =>
        Walk([asset], levels, id => GetOrEmpty(Dependencies, id));

    /// <summary>
    /// What depends on <paramref name="asset"/>, as far as is known, up to <paramref name="levels"/> levels: 1 for what
    /// names it itself, <see cref="AssetManager.AllLevels"/> for everything above it.
    /// </summary>
    public HashSet<ulong> GetDependents(ulong asset, int levels) =>
        Walk([asset], levels, id => GetOrEmpty(Dependents, id));

    /// <summary>
    /// This database with <paramref name="dependencies"/> being all <paramref name="asset"/> depends on.
    /// </summary>
    public AssetDatabase SetDependencies(ulong asset, ImmutableArray<ulong> dependencies)
    {
        AssetDatabase without = RemoveDependencies(asset);
        ImmutableDictionary<ulong, ImmutableArray<ulong>> dependents = without.Dependents;
        foreach (ulong dependency in dependencies)
        {
            dependents = dependents.SetItem(dependency, GetOrEmpty(dependents, dependency).Add(asset));
        }

        return without with
        {
            Dependencies = without.Dependencies.SetItem(asset, dependencies),
            Dependents = dependents,
        };
    }

    /// <summary>
    /// This database having forgotten what <paramref name="asset"/> depends on.
    /// </summary>
    public AssetDatabase RemoveDependencies(ulong asset)
    {
        ImmutableDictionary<ulong, ImmutableArray<ulong>> dependents = Dependents;
        foreach (ulong dependency in GetOrEmpty(Dependencies, asset))
        {
            ImmutableArray<ulong> left = GetOrEmpty(dependents, dependency).Remove(asset);
            dependents = left.IsEmpty ? dependents.Remove(dependency) : dependents.SetItem(dependency, left);
        }

        return this with
        {
            Dependencies = Dependencies.Remove(asset),
            Dependents = dependents,
        };
    }

    private static ImmutableArray<ulong> GetOrEmpty(
        ImmutableDictionary<ulong, ImmutableArray<ulong>> map,
        ulong asset
    ) => map.TryGetValue(asset, out ImmutableArray<ulong> ids) ? ids : [];
}
