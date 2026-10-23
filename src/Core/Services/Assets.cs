using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Telemetry;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// The assets: what the files under the asset roots describe, the project's <c>Assets/</c> folder, then the engine's
/// own <c>Resources/</c>, then each gem's, added as the gem loads (<see cref="Register"/>), the first winning a path
/// two of them have. A file is an asset when a <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what
/// a <see cref="Handle{T}"/> holds, so a file can move and whatever names it still finds it, and the import settings
/// its loader reads. An asset path is relative to its root, with <c>/</c> between folders on every OS; a type's files
/// are those with its extensions (<see cref="IAsset{TSelf}.Extensions"/>). Any thread.
/// <para>
/// Made before the gems load, so each gem's folder is read on a worker as the next gem loads. Nothing is found until
/// every root is indexed (<see cref="IndexAsync"/>), which the engine waits for before any gem is made.
/// </para>
/// <para>
/// A file is read by the <see cref="IAssetLoader{T}"/> a gem provides for its type, else as JSON for an
/// <see cref="IJsonAsset{TSelf}"/> or as text for an <see cref="ITextAsset{TSelf}"/>, then worked on by the
/// <see cref="ILoadPostProcessor{T}"/> a gem provides for its type, if one does. What an asset loads while it is made
/// is what it is made from. What loaded is held weakly, so it is let go once nothing that uses it holds it.
/// </para>
/// <para>
/// The roots are watched until the assets are disposed, at the end of the run. Once their changes settle, they are
/// indexed again, and every asset asked for whose file changed, or that was made from one that did, is made again,
/// each after what it is made from, and told on <see cref="Events"/>.
/// </para>
/// </summary>
public sealed class Assets : IDisposable
{
    private const string Sidecar = ".meta";
    private const string DefaultName = "Default";
    private const string FailuresFolder = "Failures";

    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(300);
    private static readonly Histogram<double> _loadDuration = Metrics.Meter.CreateTimer(
        Metrics.AssetLoadDuration,
        "How long an asset took to read, make and post-process, tagged with its type.",
        Metrics.WorkBuckets);

    private readonly IFileSystem _files;
    private readonly Events _events;
    private readonly CancellationToken _quitting;

    /// <summary>
    /// The engine's own <c>Resources/</c>, Core's: where the default and failure assets are, and only there.
    /// </summary>
    private readonly string _engine;

    /// <summary>
    /// The project's own <c>Assets/</c>: what is under it is the project's, not the engine's or a gem's.
    /// </summary>
    private readonly string _project;

    /// <summary>
    /// What the watches on the roots see, taken in once every root has first been indexed.
    /// </summary>
    private readonly Channel<string> _changes = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Done once every root added has first been indexed (<see cref="IndexAsync"/>).
    /// </summary>
    private readonly TaskCompletionSource _indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The default and failure assets found missing, by path, each said once.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _reportedMissing = new(StringComparer.Ordinal);

    /// <summary>
    /// The asset being made on the flow that runs now, which whatever it loads is recorded against; 0 for none.
    /// </summary>
    private readonly AsyncLocal<ulong> _assetBeingCreated = new();

    /// <summary>
    /// What each asset loaded as it was made last, or has loaded so far as it is made now: what it is made from.
    /// </summary>
    private readonly Dictionary<ulong, List<AssetRef>> _createdFrom = [];

    /// <summary>
    /// Every asset asked for or added, until its file is gone.
    /// </summary>
    private readonly Dictionary<ulong, Slot> _slots = [];

    /// <summary>
    /// The folders assets are found under, in the order they were added, the first winning a path two of them have.
    /// Replaced, never changed, under <see cref="_lock"/>, so it is read without it.
    /// </summary>
    private Root[] _roots = [];

    /// <summary>
    /// What is under the roots, made again whenever they change; empty until they are first indexed. Never changed once
    /// made, so it is read without a lock.
    /// </summary>
    private Index _index = new();

    /// <summary>
    /// Held while the roots, the slots, or what assets are made from, change.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Starts reading the project's <c>Assets/</c> and the engine's own <c>Resources/</c>, the first roots; the gems'
    /// are added as they load (<see cref="Register"/>).
    /// </summary>
    internal Assets(IFileSystem files, App app, Events events)
    {
        _files = files;
        _events = events;
        _quitting = app.Quitting;
        _project = files.Combine(app.Folder, "Assets");
        _engine = GemAssembly.GetResources(files, typeof(Assets).Assembly);

        WatchAsync().FireAndForget();

        // The first roots are the assets' own for the whole run, so they are added with nothing to end them.
        AddRoot(_project);
        AddRoot(_engine);
    }

    /// <summary>
    /// What the gems provide, where each type's loader and post-processor are found: set by whoever builds the service
    /// provider, once it is built, as the assets are made before the gems load so that each one's folder is added as it
    /// does. Nothing loads before it is set.
    /// </summary>
    internal IServiceProvider? Services { get; set; }

    /// <summary>
    /// Stops watching the roots: the engine's to call, at the end of the run.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (Root root in _roots)
            {
                root.Watch.Dispose();
            }
        }
    }

    /// <summary>
    /// The asset at <paramref name="path"/>, or none when no asset is there.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T>
    {
        return IsOf<T>(path) && _index.ByPath.TryGetValue(path, out Indexed? found)
            ? new Handle<T>(found.Id)
            : Handle<T>.None;
    }

    /// <summary>
    /// <typeparamref name="T"/>'s default: the asset named Default, with one of the type's extensions, in the folder
    /// named for the type, <c>Materials/Default.mat</c>, or <c>Domains/Default/Default.domain</c> for an asset that is
    /// a folder, in the engine's own <c>Resources/</c> alone: a project's or a gem's file at that path is an asset like
    /// any other, found by its path, never the default. Every asset type has one. None, and an error logged once, when
    /// it is missing.
    /// </summary>
    public Handle<T> GetDefault<T>()
        where T : IAsset<T> => Get<T>(typeof(T).Name + "s", DefaultName, "default");

    /// <summary>
    /// What is drawn in a broken <typeparamref name="T"/>'s place when <paramref name="type"/> broke: the asset named
    /// for the type of failure in the type's <c>Failures</c> folder, <c>Materials/Failures/Texture.mat</c>, in the
    /// engine's own <c>Resources/</c> alone, as a default is (<see cref="GetDefault{T}"/>). Only the types a renderer
    /// draws a failure as have one. None, and an error logged once, when it is missing.
    /// </summary>
    public Handle<T> GetFailure<T>(FailureType type)
        where T : IAsset<T> => Get<T>($"{typeof(T).Name}s/{FailuresFolder}", type.ToString(), $"{type} failure");

    /// <summary>
    /// The paths of the assets directly in <paramref name="folder"/>, as <see cref="Find{T}"/> takes them: a domain's
    /// prefabs.
    /// </summary>
    public IReadOnlyList<string> GetPaths(string folder)
        => _index.ByFolder.TryGetValue(folder, out List<string>? paths) ? paths : [];

    /// <summary>
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file has its id.
    /// </summary>
    public string? GetPath<T>(Handle<T> asset)
        where T : IAsset<T> => _index.ById.TryGetValue(asset.Id, out Indexed? found) ? found.Path : null;

    /// <summary>
    /// A hash of the asset's file and its <c>.meta</c>, which changes when either does: what its post-processor is
    /// handed (<see cref="ILoadPostProcessor{T}"/>), and what something made from it is checked against without loading
    /// it. Read from the file the first time it is asked for, and kept with the index. 0 when no file has it.
    /// </summary>
    public ulong GetVersion<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        if (!_index.ById.TryGetValue(asset.Id, out Indexed? indexed)) return 0;

        if (indexed.Version != 0) return indexed.Version;

        Result<byte[]> bytes = _files.Read(indexed.File);

        if (!bytes.Ok) return 0;

        indexed.Version = ComputeVersion(bytes.Value, indexed.Settings);

        return indexed.Version;
    }

    /// <summary>
    /// The assets <paramref name="asset"/> was made from: those its loader and its post-processor loaded as they made
    /// it, as it was made last. When one of them is made again, so is it, after it. None for an asset made from its
    /// file alone, and for one not loaded.
    /// </summary>
    public IReadOnlyList<AssetRef> GetDependencies<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        lock (_lock)
        {
            return _createdFrom.TryGetValue(asset.Id, out List<AssetRef>? sources) ? [.. sources] : [];
        }
    }

    /// <summary>
    /// The asset, or why it could not be loaded. It is read on a worker and kept while anything holds it, so a later
    /// call usually completes at once, hence the <see cref="ValueTask{TResult}"/>. Every caller gets the same object
    /// while it is kept; one asking while it is made again waits for what it is made into. A load that fails is
    /// remembered until its file changes. A caller's <paramref name="cancel"/> only stops its own wait.
    /// </summary>
    public ValueTask<Result<T>> LoadAsync<T>(Handle<T> asset, CancellationToken cancel)
        where T : IAsset<T>
    {
        if (asset == Handle<T>.None)
            return new(Result.Failure<T>($"No {typeof(T).Name} was asked for: the handle is none."));

        ulong beingCreated = _assetBeingCreated.Value;
        lock (_lock)
        {
            // Loaded as another asset is made, it is recorded as what that one is made from, and refused when it is
            // that one or is made from it, as each would wait for the other for ever.
            if (beingCreated != 0 && !HasRecorded(beingCreated, new AssetRef(typeof(T), asset.Id)))
            {
                return new(Result.Failure<T>(
                    $"{GetName(asset.Id)} is made from itself: something it loads as it is made loads it."));
            }

            if (!_slots.TryGetValue(asset.Id, out Slot? found))
            {
                found = new Slot<T>();
                _slots.Add(asset.Id, found);
            }

            if (found is not Slot<T> slot)
            {
                return new(Result.Failure<T>(
                    $"{GetName(asset.Id)} is loaded as a {found.Type.Name}, so it is not a {typeof(T).Name}."));
            }

            // Held, and not being made again, it is handed out at once; else the caller waits for the make on its way,
            // the failure kept from the last until its file changes, or a make started now.
            if (slot.Loading is null && slot.Loaded?.Target is T held) return new(Result.Success(held));

            return new((slot.Loading ?? CreateAsync(slot, asset.Id, isRecreation: false)).WaitAsync(cancel));
        }
    }

    /// <summary>
    /// Hands out <paramref name="asset"/>, one a gem made rather than read from a file (a card's atlas), under the
    /// handle the gem gave it, derived from what it was made from so that making it again gives the same one: loading
    /// that handle gets it for as long as anything holds it. Adding another under a handle already held replaces it, as
    /// a reload does. A handle a file has is an error, and the asset is not added.
    /// </summary>
    public void Add<T>(T asset)
        where T : IAsset<T>
    {
        if (asset.Handle == Handle<T>.None)
        {
            throw new ArgumentException(
                $"A {typeof(T).Name} is added with no handle, which its maker gives it.",
                nameof(asset));
        }

        ulong id = asset.Handle.Id;
        if (_index.ById.TryGetValue(id, out Indexed? file))
        {
            Debugging.Error(
                $"A {typeof(T).Name} a gem made has the id {id}, which {file.Path} has, so it is not added.");

            return;
        }

        bool hadLoaded;
        lock (_lock)
        {
            if (!_slots.TryGetValue(id, out Slot? found) || found is not Slot<T> slot)
            {
                slot = new Slot<T>();
                _slots[id] = slot;
            }

            if (ReferenceEquals(slot.Loaded?.Target, asset)) return;

            hadLoaded = slot.Loaded is not null;
            slot.Loaded = new WeakReference(asset);
            slot.Loading = null;
        }

        if (hadLoaded) _events.Publish(new AssetReloaded<T>(asset));
        else _events.Publish(new AssetLoaded<T>(asset));
    }

    /// <summary>
    /// Creates every <typeparamref name="T"/> asked for so far again, as when their files change: for a gem that makes
    /// them, a post-processor, whose settings changed how, and for code loaded again, which a script is the class of
    /// and a prefab names components from. Each that had loaded is told as <see cref="AssetReloaded{T}"/> once created;
    /// done once every one is.
    /// </summary>
    public Task RecreateAsync<T>()
        where T : IAsset<T>
    {
        List<Task> recreating = [];
        lock (_lock)
        {
            foreach ((ulong id, Slot slot) in _slots)
            {
                if (slot is Slot<T>) recreating.Add(slot.RecreateAsync(this, id));
            }
        }

        return Task.WhenAll(recreating);
    }

    /// <summary>
    /// The project's own assets of type <typeparamref name="T"/>, those under its <c>Assets/</c> rather than the
    /// engine's or a gem's, in the order of their paths.
    /// </summary>
    internal IReadOnlyList<Handle<T>> GetProjectAssets<T>()
        where T : IAsset<T>
    {
        List<string> paths = _index.Project.FindAll(IsOf<T>);
        paths.Sort(StringComparer.OrdinalIgnoreCase);

        return paths.ConvertAll(Find<T>);
    }

    /// <summary>
    /// Adds <paramref name="folder"/> to the roots, after those added before it, which win a path it shares with them,
    /// and starts reading it on a worker and watching it: a gem's own <c>Resources/</c>, as the gem loads. What is in
    /// it is found once every root is indexed (<see cref="IndexAsync"/>), until the subscription is disposed: then it
    /// is taken out of the roots, and what is in it goes as a deleted folder's files do. A folder that does not exist,
    /// or is a root already, is left out.
    /// </summary>
    internal Subscription Register(string folder)
        => AddRoot(folder) is { } root ? new Subscription(() => Unregister(root)) : Subscription.None;

    /// <summary>
    /// Indexes every root added, once each has been read: what the engine waits for once the gems have loaded and
    /// before any is made, as the settings and the gems find assets as they are made. What the watches saw meanwhile
    /// is taken in after.
    /// </summary>
    internal async Task IndexAsync()
    {
        Root[] roots = _roots;
        List<Indexed>[] reads = await Task.WhenAll(Array.ConvertAll(roots, root => root.FirstRead))
            .ConfigureAwait(false);
        _index = Merge(roots, reads, _engine, _project);
        _indexed.TrySetResult();
        Debugging.Info($"Indexed {_index.ById.Count} assets under {roots.Length} folders.");
    }

    /// <summary>
    /// Whether <paramref name="asset"/> is <paramref name="source"/>, or is made from it, directly or through others,
    /// as <paramref name="createdFrom"/> says.
    /// </summary>
    private static bool IsCreatedFrom(ulong asset, ulong source, Dictionary<ulong, List<AssetRef>> createdFrom)
    {
        HashSet<ulong> seen = [];
        Stack<ulong> waiting = new([asset]);
        while (waiting.TryPop(out ulong next))
        {
            if (next == source) return true;

            if (seen.Add(next) && createdFrom.TryGetValue(next, out List<AssetRef>? sources))
            {
                foreach (AssetRef made in sources)
                {
                    waiting.Push(made.Id);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="changed"/>, and every asset made from one of them, or from one of those, and so on out, as
    /// <paramref name="createdFrom"/> says.
    /// </summary>
    private static HashSet<ulong> AddCreatedFrom(HashSet<ulong> changed, Dictionary<ulong, List<AssetRef>> createdFrom)
    {
        // Pass after pass, each adding what is made from one found already, until a pass adds nothing.
        HashSet<ulong> found = [.. changed];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((ulong id, List<AssetRef> sources) in createdFrom)
            {
                if (!found.Contains(id) && sources.Exists(source => found.Contains(source.Id)))
                {
                    found.Add(id);
                    grew = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// <paramref name="asset"/> with <paramref name="value"/> given to its <paramref name="property"/> through the
    /// <c>init</c> accessor the asset type has for it, as its handle, and a text asset's text, are set rather than
    /// read.
    /// </summary>
    private static Result<T> TryGive<T>(T asset, string property, object value)
    {
        if (typeof(T).GetProperty(property)?.SetMethod is not { } init)
        {
            return Result.Failure<T>(
                $"{typeof(T).Name}'s {property} is no public property with an init accessor, so it cannot be given.");
        }

        init.Invoke(asset, [value]);

        return Result.Success(asset);
    }

    /// <summary>
    /// Every file with a sidecar under <paramref name="root"/>, by its path from it: the files and folders walked and
    /// the sidecars read, which is what indexing waits on, so it runs on a worker for each root. A sidecar without its
    /// file is warned about, and a broken one is an error and gives no asset.
    /// </summary>
    private static List<Indexed> ReadRoot(IFileSystem files, string root)
    {
        List<Indexed> found = [];
        foreach (string sidecar in FileSystem.FindUnder(root, Sidecar))
        {
            string path = sidecar[..^Sidecar.Length];
            string file = files.Combine([root, .. path.Split('/')]);
            if (!files.FileExists(file))
            {
                Debugging.Warn($"{path}{Sidecar} has no {path} beside it.");

                continue;
            }

            Result<(ulong Id, JsonElement Settings)> read = ReadSidecar(files, file + Sidecar);
            if (!read.Ok)
            {
                Debugging.Error($"{path}{Sidecar} is not a sidecar, so {path} is not an asset: {read.Error}");

                continue;
            }

            found.Add(new Indexed(read.Value.Id, path, file, read.Value.Settings));
        }

        return found;
    }

    /// <summary>
    /// What <paramref name="roots"/> hold, each one's files as <paramref name="reads"/> has them at its index
    /// (<see cref="ReadRoot"/>), taken in root order: every file by its id, its path and its folder, those under
    /// <paramref name="engine"/> by their path besides, and the paths of those under <paramref name="project"/>. A file
    /// with an id another has already is an error and gives no asset, unless it is at that file's path under a later
    /// root, which the first then overrides.
    /// </summary>
    private static Index Merge(Root[] roots, List<Indexed>[] reads, string engine, string project)
    {
        Index index = new();
        for (int i = 0; i < roots.Length; i++)
        {
            bool isEngine = string.Equals(roots[i].Folder, engine, StringComparison.OrdinalIgnoreCase);
            bool isProject = string.Equals(roots[i].Folder, project, StringComparison.OrdinalIgnoreCase);
            foreach (Indexed indexed in reads[i])
            {
                // An id an earlier root has already: at the same path, that root's file overrides this one, and is
                // found as the engine's own when this one is; at another path, it is an error.
                string path = indexed.Path;
                if (index.ById.TryGetValue(indexed.Id, out Indexed? other))
                {
                    if (!string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        Debugging.Error($"{path} has the id {indexed.Id}, which {other.Path} has already; "
                            + "it is not an asset until one of them has another.");
                    }
                    else if (isEngine)
                    {
                        index.Engine.TryAdd(path, other);
                    }

                    continue;
                }

                index.ById.Add(indexed.Id, indexed);
                if (isEngine) index.Engine.TryAdd(path, indexed);

                if (isProject) index.Project.Add(path);

                // The first root to have a path wins it, and only that one's file is listed in its folder.
                if (index.ByPath.TryAdd(path, indexed))
                {
                    int slash = path.LastIndexOf('/');
                    string folder = slash < 0 ? string.Empty : path[..slash];
                    if (!index.ByFolder.TryGetValue(folder, out List<string>? paths))
                    {
                        paths = [];
                        index.ByFolder.Add(folder, paths);
                    }

                    paths.Add(path);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// The id in the sidecar at <paramref name="sidecar"/>, and the rest of it: the import settings its file's loader
    /// reads.
    /// </summary>
    private static Result<(ulong Id, JsonElement Settings)> ReadSidecar(IFileSystem files, string sidecar)
    {
        Result<byte[]> bytes = files.Read(sidecar);
        Result<Dictionary<string, JsonElement>> read = bytes.Ok
            ? Json.Read<Dictionary<string, JsonElement>>(bytes.Value)
            : Result.Failure<Dictionary<string, JsonElement>>(bytes.Error);

        if (!read.Ok) return Result.Failure<(ulong, JsonElement)>(read.Error);

        if (!read.Value.Remove("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number
            || !id.TryGetUInt64(out ulong value)
            || value == 0)
        {
            return Result.Failure<(ulong, JsonElement)>("it needs an \"id\", a whole number above 0.");
        }

        return Result.Success((value, JsonSerializer.SerializeToElement(read.Value, Json.Options)));
    }

    /// <summary>
    /// A hash of <paramref name="file"/> and its sidecar's <paramref name="settings"/>, which changes when either does:
    /// the version of the asset they make, which a post-processor checks what it cached against.
    /// </summary>
    private static ulong ComputeVersion(byte[] file, JsonElement settings)
    {
        XxHash3 hash = new();
        hash.Append(file);
        hash.Append(Encoding.UTF8.GetBytes(settings.GetRawText()));

        return hash.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends in one of <typeparamref name="T"/>'s extensions.
    /// </summary>
    private static bool IsOf<T>(string path)
        where T : IAsset<T>
    {
        IReadOnlyList<string> extensions = T.Extensions;
        for (int i = 0; i < extensions.Count; i++)
        {
            if (path.EndsWith(extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// The engine's own asset named <paramref name="name"/> in <paramref name="folder"/> with one of the type's
    /// extensions, tried in order, or in its own folder of that name for an asset that is a folder;
    /// <paramref name="what"/> says which kind of named asset it is when it is missing, which is said once.
    /// </summary>
    private Handle<T> Get<T>(string folder, string name, string what)
        where T : IAsset<T>
    {
        foreach (string extension in T.Extensions)
        {
            if (_index.Engine.TryGetValue($"{folder}/{name}{extension}", out Indexed? found)
                || _index.Engine.TryGetValue($"{folder}/{name}/{name}{extension}", out found))
            {
                return new Handle<T>(found.Id);
            }
        }

        if (_reportedMissing.TryAdd($"{folder}/{name}", true))
        {
            Debugging.Error($"There is no {what} {typeof(T).Name}: no asset is {folder}/{name} or "
                + $"{folder}/{name}/{name} with a {typeof(T).Name}'s extension "
                + $"({string.Join(", ", T.Extensions)}) under {_engine}.");
        }

        return Handle<T>.None;
    }

    /// <summary>
    /// Takes in what the watches see, once every root has first been indexed, a batch at a time, once they have seen
    /// nothing for <see cref="_settle"/>, until the run ends.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Whatever goes wrong taking in one batch is logged, and the next is still taken in.")]
    private async Task WatchAsync()
    {
        await _indexed.Task.WaitAsync(_quitting).ConfigureAwait(false);
        ChannelReader<string> changes = _changes.Reader;
        while (await changes.WaitToReadAsync(_quitting).ConfigureAwait(false))
        {
            // What the watches saw, taken until nothing more has come for the settle time; a sidecar's change is its
            // file's.
            HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
            do
            {
                while (changes.TryRead(out string? path))
                {
                    touched.Add(path.EndsWith(Sidecar, StringComparison.OrdinalIgnoreCase)
                        ? path[..^Sidecar.Length]
                        : path);
                }

                await Task.Delay(_settle, _quitting).ConfigureAwait(false);
            }
            while (changes.TryPeek(out _));

            try
            {
                await ApplyAsync(touched).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debugging.Error($"The asset files' changes could not be taken in: {ex}");
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="folder"/> to the roots, reading it on a worker and watching it, as <see cref="Register"/>
    /// says; none when it does not exist or is a root already.
    /// </summary>
    private Root? AddRoot(string folder)
    {
        if (!_files.FolderExists(folder)) return null;

        lock (_lock)
        {
            if (Array.Exists(_roots, added => string.Equals(added.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                return null;

            Root root = new(folder, Task.Run(() => ReadRoot(_files, folder)))
            {
                Watch = FileSystem.Watch(folder, path => _changes.Writer.TryWrite(path)),
            };
            _roots = [.. _roots, root];

            return root;
        }
    }

    /// <summary>
    /// Takes <paramref name="root"/> out of the roots and stops watching it. It is told as a change under it, so the
    /// roots are indexed again without it once the changes are next taken in.
    /// </summary>
    private void Unregister(Root root)
    {
        lock (_lock)
        {
            _roots = Array.FindAll(_roots, added => !ReferenceEquals(added, root));
        }

        root.Watch.Dispose();
        _changes.Writer.TryWrite(root.Folder);
    }

    /// <summary>
    /// Indexes the roots again and takes in what differs: what is gone is let go, and every asset asked for is made
    /// again when its file was <paramref name="touched"/> or it is read from another file now, or when it was made from
    /// one of those or from one that is gone, and so on out. Then, if files came, went, moved or were written,
    /// <see cref="AssetsChanged"/>.
    /// </summary>
    private async Task ApplyAsync(HashSet<string> touched)
    {
        Index before = _index;
        Root[] roots = _roots;
        Index now = Merge(roots, Array.ConvertAll(roots, root => ReadRoot(_files, root.Folder)), _engine, _project);
        _index = now;

        // An asset whose file is gone is let go below, and whatever was made from it is made again.
        bool filesChanged = false;
        HashSet<ulong> changed = [];
        HashSet<ulong> gone = [];
        foreach (Indexed old in before.ById.Values)
        {
            if (!now.ById.ContainsKey(old.Id))
            {
                gone.Add(old.Id);
                changed.Add(old.Id);
                filesChanged = true;
            }
        }

        // An asset now read from another file, or whose file the watches saw, is made again.
        foreach (Indexed file in now.ById.Values)
        {
            before.ById.TryGetValue(file.Id, out Indexed? old);
            bool written = !string.Equals(old?.File, file.File, StringComparison.Ordinal)
                || touched.Contains(file.Path);
            filesChanged |= written || !string.Equals(old?.Path, file.Path, StringComparison.OrdinalIgnoreCase);
            if (written) changed.Add(file.Id);
        }

        List<Task> recreating = [];
        lock (_lock)
        {
            HashSet<ulong> toRecreate = AddCreatedFrom(changed, _createdFrom);
            foreach (ulong id in gone)
            {
                _slots.Remove(id);
                _createdFrom.Remove(id);
            }

            // Every one is started before any can load another, which waits for this lock, so whatever loads one that
            // is being made again waits for what it is made into: each is made after what it is made from.
            foreach (ulong id in toRecreate)
            {
                if (_slots.TryGetValue(id, out Slot? slot)) recreating.Add(slot.RecreateAsync(this, id));
            }
        }

        await Task.WhenAll(recreating).ConfigureAwait(false);
        if (filesChanged) _events.Publish(new AssetsChanged());
    }

    /// <summary>
    /// Creates <paramref name="slot"/>'s asset again after its file, or one it is made from, changed, and logs a
    /// failure, as nobody asked for it. Called under <see cref="_lock"/>, which it starts the creation under.
    /// </summary>
    private async Task RecreateAsync<T>(Slot<T> slot, ulong id)
        where T : IAsset<T>
    {
        Result<T> made = await CreateAsync(slot, id, isRecreation: true).ConfigureAwait(false);
        if (!made.Ok) Debugging.Error(made.Error);
    }

    /// <summary>
    /// Starts creating asset <paramref name="id"/> into <paramref name="slot"/>, once the load already on its way
    /// there, if any, is done, and has whoever asks for it meanwhile wait for what it creates: created again after a
    /// change when <paramref name="isRecreation"/>, else loaded as it is asked for. Under <see cref="_lock"/>.
    /// </summary>
    private Task<Result<T>> CreateAsync<T>(Slot<T> slot, ulong id, bool isRecreation)
        where T : IAsset<T>
    {
        Task<Result<T>>? before = slot.Loading;
        int creation = ++slot.Started;
        Task<Result<T>> creating = Task.Run(
            () => CreateAfterAsync(slot, id, creation, before, isRecreation),
            _quitting);
        slot.Loading = creating;

        return creating;
    }

    /// <summary>
    /// Reads and creates asset <paramref name="id"/> (<see cref="ReadAsync{T}"/>) once <paramref name="before"/>, the
    /// load that was on its way, is done, recording what it loads meanwhile as what it is made from, and takes in what
    /// it created: held weakly, and told, as <see cref="AssetReloaded{T}"/> when it is created again after a change
    /// (<paramref name="isRecreation"/>) and had loaded before, else as <see cref="AssetLoaded{T}"/>, the first time or
    /// again once what loaded was let go. One that fails while what loaded before is still held hands that out, as it
    /// stays in use; else the failure is kept until its file changes. Creation number <paramref name="creation"/> of
    /// <paramref name="slot"/>: one started after it, for a later change, takes over the slot.
    /// </summary>
    private async Task<Result<T>> CreateAfterAsync<T>(
        Slot<T> slot,
        ulong id,
        int creation,
        Task<Result<T>>? before,
        bool isRecreation)
        where T : IAsset<T>
    {
        if (before is not null) await before.ConfigureAwait(false);

        // What it was made from last time is forgotten: this make records its own.
        lock (_lock)
        {
            _createdFrom[id] = [];
        }

        // Whatever loads on this flow from here, as it is read, is recorded as what it is made from.
        _assetBeingCreated.Value = id;
        Result<T> made = await ReadAsync<T>(id).ConfigureAwait(false);
        bool isReload;
        T? kept = default;
        lock (_lock)
        {
            isReload = isRecreation && slot.Loaded is not null;
            if (made.Ok) slot.Loaded = new WeakReference(made.Value);
            else if (slot.Loaded?.Target is T held) kept = held;

            // A failure with nothing held stays, as this make, until the file changes.
            if (slot.Started == creation && (made.Ok || kept is not null)) slot.Loading = null;
        }

        if (kept is not null)
        {
            Debugging.Error($"{made.Error} What loaded before stays in use.");

            return Result.Success(kept);
        }

        if (!made.Ok) return made;

        if (isReload) _events.Publish(new AssetReloaded<T>(made.Value));
        else _events.Publish(new AssetLoaded<T>(made.Value));

        return made;
    }

    /// <summary>
    /// <see cref="ReadAndProcessAsync{T}"/>, timed by the asset type.
    /// </summary>
    private async Task<Result<T>> ReadAsync<T>(ulong id)
        where T : IAsset<T>
    {
        long start = _loadDuration.Start();
        Result<T> read = await ReadAndProcessAsync<T>(id).ConfigureAwait(false);
        _loadDuration.RecordSince(start, new KeyValuePair<string, object?>(Metrics.AssetTypeTag, typeof(T).Name));

        return read;
    }

    /// <summary>
    /// The asset <paramref name="id"/> as a <typeparamref name="T"/>, read and made from its file
    /// (<see cref="ReadFileAsync{T}"/>), then given its handle. Then the type's post-processor, when a gem provides
    /// one, works on it, and what it returns is given the handle too. The post-processor is asked for on every load, as
    /// the loader is; a type whose gem was not made, which Core has said why, loads without it. Nothing loads before
    /// <see cref="Services"/> is set, as what reads a file is not known until then.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A loader that throws on a file fails that file, "
            + "as content that does not load; the engine goes on.")]
    private async Task<Result<T>> ReadAndProcessAsync<T>(ulong id)
        where T : IAsset<T>
    {
        if (!_index.ById.TryGetValue(id, out Indexed? indexed)) return Result.Failure<T>($"No asset has the id {id}.");

        if (Services is not { } services)
            return Result.Failure<T>($"{indexed.Path} was asked for before the gems were, so nothing can read it yet.");

        if (!IsOf<T>(indexed.Path))
        {
            return Result.Failure<T>($"{indexed.Path} is not a {typeof(T).Name}: a {typeof(T).Name}'s file ends in "
                + $"{string.Join(", ", T.Extensions)}.");
        }

        Result<byte[]> bytes = await _files.ReadAsync(indexed.File, _quitting).ConfigureAwait(false);

        if (!bytes.Ok) return Result.Failure<T>($"{indexed.Path} could not be read: {bytes.Error}");

        Result<T> loaded;
        try
        {
            loaded = await ReadFileAsync<T>(services, indexed, bytes.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loaded = Result.Failure<T>($"its loader threw: {ex}");
        }

        loaded = loaded.Ok ? TryGive(loaded.Value, nameof(IAsset<T>.Handle), new Handle<T>(id)) : loaded;

        if (!loaded.Ok) return Result.Failure<T>($"{indexed.Path} is not a {typeof(T).Name}: {loaded.Error}");

        indexed.Version = ComputeVersion(bytes.Value, indexed.Settings);

        if (services.GetProvided<ILoadPostProcessor<T>>(out _) is not { } processor) return loaded;

        try
        {
            loaded = await processor.ProcessAsync(loaded.Value, indexed.Version, _quitting).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loaded = Result.Failure<T>($"its post-processor threw: {ex}");
        }

        return loaded.Ok
            ? TryGive(loaded.Value, nameof(IAsset<T>.Handle), new Handle<T>(id))
            : Result.Failure<T>($"{indexed.Path} was read, but could not be post-processed: {loaded.Error}");
    }

    /// <summary>
    /// <paramref name="bytes"/>, <paramref name="file"/>'s, as a <typeparamref name="T"/>: read by the loader a gem
    /// provides for the type among <paramref name="services"/>, handed the import settings its sidecar holds; else,
    /// with a sidecar that holds only the id, as JSON for an <see cref="IJsonAsset{TSelf}"/>, or as text for an
    /// <see cref="ITextAsset{TSelf}"/>, made with its parameterless constructor. The loader is asked for on every load,
    /// as loads are kept.
    /// </summary>
    private ValueTask<Result<T>> ReadFileAsync<T>(IServiceProvider services, Indexed file, byte[] bytes)
        where T : IAsset<T>
    {
        IAssetLoader<T>? loader = services.GetProvided<IAssetLoader<T>>(out bool isMade);

        if (!isMade) return new(Result.Failure<T>($"the gem that reads {typeof(T).Name}s was not made."));

        if (loader is not null) return loader.LoadAsync(file.Path, bytes, file.Settings, _quitting);

        bool isJson = typeof(T).IsAssignableTo(typeof(IJsonAsset<T>));

        if (!isJson && !typeof(T).IsAssignableTo(typeof(ITextAsset<T>)))
            return new(Result.Failure<T>($"no gem the project lists reads {typeof(T).Name}s."));

        if (file.Settings.GetPropertyCount() != 0)
        {
            return new(Result.Failure<T>($"its .meta holds settings, {file.Settings.GetRawText()}, "
                + $"which a {typeof(T).Name} read as {(isJson ? "JSON" : "text")} does not take."));
        }

        if (isJson) return new(Json.Read<T>(bytes));

        ReadOnlySpan<byte> text = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? bytes.AsSpan(Encoding.UTF8.Preamble.Length)
            : bytes;

        return new(TryGive(Activator.CreateInstance<T>(), nameof(ITextAsset<T>.Text), Encoding.UTF8.GetString(text)));
    }

    /// <summary>
    /// Records that <paramref name="beingCreated"/> loads <paramref name="source"/>; false when
    /// <paramref name="source"/> is <paramref name="beingCreated"/> or is made from it, so waiting for it would wait
    /// for ever. Under <see cref="_lock"/>.
    /// </summary>
    private bool HasRecorded(ulong beingCreated, AssetRef source)
    {
        if (!_createdFrom.TryGetValue(beingCreated, out List<AssetRef>? sources))
        {
            sources = [];
            _createdFrom.Add(beingCreated, sources);
        }

        if (!sources.Contains(source)) sources.Add(source);

        return !IsCreatedFrom(source.Id, beingCreated, _createdFrom);
    }

    /// <summary>
    /// Where asset <paramref name="id"/> is, for a message, or its id when no file has it.
    /// </summary>
    private string GetName(ulong id)
        => _index.ById.TryGetValue(id, out Indexed? found) ? found.Path : id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What is under the roots: every asset by its id, by its path, the paths in each folder, and the engine's own
    /// assets by their path, whatever another root has at it.
    /// </summary>
    private sealed class Index
    {
        public Dictionary<ulong, Indexed> ById { get; } = [];

        public Dictionary<string, Indexed> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<string>> ByFolder { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Indexed> Engine { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The paths of the project's own assets, those under its <c>Assets/</c>.
        /// </summary>
        public List<string> Project { get; } = [];
    }

    /// <summary>
    /// A file with a sidecar: its id, its asset path, the file on this OS, and its sidecar's settings beside the id.
    /// </summary>
    private sealed record Indexed(ulong Id, string Path, string File, JsonElement Settings)
    {
        /// <summary>
        /// A hash of the file and the settings once one is read (<see cref="GetVersion{T}"/>), 0 until then.
        /// </summary>
        public ulong Version { get; set; }
    }

    /// <summary>
    /// A folder assets are found under, its first read (<see cref="ReadRoot"/>), started as it was added, which the
    /// first index waits for (<see cref="IndexAsync"/>), and the watch on it.
    /// </summary>
    private sealed record Root(string Folder, Task<List<Indexed>> FirstRead)
    {
        public required Subscription Watch { get; init; }
    }

    /// <summary>
    /// An asset asked for or added, as the type it was first asked for as.
    /// </summary>
    private abstract class Slot
    {
        public abstract Type Type { get; }

        /// <summary>
        /// Creates it again, as asset <paramref name="id"/> of <paramref name="assets"/>
        /// (<see cref="RecreateAsync{T}(Slot{T}, ulong)"/>). Under <see cref="_lock"/>.
        /// </summary>
        public abstract Task RecreateAsync(Assets assets, ulong id);
    }

    /// <inheritdoc cref="Slot"/>
    private sealed class Slot<T> : Slot
        where T : IAsset<T>
    {
        public override Type Type => typeof(T);

        /// <summary>
        /// The make on its way, or the failure the last ended in while nothing is held, kept until its file changes;
        /// null once it has loaded, and before it is first asked for. Under <see cref="_lock"/>.
        /// </summary>
        public Task<Result<T>>? Loading { get; set; }

        /// <summary>
        /// What loaded, held weakly: it lives as long as something that uses it holds it. Null until it first loads.
        /// Under <see cref="_lock"/>.
        /// </summary>
        public WeakReference? Loaded { get; set; }

        /// <summary>
        /// How many makes have been started, so a make can tell whether a later one has taken over. Under
        /// <see cref="_lock"/>.
        /// </summary>
        public int Started { get; set; }

        public override Task RecreateAsync(Assets assets, ulong id) => assets.RecreateAsync(this, id);
    }
}
