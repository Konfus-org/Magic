using System.Reflection;
using System.Text.Json;
using Magic.Caching;
using Magic.Debugging;
using Magic.Files;
using Magic.Gems;
using Magic.Pipes;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The assets' side of a run: finding what is under the asset directories before any gem is made, watching them, and between
/// frames taking in what the assets were asked and what finished, when there is any. Between frames rather than at the
/// deferred point, so the assets hold still for the frame, and go on while no frames run.
/// </summary>
internal sealed class AssetPipeline(AssetDatabase assets, IFileSystem files, Caches caches, Pipelines pipelines)
    : IPipeline<AppStart>,
        IPipeline<GemsFound>,
        IPipeline<ServicePump>,
        IPipeline<AssetsChangeList>,
        IPipeline<AppEnd>
{
    private const string Sidecar = ".meta";

    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded, its
    /// source <c>Resources/</c>, which every build but a release does; else <c>Resources/</c> in
    /// <paramref name="folder"/>, the assembly's, where a release copies them.
    /// </summary>
    public static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// The project's folder comes first, as the first winning a path two directories have, then what the last run kept.
    /// </summary>
    public ValueTask<AppStart> RunAsync(AppStart state, CancellationToken cancel)
    {
        AddAssetDirectory(files.GetParent(state.Project.Path));
        AddAssetDirectory(caches.KeptFolder);
        AddAssetTypes(typeof(AssetDatabase).Assembly);

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Each gem's <c>Resources/</c> and asset types, then Magic's built-in folder, are indexed and from then watched, so a
    /// gem's constructor can find assets.
    /// </summary>
    public ValueTask<GemsFound> RunAsync(GemsFound state, CancellationToken cancel)
    {
        foreach (GemInfo gem in state.Registry.Gems)
        {
            if (gem.Resources is not null)
                AddAssetDirectory(gem.Resources);

            AddAssetTypes(gem.Type.Assembly);
        }

        AddAssetDirectory(assets.BuiltIn);

        // Watched before they are read, so nothing written meanwhile is missed.
        AssetSnapshot snapshot = assets.Snapshot;
        foreach (string directory in snapshot.Directories)
        {
            Watch(directory);
        }

        assets.Snapshot = snapshot with { Index = AssetIndex.Create(files, snapshot.Directories, assets.BuiltIn) };
        Log.Info($"Indexed {assets.Snapshot.Index.ById.Count} assets under {snapshot.Directories.Length} folders.");

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// Takes in what changed, in one go, so what is read holds still until it is all in. Waiters are told last, so the
    /// snapshot they read has what they waited for.
    /// </summary>
    public ValueTask<AssetsChangeList> RunAsync(AssetsChangeList state, CancellationToken cancel)
    {
        AssetSnapshot snapshot = AssetChanges.Take(assets.Snapshot, state);
        snapshot = AssetReindex.Take(snapshot, state, assets);
        snapshot = AssetLoads.Land(snapshot, state);
        assets.Snapshot = AssetLoads.Start(snapshot, state, assets.Stopped);
        state.TellWaiters();

        return ValueTask.FromResult(state);
    }

    public ValueTask<ServicePump> RunAsync(ServicePump state, CancellationToken cancel)
    {
        AssetsChangeList changes = assets.Changes;
        AssetSnapshot snapshot = assets.Snapshot;
        if (changes.HasChanges || !snapshot.IsLoadingEnabled || !snapshot.Touched.IsEmpty)
        {
            pipelines.Get<AssetsChangeList>().Run(changes, cancel);
            changes.EndRun();
        }

        return ValueTask.FromResult(state);
    }

    public ValueTask<AppEnd> RunAsync(AppEnd state, CancellationToken cancel)
    {
        _watches.Dispose();

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// <paramref name="bytes"/> read as a <typeparamref name="T"/> by Core itself, with its <c>.meta</c>'s
    /// <paramref name="settings"/> read against the type first.
    /// </summary>
    private static Result<object> ReadBuiltIn<T>(ReadOnlyMemory<byte> bytes, JsonElement settings)
        where T : IAsset<T>
    {
        Result<AssetMetadata> metadata = ReadMetadataPipeline<T>.Read(settings);
        if (!metadata.Ok)
            return Result.Failure(metadata.Error);

        Result<T> read = ImportPipeline<T>.ReadBuiltIn(bytes, settings);

        return read.Ok ? Result.Success<object>(read.Value) : Result.Failure(read.Error);
    }

    /// <summary>
    /// Whether <paramref name="type"/> implements <see cref="IAsset{TSelf}"/> of itself.
    /// </summary>
    private static bool IsAssetType(Type type)
    {
        foreach (Type contract in type.GetInterfaces())
        {
            if (
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IAsset<>)
                && contract.GetGenericArguments()[0] == type
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A generic method of <paramref name="owner"/>'s, <paramref name="name"/>, made for <paramref name="type"/>.
    /// </summary>
    private static MethodInfo GetGeneric(Type owner, string name, Type type) =>
        (
            owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.Name} has no method {name}.")
        ).MakeGenericMethod(type);

    /// <summary>
    /// Adds <paramref name="directory"/> to the asset directories, after those added before it, which win a path it
    /// shares with them. One that does not exist, or is added already, is left out.
    /// </summary>
    private void AddAssetDirectory(string directory)
    {
        directory = directory.TrimEnd('/', '\\');
        if (!files.DirectoryExists(directory))
            return;

        AssetSnapshot snapshot = assets.Snapshot;
        foreach (string added in snapshot.Directories)
        {
            if (string.Equals(added, directory, StringComparison.OrdinalIgnoreCase))
                return;
        }

        assets.Snapshot = snapshot with { Directories = snapshot.Directories.Add(directory) };
    }

    /// <summary>
    /// Adds the asset types <paramref name="assembly"/> declares, Magic's or a gem's, so a <c>.meta</c> can name them
    /// and the assets can be crawled without knowing them. A name another type has already stays that one's, and is an
    /// error.
    /// </summary>
    private void AddAssetTypes(Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || !IsAssetType(type))
                continue;

            AssetSnapshot snapshot = assets.Snapshot;
            if (snapshot.Types.TryGetValue(type.Name, out AssetType? other))
            {
                Log.Error(
                    $"{type.FullName} and {other.Type.FullName} are both asset types named {type.Name}, so a .meta's "
                        + "\"type\" names the first."
                );
                continue;
            }

            GetGeneric(typeof(AssetPipeline), nameof(JoinLoadPipelines), type).Invoke(this, null);
            AssetReader read = GetGeneric(typeof(AssetDatabase), nameof(AssetDatabase.RunLoadAsync), type)
                .CreateDelegate<AssetReader>(assets);
            AssetBuiltInReader readBuiltIn = GetGeneric(typeof(AssetPipeline), nameof(ReadBuiltIn), type)
                .CreateDelegate<AssetBuiltInReader>();
            assets.Snapshot = snapshot with
            {
                Types = snapshot.Types.Add(type.Name, new AssetType(type, read, readBuiltIn)),
            };
        }
    }

    /// <summary>
    /// Has Core's own join the pipeline that loads a <typeparamref name="T"/>, ahead of any gem's.
    /// </summary>
    private void JoinLoadPipelines<T>()
        where T : IAsset<T>
    {
        pipelines.Join(new ReadFilePipeline<T>(files));
        pipelines.Join(new ReadMetadataPipeline<T>());
        pipelines.Join(new ReadCachePipeline<T>(caches));
        pipelines.Join(new ImportPipeline<T>());
        pipelines.Join(new WriteCachePipeline<T>(caches));
    }

    private void Watch(string directory)
    {
        Result<IFileWatcher> watched = files.Watch(directory);
        if (!watched.Ok)
        {
            Log.Warn(
                $"The assets under {directory} are not watched, so a change there is not taken in: {watched.Error}"
            );

            return;
        }

        IFileWatcher watcher = watched.Value;
        watcher.Created += (_, e) => Touch(directory, e.FullPath);
        watcher.Changed += (_, e) => Touch(directory, e.FullPath);
        watcher.Deleted += (_, e) => Touch(directory, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            Touch(directory, e.OldFullPath);
            Touch(directory, e.FullPath);
        };
        _watches.Add(new Subscription(watcher.Dispose));
    }

    /// <summary>
    /// Hands what a watch saw at <paramref name="fullPath"/> under <paramref name="directory"/> to the change list, as a
    /// path from the asset directory, a sidecar's change being its file's.
    /// </summary>
    private void Touch(string directory, string fullPath)
    {
        if (fullPath.Length <= directory.Length)
            return;

        string path = fullPath[(directory.Length + 1)..].Replace('\\', '/');
        if (path.EndsWith(Sidecar, StringComparison.OrdinalIgnoreCase))
            path = path[..^Sidecar.Length];

        assets.Changes.PushTouched(path);
    }
}
