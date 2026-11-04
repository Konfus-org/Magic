using System.Collections.Immutable;
using System.IO.Hashing;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Magic.Caching;
using Magic.Debugging;
using Magic.Files;
using Magic.Pipes;
using Magic.Projects;
using Magic.Serialization;
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
/// Every asset directory is registered (<see cref="Register(string)"/>) and indexed (<see cref="Index"/>) before the
/// first gem is made, so finding an asset works in a constructor; a load asked for before the first run of its pipeline,
/// which comes once every gem is made, waits for it, as the gem that reads its type may be made after the one asking.
/// </para>
/// <para>
/// Each file is loaded on a worker by the pipeline over an <see cref="AssetLoad{T}"/> of its type: read it, read its
/// <c>.meta</c>, look in the cache, make it, keep it in the cache, then whatever gems joined. It is made by the
/// <see cref="IAssetLoader{T}"/> a gem provides for its type, else by Core itself for a type marked
/// <see cref="AssetFormatAttribute"/>. What loaded is kept for the run.
/// </para>
/// <para>
/// The asset directories are watched from the index until the database is disposed. Once their changes settle, they are
/// indexed again, and every asset loaded whose file changed, or that depends on one that did, is made again, each after
/// its dependencies: <see cref="GetVersion{T}"/> then says so, and a load gets the new one. One that fails to load again
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

    private const string Sidecar = ".meta";

    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    private readonly Pipelines _pipelines;
    private readonly Caches _caches;

    /// <summary>
    /// The asset directory the assets written through <see cref="WriteAsync{T}"/> go under: the project's, in its user
    /// cache.
    /// </summary>
    private readonly string _written;

    /// <summary>
    /// Ends the loads on their way and the watching, as the assets are disposed.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// What the database registered itself, for as long as it lasts: the project's and the kept folders, Core's own
    /// asset types, and Magic's built-in folder.
    /// </summary>
    private readonly Subscriptions _registered = [];

    /// <summary>
    /// What is known: written on the main thread alone, by the pipeline over its change list and before its
    /// first run.
    /// </summary>
    private AssetSnapshot _snapshot = AssetSnapshot.Empty;

    /// <summary>
    /// The assets of <paramref name="project"/>, with the project's folder first, then the folder the last run kept the
    /// world in, and Core's own asset types; a gem's come as it is found, and Magic's built-in folder last.
    /// </summary>
    internal AssetDatabase(IFileSystem files, Pipelines pipelines, Caches caches, Project project)
    {
        Files = files;
        _pipelines = pipelines;
        _caches = caches;
        Stopped = _stopping.Token;
        BuiltIn = GetResources(files, typeof(AssetDatabase).Assembly, AppContext.BaseDirectory);
        _written = caches.WrittenAssetsFolder;
        _registered.Add(Register(files.GetParent(project.Path)));
        _registered.Add(Register(_written));
        _registered.Add(Register(typeof(AssetDatabase).Assembly));
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
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded, its
    /// source <c>Resources/</c>, which every build but a release does; else <c>Resources/</c> in
    /// <paramref name="folder"/>, the assembly's, where a release copies them.
    /// </summary>
    public static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// Stops the loads on their way and the watching: the host's to call, at the end of the run, on its thread. A load
    /// still waited for ends cancelled.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        _registered.Dispose();
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
    /// The paths of the assets in <paramref name="folder"/> and every folder under it, as <see cref="Find{T}"/>
    /// takes them.
    /// </summary>
    public ImmutableArray<string> GetPathsUnder(string folder)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        foreach ((string indexed, ImmutableArray<string> files) in Snapshot.Index.ByFolder)
        {
            bool isUnder =
                indexed.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || indexed.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            if (isUnder)
                paths.AddRange(files);
        }

        return paths.ToImmutable();
    }

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
    /// Writes <paramref name="asset"/> as the asset at <paramref name="path"/>, an asset path under the project's cache,
    /// through the <see cref="IAssetWriter{T}"/> a gem provides for its type, with the sidecar beside it that makes it
    /// an asset, under an id the path decides, so the same path gives the same handle each time: that handle, or why
    /// it was not written. The path ends in the writer's extension. The asset is found once the directory is indexed
    /// again, or by the next run. Any thread.
    /// </summary>
    public async Task<Result<Handle<T>>> WriteAsync<T>(
        T asset,
        string path,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(path);
        if (Snapshot.Writers.GetValueOrDefault(typeof(T)) is not IAssetWriter<T> writer)
            return Result.Failure($"no gem the project lists writes {typeof(T).Name}s.");

        if (!path.EndsWith(writer.Extension, StringComparison.OrdinalIgnoreCase))
            return Result.Failure($"a {typeof(T).Name}'s file ends in {writer.Extension}, not {path}.");

        Result<ReadOnlyMemory<byte>> bytes = await writer.WriteAsync(asset, progress, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        string file = Files.Combine(_written, path);
        Result written = await Files.WriteAsync(file, bytes.Value, cancel).ConfigureAwait(false);
        if (!written.Ok)
            return Result.Failure(written.Error);

        ulong id = GetWrittenId(path);
        Result<JsonElement> sidecar = Json.Write(
            new Dictionary<string, object> { ["id"] = id, ["type"] = typeof(T).Name },
            typeof(Dictionary<string, object>)
        );
        if (!sidecar.Ok)
            return Result.Failure(sidecar.Error);

        Result sidecarWritten = await Files
            .WriteAsync(file + Sidecar, Encoding.UTF8.GetBytes(sidecar.Value.GetRawText()), cancel)
            .ConfigureAwait(false);

        return sidecarWritten.Ok ? Result.Success(new Handle<T>(id)) : Result.Failure(sidecarWritten.Error);
    }

    /// <summary>
    /// Adds <paramref name="directory"/> to the asset directories, after those added before it, which win a path it
    /// shares with them, until the subscription is disposed. One that does not exist, or is added already, is left out.
    /// Before <see cref="Index"/>.
    /// </summary>
    internal Subscription Register(string directory)
    {
        directory = directory.TrimEnd('/', '\\');
        if (!Files.DirectoryExists(directory))
            return new Subscription(static () => { });

        AssetSnapshot snapshot = Snapshot;
        foreach (string added in snapshot.Directories)
        {
            if (string.Equals(added, directory, StringComparison.OrdinalIgnoreCase))
                return new Subscription(static () => { });
        }

        Snapshot = snapshot with { Directories = snapshot.Directories.Add(directory) };

        return new Subscription(() =>
            Snapshot = Snapshot with { Directories = Snapshot.Directories.Remove(directory) }
        );
    }

    /// <summary>
    /// Adds the asset types <paramref name="assembly"/> declares, Magic's or a gem's, so a <c>.meta</c> can name them
    /// and the assets can be crawled without knowing them, each with Core's own stages of its load pipeline, until the
    /// subscription is disposed. A name another type has already stays that one's, and is an error.
    /// </summary>
    internal Subscription Register(Assembly assembly)
    {
        Subscriptions registered = [];
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || !IsAssetType(type))
                continue;

            AssetSnapshot snapshot = Snapshot;
            if (snapshot.Types.TryGetValue(type.Name, out AssetType? other))
            {
                Log.Error(
                    $"{type.FullName} and {other.Type.FullName} are both asset types named {type.Name}, so a .meta's "
                        + "\"type\" names the first."
                );
                continue;
            }

            if (
                GetGeneric(typeof(AssetDatabase), nameof(RegisterLoadStages), type).Invoke(this, null)
                is Subscription stages
            )
                registered.Add(stages);

            AssetReader read = GetGeneric(typeof(AssetDatabase), nameof(RunLoadAsync), type)
                .CreateDelegate<AssetReader>(this);
            AssetBuiltInReader readBuiltIn = GetGeneric(typeof(AssetDatabase), nameof(ReadBuiltIn), type)
                .CreateDelegate<AssetBuiltInReader>();
            Snapshot = snapshot with { Types = snapshot.Types.Add(type.Name, new AssetType(type, read, readBuiltIn)) };
            registered.Add(
                new Subscription(() => Snapshot = Snapshot with { Types = Snapshot.Types.Remove(type.Name) })
            );
        }

        return new Subscription(registered.Dispose);
    }

    /// <summary>
    /// Reads what is under every registered directory, Magic's built-in one last, so the others win a path they share
    /// with it, and watches them from then on, so a gem's constructor can find assets. Once, before the first gem is
    /// made.
    /// </summary>
    internal void Index()
    {
        _registered.Add(Register(BuiltIn));

        // Watched before they are read, so nothing written meanwhile is missed.
        AssetSnapshot snapshot = Snapshot;
        foreach (string directory in snapshot.Directories)
        {
            Watch(directory);
        }

        Snapshot = snapshot with { Index = AssetIndex.Create(Files, snapshot.Directories, BuiltIn) };
        Log.Info($"Indexed {Snapshot.Index.ById.Count} assets under {snapshot.Directories.Length} folders.");
    }

    /// <summary>
    /// Has <paramref name="gem"/> read each asset type it implements an <see cref="IAssetLoader{T}"/> of, and write each
    /// it implements an <see cref="IAssetWriter{T}"/> of, until the subscription is disposed, as the gem ends. A type
    /// another gem reads or writes already stays that one's, and is an error.
    /// </summary>
    internal Subscription Register(object gem)
    {
        List<Type> reads = [];
        List<Type> writes = [];
        foreach (Type contract in gem.GetType().GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;

            if (contract.GetGenericTypeDefinition() == typeof(IAssetLoader<>))
                reads.Add(contract.GetGenericArguments()[0]);
            else if (contract.GetGenericTypeDefinition() == typeof(IAssetWriter<>))
                writes.Add(contract.GetGenericArguments()[0]);
        }

        foreach (Type type in reads)
        {
            Changes.Push(new AssetLoaderAdded(type, gem));
        }

        foreach (Type type in writes)
        {
            Changes.Push(new AssetWriterAdded(type, gem));
        }

        return new Subscription(() =>
        {
            foreach (Type type in reads)
            {
                Changes.Push(new AssetLoaderRemoved(type, gem));
            }

            foreach (Type type in writes)
            {
                Changes.Push(new AssetWriterRemoved(type, gem));
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
    /// Whether a gem writes <typeparamref name="T"/>'s files. Known once its first run has come.
    /// </summary>
    internal bool CanWrite<T>()
        where T : IAsset<T> => Snapshot.Writers.ContainsKey(typeof(T));

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
        AssetLoad<T> done = await _pipelines.RunAsync(load, cancel).ConfigureAwait(false);

        return done.Asset is { Ok: true, Value: { } asset }
            ? new Made(Result.Success<object>(asset), done.Version, AssetScan.FindHandles(asset))
            : new Made(Result.Failure(done.Asset?.Error ?? $"{file.Path} made nothing."), done.Version);
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
    /// The id of an asset written at the asset path <paramref name="path"/>: the same for it each time, whatever the
    /// case, and never none.
    /// </summary>
    private static ulong GetWrittenId(string path) =>
        Math.Max(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(path.ToUpperInvariant())), 1);

    /// <summary>
    /// A generic method of <paramref name="owner"/>'s, <paramref name="name"/>, made for <paramref name="type"/>.
    /// </summary>
    private static MethodInfo GetGeneric(Type owner, string name, Type type) =>
        (
            owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.Name} has no method {name}.")
        ).MakeGenericMethod(type);

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

    /// <summary>
    /// Has Core's own stages make up the pipeline that loads a <typeparamref name="T"/>, ahead of any gem's, until the
    /// subscription is disposed.
    /// </summary>
    private Subscription RegisterLoadStages<T>()
        where T : IAsset<T>
    {
        Subscriptions stages =
        [
            _pipelines.Register(new ReadFilePipeline<T>(Files)),
            _pipelines.Register(new ReadMetadataPipeline<T>()),
            _pipelines.Register(new ReadCachePipeline<T>(_caches)),
            _pipelines.Register(new ImportPipeline<T>()),
            _pipelines.Register(new WriteCachePipeline<T>(_caches)),
        ];

        return new Subscription(stages.Dispose);
    }

    private void Watch(string directory)
    {
        Result<IFileWatcher> watched = Files.Watch(directory);
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

        Changes.PushTouched(path);
    }
}
