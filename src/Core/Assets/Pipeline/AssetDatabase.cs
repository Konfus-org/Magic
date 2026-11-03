using System.Collections.Immutable;
using System.Reflection;
using Magic.Files;
using Magic.Pipelines;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Assets.Pipeline;

/// <summary>
/// The assets: what the files under the asset directories describe, the project's folder, then each gem's
/// <c>Resources/</c>, then Magic's built-in one, the first winning a path two of them have. A file is an asset when a
/// <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what a <see cref="Handle{T}"/> holds, and the
/// import settings its loader reads. An asset path is relative to its asset directory, with <c>/</c> between folders on
/// every OS. Any thread.
/// <para>
/// Every asset directory is indexed before the first gem is made, so finding an asset works in a constructor; a load
/// asked for before the first run of its pipeline, which comes once every gem is made, waits for it, as the gem that
/// reads its type may be made after the one asking.
/// </para>
/// <para>
/// Each file is loaded on a worker by the pipeline over an <see cref="AssetLoad{T}"/> of its type: read it, read its
/// <c>.meta</c>, look in the cache, make it, keep it in the cache, then whatever gems joined. It is made by the
/// <see cref="IAssetLoader{T}"/> a gem provides for its type, else by Core itself for a type marked
/// <see cref="AssetFormatAttribute"/>. What loaded is kept for the run.
/// </para>
/// <para>
/// The asset directories are watched until the run ends. Once their changes settle, they are indexed again,
/// and every asset loaded whose file changed, or that depends on one that did, is made again, each after its
/// dependencies: <see cref="GetVersion{T}"/> then says so, and a load gets the new one. One that fails to load again
/// keeps what loaded before.
/// </para>
/// <para>
/// What it knows is one snapshot, a value every call reads as it is. It changes in one place, the pipeline over
/// <see cref="AssetsChangeList"/>, which runs between frames when something was pushed there. So the assets hold still for
/// the whole frame, and a load lands, or an asset changes, only between frames.
/// </para>
/// </summary>
public sealed class AssetDatabase : IDisposable
{
    /// <summary>
    /// Every level of dependencies or dependents, however deep: what a crawl's <c>levels</c> takes for all of them.
    /// </summary>
    public const int AllLevels = int.MaxValue;

    private readonly PipelineCoordinator _pipelines;

    /// <summary>
    /// Ends the loads on their way and the watching, as the assets are disposed.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// What is known: written on the main thread alone, by the pipeline over its change list and before its
    /// first run.
    /// </summary>
    private AssetSnapshot _snapshot = AssetSnapshot.Empty;

    internal AssetDatabase(IFileSystem files, PipelineCoordinator pipelines)
    {
        Files = files;
        _pipelines = pipelines;
        Stopped = _stopping.Token;
        BuiltIn = AssetPipeline.GetResources(files, typeof(AssetDatabase).Assembly, AppContext.BaseDirectory);
    }

    /// <summary>
    /// What it knows as of now, as any thread reads it. The main thread alone writes it.
    /// </summary>
    internal AssetSnapshot Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }

    /// <summary>
    /// What is asked of the assets, as data: every change to them goes through here. Any thread.
    /// </summary>
    internal AssetsChangeList Changes { get; } = new();

    internal IFileSystem Files { get; }

    /// <summary>
    /// Magic's built-in <c>Resources/</c>: where the defaults and failures are, and only there.
    /// </summary>
    internal string BuiltIn { get; }

    /// <summary>
    /// <see cref="_stopping"/>'s token, kept so a load still on its way as the assets are disposed reads it.
    /// </summary>
    internal CancellationToken Stopped { get; }

    /// <summary>
    /// Stops the loads on their way and the watching: the host's to call, at the end of the run, on its thread. A load
    /// still waited for ends cancelled.
    /// </summary>
    public void Dispose()
    {
        Changes.Close(Stopped);
        _stopping.Cancel();
        foreach (AssetEntry entry in _snapshot.Assets.Values)
        {
            foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
            {
                waiter.TrySetCanceled(Stopped);
            }
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="path"/>, or none when no asset there is one.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T> =>
        Snapshot.Index.ByPath.TryGetValue(path, out IndexedFile? found) && found.Type == typeof(T).Name
            ? new Handle<T>(found.Id)
            : Handle<T>.None;

    /// <summary>
    /// <typeparamref name="T"/>'s default: the asset named Default, whatever its extension, in the folder named for the
    /// type, <c>Materials/Default.mat</c>, or <c>Domains/Default/Default.domain</c> for an asset that is a folder, in
    /// Magic's built-in <c>Resources/</c> alone: a project's or a gem's file at that path is an asset like any other.
    /// Every asset type has one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetDefault<T>()
        where T : IAsset<T> => FindBuiltIn<T>(typeof(T).Name + "s", "Default", "default");

    /// <summary>
    /// What is drawn in a broken <typeparamref name="TBroken"/>'s place: the <typeparamref name="T"/> named for the
    /// broken type in <typeparamref name="T"/>'s <c>Failures</c> folder, <c>Materials/Failures/Texture.mat</c> for a
    /// texture that does not load, in Magic's built-in <c>Resources/</c> alone, as a default is. Only what a renderer
    /// draws a failure as has one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetFailure<T, TBroken>()
        where T : IAsset<T>
        where TBroken : IAsset<TBroken> =>
        FindBuiltIn<T>($"{typeof(T).Name}s/Failures", typeof(TBroken).Name, $"{typeof(TBroken).Name} failure");

    /// <summary>
    /// The paths of the assets directly in <paramref name="folder"/>, as <see cref="Find{T}"/> takes them: a domain's
    /// prefabs.
    /// </summary>
    public ImmutableArray<string> GetPaths(string folder) =>
        Snapshot.Index.ByFolder.TryGetValue(folder, out ImmutableArray<string> paths) ? paths : [];

    /// <summary>
    /// <see cref="GetPaths(string)"/>, as an <see cref="IAssetLoader{T}"/> reads it while making asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), which is made again when a file in the folder changes.
    /// </summary>
    public ImmutableArray<string> GetPaths(string folder, ulong dependent)
    {
        Changes.Push(new AssetFolderListed(dependent, folder));

        return GetPaths(folder);
    }

    /// <summary>
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file has its id.
    /// </summary>
    public string? GetPath<T>(Handle<T> asset)
        where T : IAsset<T> => Snapshot.Index.ById.TryGetValue(asset.Id, out IndexedFile? found) ? found.Path : null;

    /// <summary>
    /// Which make of <paramref name="asset"/> a load gets: for one read from a file, a hash of the file and its
    /// <c>.meta</c>, which changes when either does; for one added, a count of the times it was. Compare it with the
    /// version an asset was loaded at to know whether to load it again. 0 when no file and nothing added has it.
    /// </summary>
    public ulong GetVersion<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        AssetSnapshot snapshot = Snapshot;
        if (snapshot.Assets.TryGetValue(asset.Id, out AssetEntry? entry) && entry.Current is { } made)
            return made.Version;

        if (!snapshot.Index.ById.TryGetValue(asset.Id, out IndexedFile? file))
            return 0;

        Result<byte[]> bytes = Files.ReadBytes(file.File);

        return bytes.Ok ? AssetIndex.ComputeVersion(bytes.Value, file.Settings) : 0;
    }

    /// <summary>
    /// The ids of the assets <paramref name="asset"/>'s loader loaded as it made it last, naming it as the dependent:
    /// when one of them is made again, so is it, after it. None for an asset made from its file alone, and for one not
    /// loaded. What its file names, loaded or not, is <see cref="FindDependenciesAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependencies<T>(Handle<T> asset)
        where T : IAsset<T> => Snapshot.Assets.TryGetValue(asset.Id, out AssetEntry? entry) ? entry.Dependencies : [];

    /// <summary>
    /// The asset, or why it could not be loaded. It is read on a worker once every gem is made, and kept, so every
    /// caller gets the same object; one asking while it is made again waits for what it is made into. A load that
    /// fails is remembered until its file changes. <paramref name="cancel"/> only stops this caller's wait.
    /// </summary>
    public Task<Result<T>> LoadAsync<T>(Handle<T> asset, IProgress<LoadProgress>? progress, CancellationToken cancel)
        where T : IAsset<T> =>
        asset == Handle<T>.None
            ? Task.FromResult<Result<T>>(Result.Failure($"No {typeof(T).Name} was asked for: the handle is none."))
            : LoadIdAsync<T>(asset.Id, 0, progress, cancel);

    /// <summary>
    /// <paramref name="asset"/>, loaded by an <see cref="IAssetLoader{T}"/> as it makes asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), as the other overload loads it, and recorded as what
    /// <paramref name="dependent"/> depends on, so it is made again when <paramref name="asset"/> changes. Refused when
    /// <paramref name="asset"/> is <paramref name="dependent"/> or depends on it, as each would wait for the other for
    /// ever.
    /// </summary>
    public Task<Result<T>> LoadAsync<T>(
        Handle<T> asset,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T> =>
        asset == Handle<T>.None
            ? LoadAsync(asset, progress, cancel)
            : LoadIdAsync<T>(asset.Id, dependent, progress, cancel);

    /// <summary>
    /// The ids of what <paramref name="asset"/> depends on, as its file and theirs name them, down to
    /// <paramref name="levels"/> levels: 1 for what it names itself, <see cref="AllLevels"/> for everything under it.
    /// Found by reading the file of a type Core reads, and by loading an asset of a type a gem reads; or why one could not
    /// be read.
    /// </summary>
    public Task<Result<ImmutableArray<ulong>>> FindDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependenciesAsync(this, asset.Id, levels, progress, cancel);

    /// <summary>
    /// The ids of what depends on <paramref name="asset"/>, as the files under the asset directories name it, up to
    /// <paramref name="levels"/> levels: 1 for what names it itself, <see cref="AllLevels"/> for everything above it.
    /// Found the first time it is asked by going through every asset: reading the file of a type Core reads, and loading
    /// an asset of a type a gem reads. One that cannot be read is taken to depend on nothing.
    /// </summary>
    public Task<ImmutableArray<ulong>> FindDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset> => AssetCrawl.FindDependentsAsync(this, asset.Id, levels, progress, cancel);

    /// <summary>
    /// Loads what <paramref name="asset"/> depends on, down to <paramref name="levels"/> levels, as
    /// <see cref="FindDependenciesAsync{TAsset}"/> finds it, each as the type its <c>.meta</c> names: their ids, or why
    /// one could not be found or loaded.
    /// </summary>
    public async Task<Result<ImmutableArray<ulong>>> LoadDependenciesAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset>
    {
        Result<ImmutableArray<ulong>> found = await FindDependenciesAsync(asset, levels, progress, cancel)
            .ConfigureAwait(false);

        return found.Ok
            ? await AssetCrawl.LoadAllAsync(this, found.Value, progress, cancel).ConfigureAwait(false)
            : found;
    }

    /// <summary>
    /// Loads what depends on <paramref name="asset"/>, up to <paramref name="levels"/> levels, as
    /// <see cref="FindDependentsAsync{TAsset}"/> finds it, each as the type its <c>.meta</c> names: their ids, or why
    /// one could not be loaded.
    /// </summary>
    public async Task<Result<ImmutableArray<ulong>>> LoadDependentsAsync<TAsset>(
        Handle<TAsset> asset,
        int levels,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where TAsset : IAsset<TAsset>
    {
        ImmutableArray<ulong> found = await FindDependentsAsync(asset, levels, progress, cancel).ConfigureAwait(false);

        return await AssetCrawl.LoadAllAsync(this, found, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Has <paramref name="asset"/> loaded, for whoever asks for it next, without waiting for it, as
    /// <see cref="LoadAsync{T}(Handle{T}, IProgress{LoadProgress}, CancellationToken)"/> does.
    /// </summary>
    public void Preload<T>(Handle<T> asset, IProgress<LoadProgress>? progress)
        where T : IAsset<T>
    {
        if (asset != Handle<T>.None)
            Changes.RequestAsync(asset.Id, 0, typeof(T), RunLoadAsync<T>, progress).FireAndForget();
    }

    /// <summary>
    /// Hands out <paramref name="asset"/>, one a gem made rather than read from a file, under <paramref name="handle"/>,
    /// which the gem derives from what it made it from, so that making it again gives the same one: loading the handle
    /// gets it. Adding another under it replaces it, as a reload does. A handle a file has is an error, and the asset is
    /// not added.
    /// </summary>
    public void Add<T>(Handle<T> handle, T asset)
        where T : IAsset<T>
    {
        if (handle == Handle<T>.None)
            throw new ArgumentException($"A {typeof(T).Name} is added with no handle.", nameof(handle));

        ArgumentNullException.ThrowIfNull(asset);
        Changes.Push(new AssetAdded(handle.Id, typeof(T), RunLoadAsync<T>, asset));
    }

    /// <summary>
    /// Has <paramref name="gem"/> read each asset type it implements an <see cref="IAssetLoader{T}"/> of, until the
    /// subscription is disposed, as the gem ends. A type another gem reads already stays that one's, and is an error.
    /// </summary>
    internal Subscription AddLoader(object gem)
    {
        List<Type> reads = [];
        foreach (Type contract in gem.GetType().GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAssetLoader<>))
                reads.Add(contract.GetGenericArguments()[0]);
        }

        foreach (Type type in reads)
        {
            Changes.Push(new AssetLoaderAdded(type, gem));
        }

        return new Subscription(() =>
        {
            foreach (Type type in reads)
            {
                Changes.Push(new AssetLoaderRemoved(type, gem));
            }
        });
    }

    /// <summary>
    /// Whether something reads <typeparamref name="T"/>'s files: Core itself, or a gem. Known once its first run has come.
    /// </summary>
    internal bool CanRead<T>()
        where T : IAsset<T> =>
        typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not null || Snapshot.Loaders.ContainsKey(typeof(T));

    /// <summary>
    /// Asset <paramref name="id"/> as <paramref name="type"/>, at once when it has loaded, else once its
    /// pipeline has it loaded; asked for by asset <paramref name="dependent"/>'s loader when that is not 0.
    /// </summary>
    internal async Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetReader read,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        AssetSnapshot snapshot = Snapshot;
        string work = $"Loading {snapshot.GetName(id)}";
        progress?.Report(new LoadProgress(work, 0, 1));

        // Loaded already, it needs no run, but a dependency is recorded in one.
        Made made;
        if (
            dependent == 0
            && snapshot.Assets.TryGetValue(id, out AssetEntry? entry)
            && entry.Stage == AssetStage.Loaded
            && entry.Type == type
        )
        {
            made = entry.Outcome;
        }
        else
        {
            made = await Changes
                .RequestAsync(id, dependent, type, read, progress)
                .WaitAsync(cancel)
                .ConfigureAwait(false);
        }

        progress?.Report(new LoadProgress(work, 1, 1));

        return made;
    }

    /// <summary>
    /// Runs the pipeline that loads asset <paramref name="id"/> as a <typeparamref name="T"/>, and says what it made.
    /// </summary>
    internal async Task<Made> RunLoadAsync<T>(
        ulong id,
        IndexedFile file,
        object? loader,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        AssetLoad<T> load = new()
        {
            Id = id,
            Path = file.Path,
            File = file.File,
            Settings = file.Settings,
            Loader = loader as IAssetLoader<T>,
            Progress = progress,
        };
        AssetLoad<T> done = await _pipelines.Get<AssetLoad<T>>().RunAsync(load, cancel).ConfigureAwait(false);

        return done.Asset is { Ok: true, Value: { } asset }
            ? new Made(Result.Success<object>(asset), done.Version, AssetScan.FindHandles(asset))
            : new Made(Result.Failure(done.Asset?.Error ?? $"{file.Path} made nothing."), done.Version);
    }

    /// <summary>
    /// Asset <paramref name="id"/>, at once when it has loaded, else once its pipeline has it loaded; asked for by asset
    /// <paramref name="dependent"/>'s loader when that is not 0.
    /// </summary>
    private async Task<Result<T>> LoadIdAsync<T>(
        ulong id,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        Made made = await RequestAsync(id, dependent, typeof(T), RunLoadAsync<T>, progress, cancel)
            .ConfigureAwait(false);

        return made.Value.Ok ? Result.Success((T)made.Value.Value) : Result.Failure(made.Value.Error);
    }

    /// <summary>
    /// The built-in asset named <paramref name="name"/> in <paramref name="folder"/>, as
    /// <see cref="AssetIndex.FindBuiltIn"/> finds it; <paramref name="what"/> says which kind of named asset it is when
    /// it is missing, which its pipeline says once.
    /// </summary>
    private Handle<T> FindBuiltIn<T>(string folder, string name, string what)
        where T : IAsset<T>
    {
        if (Snapshot.Index.FindBuiltIn(folder, name) is { } found && found.Type == typeof(T).Name)
            return new Handle<T>(found.Id);

        Changes.Push(
            new BuiltInAssetMissing(
                $"{folder}/{name}",
                $"There is no {what} {typeof(T).Name}: no {typeof(T).Name} is {folder}/{name} or {folder}/{name}/{name} "
                    + $"under {BuiltIn}."
            )
        );

        return Handle<T>.None;
    }
}
