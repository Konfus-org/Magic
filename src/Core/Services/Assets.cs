using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Internal;

namespace Magic.Services;

/// <summary>
/// The assets: what the files under the asset roots describe, the project's <c>Assets/</c> folder, then the engine's
/// and each gem's <c>Resources/</c> (<see cref="Project.Resources"/>), the first winning a path two of them have. A file
/// is an asset when a <c>.meta</c> sidecar is beside it, holding its 64-bit id, which is what a <see cref="Handle{T}"/>
/// holds, so a file can move and whatever names it still finds it, and the import settings its loader reads. An asset
/// path is relative to its root, with <c>/</c> between folders on every OS; a type's files are those with its
/// extensions (<see cref="IAsset{TSelf}.Extensions"/>). Any thread.
/// <para>
/// A file is read by the <see cref="IAssetLoader{T}"/> a gem provides for its type, else as JSON for an
/// <see cref="IJsonAsset{TSelf}"/> or as text for an <see cref="ITextAsset{TSelf}"/>, then worked on by the
/// <see cref="ILoadPostProcessor{T}"/> a gem provides for its type, if one does. What an asset loads while it is made is
/// what it is made from. What loaded is held weakly, so it is let go once nothing that uses it holds it.
/// </para>
/// <para>
/// The roots are watched. Once their changes settle, they are indexed again, and every asset asked for whose file
/// changed, or that was made from one that did, is made again, each after what it is made from, and told on
/// <see cref="Events"/>.
/// </para>
/// </summary>
public sealed class Assets
{
    private const string Sidecar = ".meta";
    private const string DefaultName = "Default";

    /// <summary>
    /// How long the files must have been left alone before their changes are taken in: an editor saving a file, or a
    /// build copying a folder, writes several times in a row.
    /// </summary>
    private static readonly TimeSpan _settle = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;
    private readonly Events _events;
    private readonly IServiceProvider _services;
    private readonly CancellationToken _stopping;

    /// <summary>
    /// The folders assets are found under, the first winning a path two of them have.
    /// </summary>
    private readonly string[] _roots;

    private readonly ConcurrentDictionary<Type, bool> _missingDefaults = new();

    /// <summary>
    /// The asset being made on the flow that runs now, which whatever it loads is recorded against; 0 for none.
    /// </summary>
    private readonly AsyncLocal<ulong> _maker = new();

    /// <summary>
    /// Held while the slots, or what assets are made from, change.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// Every asset asked for or added, until its file is gone.
    /// </summary>
    private readonly Dictionary<ulong, Slot> _slots = [];

    /// <summary>
    /// What each asset loaded as it was made last, or has loaded so far as it is made now: what it is made from.
    /// </summary>
    private readonly Dictionary<ulong, List<AssetRef>> _madeFrom = [];

    /// <summary>
    /// What is under the roots, made again whenever they change. Never changed once made, so it is read without a lock.
    /// </summary>
    private Index _index;

    internal Assets(IFileSystem files, Project project, Lifetime lifetime, Events events, IServiceProvider services)
    {
        _files = files;
        _events = events;
        _services = services;
        _stopping = lifetime.Stopping;

        string[] folders = [files.Combine(project.Root, "Assets"), .. project.Resources];
        List<string> roots = [];
        foreach (string folder in folders)
        {
            if (files.FolderExists(folder) && !roots.Exists(root => string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(folder);
            }
        }

        _roots = [.. roots];
        _index = Build(files, _roots);

        Channel<string> changes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        SubscriptionList watches = new();
        foreach (string root in _roots)
        {
            watches.Add(FileSystem.Watch(root, path => changes.Writer.TryWrite(path)));
        }

        WatchAsync(changes.Reader, watches).FireAndForget();
        Debugging.Info($"Indexed {_index.ById.Count} assets under {_roots.Length} folders.");
    }

    /// <summary>
    /// The asset at <paramref name="path"/>, or none when no asset is there.
    /// </summary>
    public Handle<T> Find<T>(string path)
        where T : IAsset<T>
    {
        return IsOf<T>(path) && _index.ByPath.TryGetValue(path, out Indexed? found) ? new Handle<T>(found.Id) : Handle<T>.None;
    }

    /// <summary>
    /// <typeparamref name="T"/>'s default: the asset named Default, with one of the type's extensions, in the folder
    /// named for the type, <c>Materials/Default.mat</c>, or <c>Domains/Default/Default.domain</c> for an asset that is
    /// a folder. Every asset type has one, and a project replaces one by having its own at the same path. None, and an
    /// error logged once, when it is missing.
    /// </summary>
    public Handle<T> Default<T>()
        where T : IAsset<T>
    {
        string folder = typeof(T).Name + "s";
        foreach (string extension in T.Extensions)
        {
            Handle<T> found = Find<T>($"{folder}/{DefaultName}{extension}");
            if (found == Handle<T>.None)
            {
                found = Find<T>($"{folder}/{DefaultName}/{DefaultName}{extension}");
            }

            if (found != Handle<T>.None)
            {
                return found;
            }
        }

        if (_missingDefaults.TryAdd(typeof(T), true))
        {
            Debugging.Error($"There is no default {typeof(T).Name}: no asset is {folder}/{DefaultName} or {folder}/{DefaultName}/{DefaultName} with a {typeof(T).Name}'s extension ({string.Join(", ", T.Extensions)}) under {string.Join(", ", _roots)}.");
        }

        return Handle<T>.None;
    }

    /// <summary>
    /// The paths of the assets directly in <paramref name="folder"/>, as <see cref="Find{T}"/> takes them: a domain's
    /// prefabs.
    /// </summary>
    public IReadOnlyList<string> Paths(string folder)
    {
        return _index.ByFolder.TryGetValue(folder, out List<string>? paths) ? paths : [];
    }

    /// <summary>
    /// Where <paramref name="asset"/> is, as <see cref="Find{T}"/> takes it; null when no file has its id.
    /// </summary>
    public string? Path<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        return _index.ById.TryGetValue(asset.Id, out Indexed? found) ? found.Path : null;
    }

    /// <summary>
    /// A hash of the asset's file and its <c>.meta</c>, which changes when either does: what its post-processor is handed
    /// (<see cref="ILoadPostProcessor{T}"/>), and what something made from it is checked against without loading it. Read
    /// from the file the first time it is asked for, and kept with the index. 0 when no file has it.
    /// </summary>
    public ulong Version<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        if (!_index.ById.TryGetValue(asset.Id, out Indexed? indexed))
        {
            return 0;
        }

        if (indexed.Version != 0)
        {
            return indexed.Version;
        }

        Result<byte[]> bytes = _files.Read(indexed.File);
        if (!bytes.Ok)
        {
            return 0;
        }

        indexed.Version = VersionOf(bytes.Value, indexed.Settings);
        return indexed.Version;
    }

    /// <summary>
    /// The assets <paramref name="asset"/> was made from: those its loader and its post-processor loaded as they made it,
    /// as it was made last. When one of them is made again, so is it, after it. None for an asset made from its file
    /// alone, and for one not loaded.
    /// </summary>
    public IReadOnlyList<AssetRef> Dependencies<T>(Handle<T> asset)
        where T : IAsset<T>
    {
        lock (_lock)
        {
            return _madeFrom.TryGetValue(asset.Id, out List<AssetRef>? sources) ? [.. sources] : [];
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
        {
            return new(Result.Failure<T>($"No {typeof(T).Name} was asked for: the handle is none."));
        }

        ulong maker = _maker.Value;
        lock (_lock)
        {
            if (maker != 0 && !Records(maker, new AssetRef(typeof(T), asset.Id)))
            {
                return new(Result.Failure<T>($"{NameOf(asset.Id)} is made from itself: something it loads as it is made loads it."));
            }

            if (!_slots.TryGetValue(asset.Id, out Slot? found))
            {
                found = new Slot<T>(this, asset.Id);
                _slots.Add(asset.Id, found);
            }

            if (found is not Slot<T> slot)
            {
                return new(Result.Failure<T>($"{NameOf(asset.Id)} is loaded as a {found.Type.Name}, so it is not a {typeof(T).Name}."));
            }

            if (slot.Loading is null && slot.Loaded?.Target is T held)
            {
                return new(Result.Success(held));
            }

            return new((slot.Loading ?? MakeAsync(slot, asset.Id, isRemake: false)).WaitAsync(cancel));
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
            throw new ArgumentException($"A {typeof(T).Name} is added with no handle, which its maker gives it.", nameof(asset));
        }

        ulong id = asset.Handle.Id;
        if (_index.ById.TryGetValue(id, out Indexed? file))
        {
            Debugging.Error($"A {typeof(T).Name} a gem made has the id {id}, which {file.Path} has, so it is not added.");
            return;
        }

        bool hadLoaded;
        lock (_lock)
        {
            if (!_slots.TryGetValue(id, out Slot? found) || found is not Slot<T> slot)
            {
                slot = new Slot<T>(this, id);
                _slots[id] = slot;
            }

            if (ReferenceEquals(slot.Loaded?.Target, asset))
            {
                return;
            }

            hadLoaded = slot.Loaded is not null;
            slot.Loaded = new WeakReference(asset);
            slot.Loading = null;
        }

        if (hadLoaded)
        {
            _events.Publish(new AssetReloaded<T>(asset));
        }
        else
        {
            _events.Publish(new AssetLoaded<T>(asset));
        }
    }

    /// <summary>
    /// Takes in what the watches see, a batch at a time, once they have seen nothing for <see cref="_settle"/>, until the
    /// run ends, when it stops <paramref name="watches"/>.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Whatever goes wrong taking in one batch is logged, and the next is still taken in.")]
    private async Task WatchAsync(ChannelReader<string> changes, SubscriptionList watches)
    {
        try
        {
            while (await changes.WaitToReadAsync(_stopping).ConfigureAwait(false))
            {
                HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
                do
                {
                    while (changes.TryRead(out string? path))
                    {
                        touched.Add(path.EndsWith(Sidecar, StringComparison.OrdinalIgnoreCase) ? path[..^Sidecar.Length] : path);
                    }

                    await Task.Delay(_settle, _stopping).ConfigureAwait(false);
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
        finally
        {
            watches.Dispose();
        }
    }

    /// <summary>
    /// Indexes the roots again and takes in what differs: what is gone is let go, and every asset asked for is made again
    /// when its file was <paramref name="touched"/> or it is read from another file now, or when it was made from one of
    /// those or from one that is gone, and so on out. Then, if files came, went, moved or were written,
    /// <see cref="AssetsChanged"/>.
    /// </summary>
    private async Task ApplyAsync(HashSet<string> touched)
    {
        Index before = _index;
        Index now = Build(_files, _roots);
        _index = now;

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

        foreach (Indexed file in now.ById.Values)
        {
            before.ById.TryGetValue(file.Id, out Indexed? old);
            bool written = !string.Equals(old?.File, file.File, StringComparison.Ordinal) || touched.Contains(file.Path);
            filesChanged |= written || !string.Equals(old?.Path, file.Path, StringComparison.OrdinalIgnoreCase);
            if (written)
            {
                changed.Add(file.Id);
            }
        }

        List<Task> remaking = [];
        lock (_lock)
        {
            HashSet<ulong> remake = AndMadeFrom(changed, _madeFrom);
            foreach (ulong id in gone)
            {
                _slots.Remove(id);
                _madeFrom.Remove(id);
            }

            // Every one is started before any can load another, which waits for this lock, so whatever loads one that is
            // being made again waits for what it is made into: each is made after what it is made from.
            foreach (ulong id in remake)
            {
                if (_slots.TryGetValue(id, out Slot? slot))
                {
                    remaking.Add(slot.RemakeAsync());
                }
            }
        }

        await Task.WhenAll(remaking).ConfigureAwait(false);
        if (filesChanged)
        {
            _events.Publish(new AssetsChanged());
        }
    }

    /// <summary>
    /// Makes <paramref name="slot"/>'s asset again after its file, or one it is made from, changed, and logs a failure,
    /// as nobody asked for it. Called under <see cref="_lock"/>, which it starts the make under.
    /// </summary>
    private async Task RemakeAsync<T>(Slot<T> slot, ulong id)
        where T : IAsset<T>
    {
        Result<T> made = await MakeAsync(slot, id, isRemake: true).ConfigureAwait(false);
        if (!made.Ok)
        {
            Debugging.Error(made.Error);
        }
    }

    /// <summary>
    /// Starts making asset <paramref name="id"/> into <paramref name="slot"/>, once the load already on its way there, if
    /// any, is done, and has whoever asks for it meanwhile wait for what it makes: made again after a change when
    /// <paramref name="isRemake"/>, else loaded as it is asked for. Under <see cref="_lock"/>.
    /// </summary>
    private Task<Result<T>> MakeAsync<T>(Slot<T> slot, ulong id, bool isRemake)
        where T : IAsset<T>
    {
        Task<Result<T>>? before = slot.Loading;
        int make = ++slot.Started;
        Task<Result<T>> making = Task.Run(() => MakeAfterAsync(slot, id, make, before, isRemake), _stopping);
        slot.Loading = making;
        return making;
    }

    /// <summary>
    /// Reads and makes asset <paramref name="id"/> (<see cref="ReadAsync{T}"/>) once <paramref name="before"/>, the load
    /// that was on its way, is done, recording what it loads meanwhile as what it is made from, and takes in what it
    /// made: held weakly, and told, as <see cref="AssetReloaded{T}"/> when it is made again after a change
    /// (<paramref name="isRemake"/>) and had loaded before, else as <see cref="AssetLoaded{T}"/>, the first time or again
    /// once what loaded was let go. One that fails while what loaded before is still held hands that out, as it stays in
    /// use; else the failure is kept until its file changes. Make number <paramref name="make"/> of
    /// <paramref name="slot"/>: one started after it, for a later change, takes over the slot.
    /// </summary>
    private async Task<Result<T>> MakeAfterAsync<T>(Slot<T> slot, ulong id, int make, Task<Result<T>>? before, bool isRemake)
        where T : IAsset<T>
    {
        if (before is not null)
        {
            await before.ConfigureAwait(false);
        }

        lock (_lock)
        {
            _madeFrom[id] = [];
        }

        _maker.Value = id;
        Result<T> made = await ReadAsync<T>(id).ConfigureAwait(false);
        bool isReload;
        T? kept = default;
        lock (_lock)
        {
            isReload = isRemake && slot.Loaded is not null;
            if (made.Ok)
            {
                slot.Loaded = new WeakReference(made.Value);
            }
            else if (slot.Loaded?.Target is T held)
            {
                kept = held;
            }

            // A failure with nothing held stays, as this make, until the file changes.
            if (slot.Started == make && (made.Ok || kept is not null))
            {
                slot.Loading = null;
            }
        }

        if (kept is not null)
        {
            Debugging.Error($"{made.Error} What loaded before stays in use.");
            return Result.Success(kept);
        }

        if (!made.Ok)
        {
            return made;
        }

        if (isReload)
        {
            _events.Publish(new AssetReloaded<T>(made.Value));
        }
        else
        {
            _events.Publish(new AssetLoaded<T>(made.Value));
        }

        return made;
    }

    /// <summary>
    /// The asset <paramref name="id"/> as a <typeparamref name="T"/>, read and made from its file
    /// (<see cref="ReadFileAsync{T}"/>), then given its handle. Then the type's post-processor, when a gem provides one,
    /// works on it, and what it returns is given the handle too.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A loader that throws on a file fails that file, as content that does not load; the engine goes on.")]
    private async Task<Result<T>> ReadAsync<T>(ulong id)
        where T : IAsset<T>
    {
        if (!_index.ById.TryGetValue(id, out Indexed? indexed))
        {
            return Result.Failure<T>($"No asset has the id {id}.");
        }

        if (!IsOf<T>(indexed.Path))
        {
            return Result.Failure<T>($"{indexed.Path} is not a {typeof(T).Name}: a {typeof(T).Name}'s file ends in {string.Join(", ", T.Extensions)}.");
        }

        Result<byte[]> bytes = await _files.ReadAsync(indexed.File, _stopping).ConfigureAwait(false);
        if (!bytes.Ok)
        {
            return Result.Failure<T>($"{indexed.Path} could not be read: {bytes.Error}");
        }

        Result<T> loaded;
        try
        {
            loaded = await ReadFileAsync<T>(indexed, bytes.Value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loaded = Result.Failure<T>($"its loader threw: {ex}");
        }

        loaded = loaded.Ok ? Given(loaded.Value, nameof(IAsset<T>.Handle), new Handle<T>(id)) : loaded;
        if (!loaded.Ok)
        {
            return Result.Failure<T>($"{indexed.Path} is not a {typeof(T).Name}: {loaded.Error}");
        }

        indexed.Version = VersionOf(bytes.Value, indexed.Settings);
        if (PostProcessorOf<T>() is not { } processor)
        {
            return loaded;
        }

        try
        {
            loaded = await processor.ProcessAsync(loaded.Value, indexed.Version, _stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loaded = Result.Failure<T>($"its post-processor threw: {ex}");
        }

        return loaded.Ok
            ? Given(loaded.Value, nameof(IAsset<T>.Handle), new Handle<T>(id))
            : Result.Failure<T>($"{indexed.Path} was read, but could not be post-processed: {loaded.Error}");
    }

    /// <summary>
    /// <paramref name="bytes"/>, <paramref name="file"/>'s, as a <typeparamref name="T"/>: read by the loader a gem
    /// provides for the type, handed the import settings its sidecar holds; else, with a sidecar that holds only the id,
    /// as JSON for an <see cref="IJsonAsset{TSelf}"/>, or as text for an <see cref="ITextAsset{TSelf}"/>, made with its
    /// parameterless constructor. The loader is asked for on every load, as loads are kept.
    /// </summary>
    private ValueTask<Result<T>> ReadFileAsync<T>(Indexed file, byte[] bytes)
        where T : IAsset<T>
    {
        IAssetLoader<T>? loader;
        try
        {
            loader = _services.GetService(typeof(IAssetLoader<T>)) as IAssetLoader<T>;
        }
        catch (InvalidOperationException)
        {
            return new(Result.Failure<T>($"the gem that reads {typeof(T).Name}s was not made."));
        }

        if (loader is not null)
        {
            return loader.LoadAsync(file.Path, bytes, file.Settings, _stopping);
        }

        bool isJson = typeof(T).IsAssignableTo(typeof(IJsonAsset<T>));
        if (!isJson && !typeof(T).IsAssignableTo(typeof(ITextAsset<T>)))
        {
            return new(Result.Failure<T>($"no gem the project lists reads {typeof(T).Name}s."));
        }

        if (file.Settings.GetPropertyCount() != 0)
        {
            return new(Result.Failure<T>($"its .meta holds settings, {file.Settings.GetRawText()}, which a {typeof(T).Name} read as {(isJson ? "JSON" : "text")} does not take."));
        }

        if (isJson)
        {
            return new(Json.Read<T>(bytes));
        }

        ReadOnlySpan<byte> text = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
        return new(Given(Activator.CreateInstance<T>(), nameof(ITextAsset<T>.Text), Encoding.UTF8.GetString(text)));
    }

    /// <summary>
    /// The post-processor a gem provides for <typeparamref name="T"/>, asked for on every load as the loader is; null when
    /// none does, or the gem that does was not made, which Core has said why: the type loads without it.
    /// </summary>
    private ILoadPostProcessor<T>? PostProcessorOf<T>()
        where T : IAsset<T>
    {
        try
        {
            return _services.GetService(typeof(ILoadPostProcessor<T>)) as ILoadPostProcessor<T>;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records that <paramref name="maker"/>, being made, loads <paramref name="source"/>; false when
    /// <paramref name="source"/> is <paramref name="maker"/> or is made from it, so waiting for it would wait for ever.
    /// Under <see cref="_lock"/>.
    /// </summary>
    private bool Records(ulong maker, AssetRef source)
    {
        if (!_madeFrom.TryGetValue(maker, out List<AssetRef>? sources))
        {
            sources = [];
            _madeFrom.Add(maker, sources);
        }

        if (!sources.Contains(source))
        {
            sources.Add(source);
        }

        return !IsMadeFrom(source.Id, maker, _madeFrom);
    }

    /// <summary>
    /// Where asset <paramref name="id"/> is, for a message, or its id when no file has it.
    /// </summary>
    private string NameOf(ulong id)
    {
        return _index.ById.TryGetValue(id, out Indexed? found) ? found.Path : id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether <paramref name="asset"/> is <paramref name="source"/>, or is made from it, directly or through others, as
    /// <paramref name="madeFrom"/> says.
    /// </summary>
    private static bool IsMadeFrom(ulong asset, ulong source, Dictionary<ulong, List<AssetRef>> madeFrom)
    {
        HashSet<ulong> seen = [];
        Stack<ulong> waiting = new([asset]);
        while (waiting.TryPop(out ulong next))
        {
            if (next == source)
            {
                return true;
            }

            if (seen.Add(next) && madeFrom.TryGetValue(next, out List<AssetRef>? sources))
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
    /// <paramref name="madeFrom"/> says.
    /// </summary>
    private static HashSet<ulong> AndMadeFrom(HashSet<ulong> changed, Dictionary<ulong, List<AssetRef>> madeFrom)
    {
        HashSet<ulong> found = [.. changed];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((ulong id, List<AssetRef> sources) in madeFrom)
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
    /// <c>init</c> accessor the asset type has for it, as its handle, and a text asset's text, are set rather than read.
    /// </summary>
    private static Result<T> Given<T>(T asset, string property, object value)
    {
        if (typeof(T).GetProperty(property)?.SetMethod is not { } init)
        {
            return Result.Failure<T>($"{typeof(T).Name}'s {property} is no public property with an init accessor, so it cannot be given.");
        }

        init.Invoke(asset, [value]);
        return Result.Success(asset);
    }

    /// <summary>
    /// Every file with a sidecar under <paramref name="roots"/>, by its id, its path and its folder. A sidecar without its
    /// file is warned about; a broken one, or one with an id another file has already, is an error and gives no asset,
    /// unless it is at that file's path under a later root, which the first then overrides.
    /// </summary>
    private static Index Build(IFileSystem files, string[] roots)
    {
        Index index = new();
        foreach (string root in roots)
        {
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

                if (index.ById.TryGetValue(read.Value.Id, out Indexed? other))
                {
                    if (!string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        Debugging.Error($"{path} has the id {read.Value.Id}, which {other.Path} has already; it is not an asset until one of them has another.");
                    }

                    continue;
                }

                Indexed indexed = new(read.Value.Id, path, file, read.Value.Settings);
                index.ById.Add(indexed.Id, indexed);
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
        Result<Dictionary<string, JsonElement>> read = bytes.Ok ? Json.Read<Dictionary<string, JsonElement>>(bytes.Value) : Result.Failure<Dictionary<string, JsonElement>>(bytes.Error);
        if (!read.Ok)
        {
            return Result.Failure<(ulong, JsonElement)>(read.Error);
        }

        if (!read.Value.Remove("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt64(out ulong value) || value == 0)
        {
            return Result.Failure<(ulong, JsonElement)>("it needs an \"id\", a whole number above 0.");
        }

        return Result.Success((value, JsonSerializer.SerializeToElement(read.Value, Json.Options)));
    }

    /// <summary>
    /// A hash of <paramref name="file"/> and its sidecar's <paramref name="settings"/>, which changes when either does:
    /// the version of the asset they make, which a post-processor checks what it cached against.
    /// </summary>
    private static ulong VersionOf(byte[] file, JsonElement settings)
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
            if (path.EndsWith(extensions[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What is under the roots: every asset by its id, by its path, and the paths in each folder.
    /// </summary>
    private sealed class Index
    {
        public Dictionary<ulong, Indexed> ById { get; } = [];

        public Dictionary<string, Indexed> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<string>> ByFolder { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file with a sidecar: its id, its asset path, the file on this OS, and its sidecar's settings beside the id.
    /// </summary>
    private sealed record Indexed(ulong Id, string Path, string File, JsonElement Settings)
    {
        /// <summary>
        /// A hash of the file and the settings once one is read (<see cref="Assets.Version{T}"/>), 0 until then.
        /// </summary>
        public ulong Version { get; set; }
    }

    /// <summary>
    /// An asset asked for or added, as the type it was first asked for as.
    /// </summary>
    private abstract class Slot
    {
        public abstract Type Type { get; }

        /// <summary>
        /// Makes it again (<see cref="Assets.RemakeAsync{T}"/>). Under <see cref="_lock"/>.
        /// </summary>
        public abstract Task RemakeAsync();
    }

    /// <inheritdoc cref="Slot"/>
    private sealed class Slot<T>(Assets assets, ulong id) : Slot
        where T : IAsset<T>
    {
        private readonly Assets _assets = assets;
        private readonly ulong _id = id;

        public override Type Type => typeof(T);

        /// <summary>
        /// The make on its way, or the failure the last ended in while nothing is held, kept until its file changes; null
        /// once it has loaded, and before it is first asked for. Under <see cref="_lock"/>.
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

        public override Task RemakeAsync() => _assets.RemakeAsync(this, _id);
    }
}
