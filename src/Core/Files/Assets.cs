using System.Collections.Immutable;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magic.Caching;
using Magic.Debugging;
using Magic.Files.Pipeline;
using Magic.Serialization;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Files;

/// <summary>
/// The assets, as gems and gameplay use them: what the files under the asset directories describe, the project's folder,
/// then each gem's <c>Resources/</c>, then Magic's built-in one, the first winning a path two of them have. A file is an
/// asset when a <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what a <see cref="Handle{T}"/> holds,
/// and the import settings its importer reads. An asset path is relative to its asset directory, with <c>/</c> between
/// folders on every OS. Any thread.
/// <para>
/// Every asset directory is indexed as it is registered, a gem's before the gem is made, so finding an asset works in a
/// constructor; a load asked for before changes are first taken in,
/// which comes once every gem is made, waits for it, as the gem that reads its type may be made after the one asking.
/// </para>
/// <para>
/// Each file is imported on a worker by <see cref="RunImportAsync{T}"/>: read it, read its <c>.meta</c>, look in the
/// cache, make it, keep it in the cache. It is made by the <see cref="IAssetImporter{T}"/> a gem provides for its type,
/// else by Core itself for a type marked <see cref="AssetFormatAttribute"/>. What loaded is kept for the run.
/// </para>
/// <para>
/// The asset directories are watched from when they are registered until the assets are disposed. Once their changes settle, they are
/// indexed again, and every asset loaded whose file changed, or that was made from one that did, is made again, each after
/// what it was made from: <see cref="GetVersion{T}"/> then says so, and a load gets the new one. One that fails to load
/// again keeps what loaded before.
/// </para>
/// <para>
/// What assets there are and what depends on what is its <see cref="AssetDatabase"/>, what was loaded its
/// <see cref="LoadedAssets"/>: values every call reads as they are. They change in one place,
/// <see cref="ProcessChanges"/>, which runs between frames when something was pushed there. So the assets hold still for
/// the whole frame, and a load lands, or an asset changes, only between frames.
/// </para>
/// </summary>
public sealed class Assets : IDisposable
{
    /// <summary>
    /// Every level of dependencies or dependents, however deep: what a crawl's <c>levels</c> takes for all of them.
    /// </summary>
    public const int AllLevels = int.MaxValue;

    /// <summary>
    /// The assembly metadata a build records the folder of its part's assets in, where it does not copy them beside it
    /// (the root <c>Directory.Build.props</c>).
    /// </summary>
    private const string ResourcesKey = "Magic.Resources";

    private readonly AssetWatches _watches;

    /// <summary>
    /// Whose <see cref="App.Lifetime"/> ends the loads on their way.
    /// </summary>
    private readonly App _app;

    /// <summary>
    /// How many loads are on their way: written from any thread with <see cref="Interlocked"/>.
    /// </summary>
    private int _loadsInFlight;

    /// <summary>
    /// The assets, knowing Magic's built-in folder and Core's own asset types: register the rest (<see cref="Register(string)"/>).
    /// </summary>
    internal Assets(IFileSystem files, Caches caches, App app)
    {
        Files = files;
        Caches = caches;
        _app = app;
        _watches = new AssetWatches(files);
        BuiltIn = GetResources(files, typeof(Assets).Assembly, AppContext.BaseDirectory);
        Register(BuiltIn);
        Register(typeof(Assets).Assembly);
    }

    /// <summary>
    /// Whether a load is on its way, as a replace of the world waits for none to be. Any thread.
    /// </summary>
    internal bool HasLoadsInFlight => Volatile.Read(ref _loadsInFlight) > 0;

    /// <summary>
    /// What assets there are and what depends on what, as of now, as any thread reads it. The main thread alone writes it.
    /// </summary>
    internal AssetDatabase Database
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = AssetDatabase.Empty;

    /// <summary>
    /// What was asked for and made, as of now, as any thread reads it. The main thread alone writes it.
    /// </summary>
    internal LoadedAssets Loaded
    {
        get => Volatile.Read(ref field);
        private set => Volatile.Write(ref field, value);
    } = LoadedAssets.Empty;

    /// <summary>
    /// What is asked of the assets, as data: every change to them goes through here. Any thread.
    /// </summary>
    internal AssetsChangeList Changes { get; } = new();

    internal IFileSystem Files { get; }

    /// <summary>
    /// Where what is slow to make is kept.
    /// </summary>
    internal Caches Caches { get; }

    /// <summary>
    /// The assets that had loaded and loaded again as changes were last taken in.
    /// </summary>
    internal ImmutableArray<AssetLoaded> Reloaded { get; private set; } = [];

    /// <summary>
    /// Magic's built-in <c>Resources/</c>: where the defaults and failures are, and only there.
    /// </summary>
    internal string BuiltIn { get; }

    /// <summary>
    /// Where the part built as <paramref name="assembly"/> keeps its own assets: the folder its build recorded, its
    /// source <c>Resources/</c>, which every build but a release does; else <c>Resources/</c> in
    /// <paramref name="folder"/>, the assembly's, where a release copies them.
    /// </summary>
    public static string GetResources(IFileSystem files, Assembly assembly, string folder) =>
        assembly.GetMetadata(ResourcesKey) ?? files.Combine(folder, "Resources");

    /// <summary>
    /// Stops the watching: the host's to call, at the end of the run, on its thread. The loads on their way stopped with
    /// the run's <see cref="App.Lifetime"/>, and a load still waited for ends cancelled.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        Changes.Close(_app.Lifetime);
        foreach (AssetEntry entry in Loaded.Entries.Values)
        {
            foreach (TaskCompletionSource<Made> waiter in entry.Waiting)
            {
                waiter.TrySetCanceled(_app.Lifetime);
            }
        }
    }

    /// <summary>
    /// The <typeparamref name="T"/> at <paramref name="path"/>, or none when no asset there is one.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T> =>
        Database.Index.ByPath.TryGetValue(path, out IndexedFile? found) && found.Type == typeof(T).Name
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
        Database.Index.ByFolder.TryGetValue(folder, out ImmutableArray<string> paths) ? paths : [];

    /// <summary>
    /// The paths of the assets in <paramref name="folder"/> and every folder under it, as <see cref="Find{T}"/>
    /// takes them.
    /// </summary>
    public ImmutableArray<string> GetPathsUnder(string folder)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        foreach ((string indexed, ImmutableArray<string> files) in Database.Index.ByFolder)
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
    /// <see cref="GetPaths(string)"/>, as an <see cref="IAssetImporter{T}"/> reads it while making asset
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
        where T : IAsset<T> => Database.Index.ById.TryGetValue(asset.Id, out IndexedFile? found) ? found.Path : null;

    /// <summary>
    /// Which make of <paramref name="asset"/> a load gets: for one read from a file, a hash of the file and its
    /// <c>.meta</c>, which changes when either does; for one added, a count of the times it was. Compare it with the
    /// version an asset was loaded at to know whether to load it again. 0 when no file and nothing added has it.
    /// </summary>
    public ulong GetVersion<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        if (Loaded.Entries.TryGetValue(asset.Id, out AssetEntry? entry) && entry.Current is { } made)
            return made.Version;

        if (!Database.Index.ById.TryGetValue(asset.Id, out IndexedFile? file))
            return 0;

        Result<byte[]> bytes = Files.ReadBytes(file.File);

        return bytes.Ok ? AssetIndex.ComputeVersion(bytes.Value, file.Settings) : 0;
    }

    /// <summary>
    /// The ids of what <paramref name="asset"/> holds handles to or was made from, down to <paramref name="levels"/>
    /// levels: 1 for its own, <see cref="AllLevels"/> for everything under it. As far as is known now, from what has
    /// loaded or been found; reading what has not is <see cref="FindDependenciesAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependencies<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependencies(asset.Id, levels)];

    /// <summary>
    /// The ids of what holds handles to <paramref name="asset"/> or was made from it, up to <paramref name="levels"/>
    /// levels: 1 for what names it itself, <see cref="AllLevels"/> for everything above it. As far as is known now, from
    /// what has loaded or been found; reading what has not is <see cref="FindDependentsAsync{TAsset}"/>'s.
    /// </summary>
    public ImmutableArray<ulong> GetDependents<T>(Handle<T> asset, int levels)
        where T : IAsset<T> => [.. Database.GetDependents(asset.Id, levels)];

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
    /// <paramref name="asset"/>, loaded by an <see cref="IAssetImporter{T}"/> as it makes asset
    /// <paramref name="dependent"/> (<see cref="AssetFile.Id"/>), as the other overload loads it, and recorded as what
    /// <paramref name="dependent"/> is made from, so it is made again when <paramref name="asset"/> changes. Refused when
    /// <paramref name="asset"/> is <paramref name="dependent"/> or is made from it, as each would wait for the other for
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
    /// Found by reading the file of a type Core reads, and for a type a gem reads from what its last import kept, else by
    /// loading it; or why one could not be read.
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
    /// Found the first time it is asked by going through every asset: reading the file of a type Core reads, and for a
    /// type a gem reads from what its last import kept, else by loading it. One that cannot be read is taken to depend on
    /// nothing.
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
            CountInFlightAsync(Changes.RequestAsync(asset.Id, 0, typeof(T), RunImportAsync<T>, progress))
                .FireAndForget();
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
        Changes.Push(new AssetAdded(handle.Id, typeof(T), RunImportAsync<T>, asset));
    }

    /// <summary>
    /// Writes <paramref name="asset"/> as the asset at <paramref name="path"/>, an asset path under the project's folder,
    /// through the <see cref="IAssetExporter{T}"/> a gem provides for its type, with the sidecar beside it that makes it
    /// an asset, under an id the path decides, so the same path gives the same handle each time: that handle, or why
    /// it was not written. The path ends in the exporter's extension. The asset is found once the directory is indexed
    /// again, or by the next run. Any thread.
    /// </summary>
    public async Task<Result<Handle<T>>> SaveAsync<T>(
        T asset,
        string path,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        ArgumentNullException.ThrowIfNull(path);

        string file = Files.Combine(Database.Directories[0], path);
        Result written = await ExportAsync(asset, file, progress, cancel).ConfigureAwait(false);
        if (!written.Ok)
            return Result.Failure(written.Error);

        // The same whatever the path's case, and never none.
        ulong id = Math.Max(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(path.ToUpperInvariant())), 1);
        Result<JsonElement> sidecar = Json.Write(
            new Dictionary<string, object> { ["id"] = id, ["type"] = typeof(T).Name },
            typeof(Dictionary<string, object>)
        );
        if (!sidecar.Ok)
            return Result.Failure(sidecar.Error);

        byte[] sidecarBytes = Encoding.UTF8.GetBytes(sidecar.Value.GetRawText());
        Result sidecarWritten = await Files
            .WriteAsync(file + AssetIndex.Sidecar, sidecarBytes, cancel)
            .ConfigureAwait(false);

        return sidecarWritten.Ok ? Result.Success(new Handle<T>(id)) : Result.Failure(sidecarWritten.Error);
    }

    /// <summary>
    /// Writes <paramref name="asset"/> to <paramref name="file"/>, a path on the machine, through the
    /// <see cref="IAssetExporter{T}"/> a gem provides for its type, else by Core itself for a type marked
    /// <see cref="AssetFormatAttribute"/>, with no sidecar, so it is a file and not an asset: why not, when it was not
    /// written. The path ends in the exporter's extension. Any thread.
    /// </summary>
    public async Task<Result> ExportAsync<T>(
        T asset,
        string file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(file);

        Result<ReadOnlyMemory<byte>> bytes;
        if (Loaded.Exporters.GetValueOrDefault(typeof(T)) is IAssetExporter<T> exporter)
        {
            if (!file.EndsWith(exporter.Extension, StringComparison.OrdinalIgnoreCase))
                return Result.Failure($"a {typeof(T).Name}'s file ends in {exporter.Extension}, not {file}.");

            bytes = await exporter.ExportAsync(asset, progress, cancel).ConfigureAwait(false);
        }
        else if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is { } format)
        {
            bytes = await Task.Run(() => WriteCoreFormat(asset, format.Format), cancel).ConfigureAwait(false);
        }
        else
        {
            return Result.Failure($"no gem the project lists writes {typeof(T).Name}s.");
        }

        if (!bytes.Ok)
            return Result.Failure(bytes.Error);

        return await Files.WriteAsync(file, bytes.Value, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds <paramref name="directory"/> to the asset directories, after those added before it, which win a path it shares
    /// with them, and before Magic's built-in one, which all of them win over; and indexes them, so what it holds is found
    /// from now on, and watches it. One that does not exist, or is added already, is left out. Main thread.
    /// </summary>
    internal void Register(string directory)
    {
        directory = directory.TrimEnd('/', '\\');
        if (!Files.DirectoryExists(directory))
            return;

        AssetDatabase database = Database;
        foreach (string added in database.Directories)
        {
            if (string.Equals(added, directory, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Watched before it is read, so nothing written meanwhile is missed.
        _watches.Add(directory);
        ImmutableArray<string> directories = database.Directories.IsEmpty
            ? [directory]
            : database.Directories.Insert(database.Directories.Length - 1, directory);
        Database = database with { Directories = directories, Index = AssetIndex.Create(Files, directories, BuiltIn) };
        Log.Verbose($"Indexed {Database.Index.ById.Count} assets under {directories.Length} folders.");
    }

    /// <summary>
    /// Adds the asset types <paramref name="assembly"/> declares, Magic's or a gem's, so a <c>.meta</c> can name them and the
    /// assets can be crawled without knowing them. A name another type has already stays that one's, and is an error. Main
    /// thread.
    /// </summary>
    internal void Register(Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (!type.IsClass || type.IsAbstract || !IsAssetType(type))
                continue;

            LoadedAssets loaded = Loaded;
            if (loaded.Types.TryGetValue(type.Name, out AssetType? other))
            {
                Log.Error(
                    $"{type.FullName} and {other.Type.FullName} are both asset types named {type.Name}, so a .meta's "
                        + "\"type\" names the first."
                );
                continue;
            }

            AssetImportRun import = GetGeneric(nameof(RunImportAsync), type).CreateDelegate<AssetImportRun>(this);
            AssetBuiltInReader readBuiltIn = GetGeneric(nameof(ReadBuiltIn), type).CreateDelegate<AssetBuiltInReader>();
            Loaded = loaded with { Types = loaded.Types.Add(type.Name, new AssetType(type, import, readBuiltIn)) };
        }
    }

    /// <summary>
    /// Has <paramref name="gem"/> read each asset type it implements an <see cref="IAssetImporter{T}"/> of, and write each
    /// it implements an <see cref="IAssetExporter{T}"/> of, until the subscription is disposed, as the gem ends. A type
    /// another gem reads or writes already stays that one's, and is an error.
    /// </summary>
    internal Subscription Register(object gem)
    {
        List<AssetChange> removals = [];
        foreach (Type contract in gem.GetType().GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;

            Type definition = contract.GetGenericTypeDefinition();
            Type type = contract.GenericTypeArguments[0];
            if (definition == typeof(IAssetImporter<>))
            {
                Changes.Push(new AssetImporterAdded(type, gem));
                removals.Add(new AssetImporterRemoved(type, gem));
            }
            else if (definition == typeof(IAssetExporter<>))
            {
                Changes.Push(new AssetExporterAdded(type, gem));
                removals.Add(new AssetExporterRemoved(type, gem));
            }
        }

        return new Subscription(() => removals.ForEach(Changes.Push));
    }

    /// <summary>
    /// Takes in what changed, in one go, so what is read holds still until it is all in. Waiters are told last, so what
    /// they read has what they waited for. Between frames, so the assets hold still for the frame. Main thread.
    /// </summary>
    internal void ProcessChanges()
    {
        if (!Changes.HasChanges && !_watches.HasWork)
        {
            Reloaded = [];

            return;
        }

        (AssetDatabase database, LoadedAssets loaded) = AssetChanges.Take(Database, Loaded, Changes);
        CancellationToken lifetime = _app.Lifetime;
        (database, loaded) = AssetReindex.Take(database, loaded, _watches, Changes, BuiltIn, lifetime);
        (database, loaded) = AssetLoads.Land(database, loaded, Changes, Caches, lifetime);
        loaded = AssetLoads.Start(database, loaded, Changes, lifetime);
        Database = database;
        Loaded = loaded;
        Changes.TellWaiters();
        Reloaded = Changes.TakeReloaded();
    }

    /// <summary>
    /// Whether something reads <typeparamref name="T"/>'s files: Core itself, or a gem. Known once changes are first taken
    /// in.
    /// </summary>
    internal bool CanImport<T>()
        where T : IAsset<T> =>
        typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not null || Loaded.Importers.ContainsKey(typeof(T));

    /// <summary>
    /// Asset <paramref name="id"/> as <paramref name="type"/>, at once when it has loaded, else once it has been
    /// imported; asked for by asset <paramref name="dependent"/>'s importer when that is not 0.
    /// </summary>
    internal async Task<Made> RequestAsync(
        ulong id,
        ulong dependent,
        Type type,
        AssetImportRun import,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        string work = $"Loading {Database.GetName(id)}";
        progress?.Report(new LoadProgress(work, 0, 1));

        // Loaded already, it needs no run, but what it was made from is recorded in one.
        Made made;
        if (
            dependent == 0
            && Loaded.Entries.TryGetValue(id, out AssetEntry? entry)
            && entry.Stage == AssetStage.Loaded
            && entry.Type == type
        )
        {
            made = entry.Outcome;
        }
        else
        {
            made = await CountInFlightAsync(Changes.RequestAsync(id, dependent, type, import, progress))
                .WaitAsync(cancel)
                .ConfigureAwait(false);
        }

        progress?.Report(new LoadProgress(work, 1, 1));

        return made;
    }

    /// <summary>
    /// Imports asset <paramref name="id"/> as a <typeparamref name="T"/> on a worker, for several assets at once, and says
    /// what it made: reads the file and its <c>.meta</c>, looks in the cache, makes the asset, and keeps what it made in the
    /// cache. It is made by the gem that reads its type, else by Core itself for a type marked
    /// <see cref="AssetFormatAttribute"/>.
    /// </summary>
    internal async Task<Made> RunImportAsync<T>(
        ulong id,
        IndexedFile file,
        object? importer,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        string notAsset = $"{file.Path} is not a {typeof(T).Name}: ";
        IAssetImporter<T>? reader = importer as IAssetImporter<T>;

        // A file with an extension the type's importer doesn't take is refused before it is read.
        if (
            reader is not null
            && !reader.Extensions.Any(extension => file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        )
        {
            string extensions = string.Join(", ", reader.Extensions);

            return new Made(Result.Failure($"{notAsset}a {typeof(T).Name}'s file ends in {extensions}."), 0);
        }

        Result<byte[]> bytes = await Files.ReadBytesAsync(file.File, cancel).ConfigureAwait(false);
        if (!bytes.Ok)
            return new Made(Result.Failure($"{file.Path} could not be read: {bytes.Error}"), 0);

        ulong version = AssetIndex.ComputeVersion(bytes.Value, file.Settings);
        Result<AssetMetadata> metadata = ReadSettings<T>(file.Settings);
        if (!metadata.Ok)
            return new Made(Result.Failure(notAsset + metadata.Error), version);

        // What was made of the file before, by its id and version, when its importer is also a cache.
        ICache<T>? cache = reader as ICache<T>;
        if (cache is not null)
        {
            Result<T> cached = await Caches.ReadAsync(id, version, cache, cancel).ConfigureAwait(false);
            if (cached.Ok)
                return new Made(Result.Success<object>(cached.Value), version, AssetScan.FindHandles(cached.Value));
        }

        Result<T> read = reader is not null
            ? await reader
                .ImportAsync(new AssetFile(id, file.Path, bytes.Value, metadata.Value, version), progress, cancel)
                .ConfigureAwait(false)
            : ReadCoreFormat<T>(bytes.Value, file.Settings);
        if (!read.Ok)
            return new Made(Result.Failure(notAsset + read.Error), version);

        if (cache is not null)
            await Caches.WriteAsync(id, version, read.Value, cache, cancel).ConfigureAwait(false);

        return new Made(Result.Success<object>(read.Value), version, AssetScan.FindHandles(read.Value));
    }

    /// <summary>
    /// <paramref name="bytes"/> read as a <typeparamref name="T"/> by Core itself, with its <c>.meta</c>'s
    /// <paramref name="settings"/> read against the type first.
    /// </summary>
    private static Result<object> ReadBuiltIn<T>(ReadOnlyMemory<byte> bytes, JsonElement settings)
        where T : IAsset<T>
    {
        Result<AssetMetadata> metadata = ReadSettings<T>(settings);
        if (!metadata.Ok)
            return Result.Failure(metadata.Error);

        Result<T> read = ReadCoreFormat<T>(bytes, settings);

        return read.Ok ? Result.Success<object>(read.Value) : Result.Failure(read.Error);
    }

    /// <summary>
    /// <paramref name="settings"/>, a <c>.meta</c>'s, read as the values of <typeparamref name="T"/>'s
    /// <see cref="AssetMetaAttribute"/> properties, each in the type of its property, one it leaves out its
    /// initializer; or why they are not: a key no marked property has, or a value not of its property's type.
    /// </summary>
    private static Result<AssetMetadata> ReadSettings<T>(JsonElement settings)
        where T : IAsset<T>
    {
        // A type made with its initializers is where the defaults are; one with no parameterless constructor has none.
        object? defaults = typeof(T).GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance<T>();
        Dictionary<string, PropertyInfo> byKey = new(StringComparer.Ordinal);
        ImmutableDictionary<string, object?>.Builder values = ImmutableDictionary.CreateBuilder<string, object?>(
            StringComparer.Ordinal
        );
        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<AssetMetaAttribute>() is null)
                continue;

            byKey.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name), property);
            values[property.Name] =
                defaults is not null ? property.GetValue(defaults)
                : property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType)
                : null;
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            if (!byKey.TryGetValue(setting.Name, out PropertyInfo? property))
            {
                string takes = byKey.Count == 0 ? "none" : string.Join(", ", byKey.Keys);
                return Result.Failure(
                    $"its .meta sets \"{setting.Name}\", which a {typeof(T).Name} does not take; it takes {takes}."
                );
            }

            Result<object> value = Json.Read(setting.Value, property.PropertyType);
            if (!value.Ok)
                return Result.Failure(
                    $"its .meta's \"{setting.Name}\" is not a {property.PropertyType.Name}: {value.Error}"
                );

            values[property.Name] = value.Value;
        }

        return Result.Success(new AssetMetadata(values.ToImmutable()));
    }

    /// <summary>
    /// <paramref name="bytes"/> read by Core itself, in the <see cref="AssetFileFormat"/> its type is marked with, with
    /// what its <c>.meta</c> sets, <paramref name="settings"/>, already read against the type's
    /// <see cref="AssetMetaAttribute"/> properties, set over it; else no gem the project lists reads the type.
    /// </summary>
    private static Result<T> ReadCoreFormat<T>(ReadOnlyMemory<byte> bytes, JsonElement settings)
        where T : IAsset<T>
    {
        if (typeof(T).GetCustomAttribute<AssetFormatAttribute>() is not { } format)
            return Result.Failure($"no gem the project lists reads {typeof(T).Name}s.");

        if (format.Format == AssetFileFormat.Json && settings.GetPropertyCount() == 0)
            return Json.Read<T>(bytes.Span);

        JsonObject json;
        if (format.Format == AssetFileFormat.Json)
        {
            Result<JsonObject> read = Json.Read<JsonObject>(bytes.Span);
            if (!read.Ok)
                return Result.Failure(read.Error);

            json = read.Value;
        }
        else
        {
            ReadOnlySpan<byte> text = bytes.Span;
            if (text.StartsWith(Encoding.UTF8.Preamble))
                text = text[Encoding.UTF8.Preamble.Length..];

            json = new JsonObject { ["text"] = Encoding.UTF8.GetString(text) };
        }

        foreach (JsonProperty setting in settings.EnumerateObject())
        {
            json[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());
        }

        Result<JsonElement> element = Json.Write(json, typeof(JsonObject));

        return element.Ok ? Json.Read<T>(element.Value) : Result.Failure(element.Error);
    }

    /// <summary>
    /// <paramref name="asset"/> as Core writes its type itself, in the <see cref="AssetFileFormat"/> it is marked with: the
    /// file <see cref="ReadCoreFormat{T}"/> reads back, without its <see cref="AssetMetaAttribute"/> properties, which its
    /// <c>.meta</c> holds. Why not, when JSON can't hold it.
    /// </summary>
    private static Result<ReadOnlyMemory<byte>> WriteCoreFormat<T>(T asset, AssetFileFormat format)
    {
        Result<JsonElement> written = Json.Write(asset, typeof(T));
        if (!written.Ok)
            return Result.Failure(written.Error);

        if (JsonObject.Create(written.Value) is not { } json)
            return Result.Failure($"a {typeof(T).Name} is not a JSON object.");

        foreach (PropertyInfo property in typeof(T).GetProperties())
        {
            if (property.GetCustomAttribute<AssetMetaAttribute>() is not null)
                json.Remove(JsonNamingPolicy.CamelCase.ConvertName(property.Name));
        }

        if (format == AssetFileFormat.Json)
            return Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(json.ToJsonString(Json.Options)));

        return json["text"]?.GetValue<string>() is { } text
            ? Result.Success<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(text))
            : Result.Failure($"a {typeof(T).Name} has no text.");
    }

    /// <summary>
    /// Whether <paramref name="type"/> implements <see cref="IAsset{TSelf}"/> of itself.
    /// </summary>
    private static bool IsAssetType(Type type) =>
        Array.Exists(
            type.GetInterfaces(),
            contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IAsset<>)
                && contract.GenericTypeArguments[0] == type
        );

    /// <summary>
    /// A generic method of this class's, <paramref name="name"/>, made for <paramref name="type"/>.
    /// </summary>
    private static MethodInfo GetGeneric(string name, Type type) =>
        (
            typeof(Assets).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{nameof(Assets)} has no method {name}.")
        ).MakeGenericMethod(type);

    /// <summary>
    /// Asset <paramref name="id"/>, at once when it has loaded, else once it has been imported; asked for by asset
    /// <paramref name="dependent"/>'s importer when that is not 0.
    /// </summary>
    private async Task<Result<T>> LoadIdAsync<T>(
        ulong id,
        ulong dependent,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
        where T : IAsset<T>
    {
        Made made = await RequestAsync(id, dependent, typeof(T), RunImportAsync<T>, progress, cancel)
            .ConfigureAwait(false);

        return made.Value.Ok ? Result.Success((T)made.Value.Value) : Result.Failure(made.Value.Error);
    }

    /// <summary>
    /// <paramref name="loading"/>, counted in <see cref="HasLoadsInFlight"/> until it ends.
    /// </summary>
    private async Task<Made> CountInFlightAsync(Task<Made> loading)
    {
        Interlocked.Increment(ref _loadsInFlight);
        try
        {
            return await loading.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _loadsInFlight);
        }
    }

    /// <summary>
    /// The built-in asset named <paramref name="name"/> in <paramref name="folder"/>, as
    /// <see cref="AssetIndex.FindBuiltIn"/> finds it; <paramref name="what"/> says which kind of named asset it is when
    /// it is missing, which is said once.
    /// </summary>
    private Handle<T> FindBuiltIn<T>(string folder, string name, string what)
        where T : IAsset<T>
    {
        if (Database.Index.FindBuiltIn(folder, name) is { } found && found.Type == typeof(T).Name)
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
