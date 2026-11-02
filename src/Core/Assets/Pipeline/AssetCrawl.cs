using System.Collections.Immutable;
using System.Reflection;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// Walks what assets depend on, down or up to a number of levels, and loads what it finds: what
/// <see cref="AssetManager"/>'s dependency calls run. An asset's own handles are found by <see cref="AssetScan"/>.
/// </summary>
internal static class AssetCrawl
{
    /// <summary>
    /// The ids <paramref name="id"/> depends on, down to <paramref name="levels"/> levels, scanning each.
    /// </summary>
    public static async Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync(
        AssetManager assets,
        ulong id,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Finding what {AssetFold.GetName(assets.State.Index, id)} depends on";
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
        AssetManager assets,
        ulong asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        // What each asset depends on, turned around: who depends on each.
        AssetIndex index = assets.State.Index;
        string work = $"Finding what depends on {AssetFold.GetName(index, asset)}";
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

        HashSet<ulong> found = [];
        List<ulong> level = [asset];
        for (int depth = 0; depth < levels && level.Count > 0; depth++)
        {
            List<ulong> next = [];
            foreach (ulong id in level)
            {
                if (!dependents.TryGetValue(id, out List<ulong>? of))
                    continue;

                foreach (ulong dependent in of)
                {
                    if (dependent != asset && found.Add(dependent))
                        next.Add(dependent);
                }
            }

            level = next;
        }

        return [.. found];
    }

    /// <summary>
    /// Loads each of <paramref name="ids"/> as the type its <c>.meta</c> names: their ids, or why any could not load.
    /// </summary>
    public static async Task<Result<ImmutableArray<ulong>>> LoadAllAsync(
        AssetManager assets,
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
    /// The ids of the handles asset <paramref name="id"/> holds, found by reflection: from the last scan of it while its
    /// file is the same, else from it as it has loaded, else from its file read by Core for a type Core reads, else
    /// from loading it; or why it could not be read.
    /// </summary>
    private static async Task<Result<ImmutableArray<ulong>>> ScanAsync(
        AssetManager assets,
        ulong id,
        CancellationToken cancel
    )
    {
        AssetState state = assets.State;
        if (state.Scanned.TryGetValue(id, out ImmutableArray<ulong> scanned))
            return Result.Success(scanned);

        if (!state.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!state.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        Result<object> asset;
        if (state.Assets.TryGetValue(id, out AssetEntry? entry) && entry.Current is { } current)
        {
            asset = current.Value;
        }
        else if (type.Type.GetCustomAttribute<AssetFormatAttribute>() is not null)
        {
            Result<byte[]> bytes = await assets.Files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
            if (!bytes.Ok)
                return Result.Failure($"{file.Path} could not be read: {bytes.Error}");

            asset = type.ReadBuiltIn(bytes.Value, file.Settings);
        }
        else if (!state.Loaders.ContainsKey(type.Type))
        {
            // No gem the project lists reads it, so it names nothing that can be found.
            return Result.Success<ImmutableArray<ulong>>([]);
        }
        else
        {
            // TODO(Konfus): keep each gem-read asset's dependency ids in the disk cache, keyed by its file's version,
            // so finding them doesn't load it.
            asset = await LoadAnyAsync(assets, id, null, cancel).ConfigureAwait(false);
        }

        if (!asset.Ok)
            return Result.Failure(asset.Error);

        ImmutableArray<ulong> dependencies = AssetScan.FindHandles(asset.Value);
        assets.Post(new AssetDependenciesScanned(id, dependencies));

        return Result.Success(dependencies);
    }

    /// <summary>
    /// Asset <paramref name="id"/>, loaded as the type its <c>.meta</c> names.
    /// </summary>
    private static async Task<Result<object>> LoadAnyAsync(
        AssetManager assets,
        ulong id,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        AssetState state = assets.State;
        if (!state.Index.ById.TryGetValue(id, out IndexedFile? file))
            return Result.Failure($"No asset has the id {id}.");

        if (!state.Types.TryGetValue(file.Type, out AssetType? type))
            return Result.Failure(
                $"{file.Path} is a {file.Type}, which neither Magic nor a gem the project lists holds."
            );

        Made made = await assets.RequestAsync(id, 0, type.Type, type.Read, progress, cancel).ConfigureAwait(false);

        return made.Value;
    }
}
