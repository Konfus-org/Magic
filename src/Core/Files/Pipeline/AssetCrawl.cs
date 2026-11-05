using System.Collections.Immutable;
using System.Reflection;
using Magic.Utils;

namespace Magic.Files.Pipeline;

/// <summary>
/// Walks what assets depend on, down or up to a number of levels, finding what the database does not know yet, and loads
/// what it finds: what <see cref="Assets"/>'s dependency calls run. An asset's own handles are found by
/// <see cref="AssetScan"/>.
/// </summary>
internal static class AssetCrawl
{
    /// <summary>
    /// The ids <paramref name="id"/> depends on, down to <paramref name="levels"/> levels, scanning each.
    /// </summary>
    public static async Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync(
        Assets assets,
        ulong id,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Finding what {assets.Database.GetName(id)} depends on";
        HashSet<ulong> found = [];
        List<ulong> level = [id];
        int scanned = 0;
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong asset in level)
            {
                Result<ImmutableArray<ulong>> dependencies = await ScanAsync(assets, asset, cancel)
                    .ConfigureAwait(false);
                if (!dependencies.Ok)
                    return Result.Failure(dependencies.Error);

                progress?.Report(new LoadProgress(work, ++scanned, scanned + level.Count + next.Count));
                foreach (ulong dependency in dependencies.Value)
                {
                    if (dependency != id && found.Add(dependency))
                        next.Add(dependency);
                }
            }

            level = next;
        }

        return Result.Success<ImmutableArray<ulong>>([.. found]);
    }

    /// <summary>
    /// The ids that depend on <paramref name="asset"/>, up to <paramref name="levels"/> levels, scanning every asset the
    /// first time it is asked; one that cannot be read is taken to depend on nothing.
    /// </summary>
    public static async Task<ImmutableArray<ulong>> FindDependentsAsync(
        Assets assets,
        ulong asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        // What each asset depends on, turned around: who depends on each.
        AssetDatabase database = assets.Database;
        AssetIndex index = database.Index;
        string work = $"Finding what depends on {database.GetName(asset)}";
        Dictionary<ulong, List<ulong>> dependents = [];
        int scanned = 0;
        foreach (ulong id in index.ById.Keys)
        {
            Result<ImmutableArray<ulong>> dependencies = await ScanAsync(assets, id, cancel).ConfigureAwait(false);
            progress?.Report(new LoadProgress(work, ++scanned, index.ById.Count));
            if (!dependencies.Ok)
                continue;

            foreach (ulong dependency in dependencies.Value)
            {
                if (!dependents.TryGetValue(dependency, out List<ulong>? of))
                {
                    of = [];
                    dependents.Add(dependency, of);
                }

                of.Add(id);
            }
        }

        HashSet<ulong> found = AssetDatabase.Walk(
            [asset],
            levels,
            id => dependents.TryGetValue(id, out List<ulong>? of) ? of : []
        );
        found.Remove(asset);

        return [.. found];
    }

    /// <summary>
    /// Loads each of <paramref name="ids"/> as the type its <c>.meta</c> names: their ids, or why any could not load.
    /// </summary>
    public static async Task<Result<ImmutableArray<ulong>>> LoadAllAsync(
        Assets assets,
        ImmutableArray<ulong> ids,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        Task<Result<object>>[] loading = new Task<Result<object>>[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            loading[i] = LoadAnyAsync(assets, ids[i], progress, cancel);
        }

        Result<object>[] loaded = await Task.WhenAll(loading).ConfigureAwait(false);
        List<string> errors = [];
        foreach (Result<object> one in loaded)
        {
            if (!one.Ok)
                errors.Add(one.Error);
        }

        return errors.Count == 0 ? Result.Success(ids) : Result.Failure(string.Join(" ", errors));
    }

    /// <summary>
    /// What asset <paramref name="id"/> depends on: from the database when it has been through it while its file is the
    /// same, else from it as it has loaded, else from its file read by Core for a type Core reads, else from the cache by
    /// its file's version, else from loading it; or why it could not be read.
    /// </summary>
    private static async Task<Result<ImmutableArray<ulong>>> ScanAsync(
        Assets assets,
        ulong id,
        CancellationToken cancel
    )
    {
        AssetDatabase database = assets.Database;
        if (database.HasDependencies(id))
            return Result.Success<ImmutableArray<ulong>>([.. database.GetDependencies(id, 1)]);

        if (!database.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!assets.Loaded.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        // Loaded, what it holds is there: one a gem added.
        LoadedAssets loaded = assets.Loaded;
        if (loaded.Entries.TryGetValue(id, out AssetEntry? entry) && entry.Current is { Value: { Ok: true } current })
            return Result.Success(AssetScan.FindHandles(current.Value));

        if (type.Type.GetCustomAttribute<AssetFormatAttribute>() is not null)
        {
            Result<byte[]> bytes = await assets.Files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
            if (!bytes.Ok)
                return Result.Failure($"{file.Path} could not be read: {bytes.Error}");

            Result<object> read = type.ReadBuiltIn(bytes.Value, file.Settings);
            if (!read.Ok)
                return Result.Failure(read.Error);

            ImmutableArray<ulong> dependencies = AssetScan.FindHandles(read.Value);
            assets.Changes.Push(new AssetDependenciesFound(id, dependencies));

            return Result.Success(dependencies);
        }

        // No gem the project lists reads it, so it names nothing that can be found.
        if (!loaded.Importers.ContainsKey(type.Type))
            return Result.Success<ImmutableArray<ulong>>([]);

        Result<AssetDependencies> kept = await ReadKeptAsync(assets, file, cancel).ConfigureAwait(false);
        if (kept.Ok)
        {
            assets.Changes.Push(new AssetDependenciesFound(id, kept.Value.Ids));

            return Result.Success(kept.Value.Ids);
        }

        Made made = await assets.RequestAsync(id, 0, type.Type, type.Import, null, cancel).ConfigureAwait(false);

        return made.Value.Ok ? Result.Success(made.Handles) : Result.Failure(made.Value.Error);
    }

    /// <summary>
    /// What <paramref name="file"/> came to depend on when it was last imported, while it is the same; why not otherwise.
    /// </summary>
    private static async Task<Result<AssetDependencies>> ReadKeptAsync(
        Assets assets,
        IndexedFile file,
        CancellationToken cancel
    )
    {
        Result<byte[]> bytes = await assets.Files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        ulong version = AssetIndex.ComputeVersion(bytes.Value, file.Settings);

        return await assets
            .Caches.ReadAsync(file.Id, version, AssetDependencyCache.Instance, cancel)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asset <paramref name="id"/>, loaded as the type its <c>.meta</c> names.
    /// </summary>
    private static async Task<Result<object>> LoadAnyAsync(
        Assets assets,
        ulong id,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        AssetDatabase database = assets.Database;
        if (!database.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!assets.Loaded.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        Made made = await assets.RequestAsync(id, 0, type.Type, type.Import, progress, cancel).ConfigureAwait(false);

        return made.Value;
    }
}
