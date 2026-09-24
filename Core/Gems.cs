using DryIoc;
using Magic.Attributes;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Magic;

/// <summary>
/// The whole gem system: load, watch, reload, unload.
///
/// <para>A gem is one dll holding one <see cref="GemAttribute"/> class and any number of
/// <see cref="GemExportAttribute"/> classes. Each is constructed once, its constructor parameters resolved from the
/// container, and each export is registered into the container under its contracts. What a gem's constructors need is
/// what it depends on; gems load after whatever provides that, and unload before it. A
/// <see cref="GemAttribute.Static"/> gem is never unloaded before shutdown, and neither is anything it depends on.
/// Whatever a gem registers with a host service (a system, a subscription, an event watch) the gem disposes in its
/// own Dispose: the host tracks none of it, and a handle left behind keeps the old assembly alive after a reload.
/// </para>
///
/// Gems only change on the main thread: <see cref="Load"/> at startup, <see cref="ProcessChanges"/> at the top of every frame,
/// and <see cref="Dispose"/> on the way out. The folder watcher only queues changes.
/// </summary>
internal sealed class Gems(IContainer container, IFileSystem files) : IDisposable
{
    /// <summary>How long a file must be quiet before its change is applied; a build touches a dll several times.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly string HostAssembly = typeof(GemAttribute).Assembly.GetName().Name!;

    private readonly List<Gem> _gems = []; // in load order
    private readonly Dictionary<string, (bool Deleted, long Stamp)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _pendingLock = new();
    private readonly List<WeakReference<LoadContext>> _unloaded = [];
    private IDisposable? _watcher;

    internal IReadOnlyList<Gem> Loaded => _gems;

    /// <summary>Stops watching and unloads everything, dependents before dependencies and logger gems last.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        foreach (Gem gem in _gems.Where(g => !g.Provides.Contains(typeof(ILogger))).Reverse().ToList())
            Unload(gem);
        foreach (Gem gem in _gems.AsEnumerable().Reverse().ToList())
            Unload(gem);
    }

    /// <summary>
    /// Applies every queued change whose file has been quiet for <see cref="Settle"/>. Called by the main
    /// loop at the start of each frame, so gems never come or go in the middle of one.
    /// </summary>
    public void ProcessChanges()
    {
        CheckUnloaded();

        List<(string Path, bool Deleted)> ready;
        lock (_pendingLock)
        {
            ready = [.. _pending.Where(p => Stopwatch.GetElapsedTime(p.Value.Stamp) >= Settle).Select(p => (p.Key, p.Value.Deleted))];
            foreach ((string path, _) in ready)
                _pending.Remove(path);
        }

        foreach ((string path, bool deleted) in ready)
        {
            try
            {
                if (deleted)
                    Unload(path);
                else
                    Reload(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.LogWarning($"Handling a change to {path} failed. {ex}");
            }
        }
    }

    /// <summary>Loads every gem dll in <paramref name="directory"/> in dependency order and watches the folder for changes.</summary>
    public void Load(string directory)
    {
        Watch(directory);
        Result<string[]> listing = files.ReadDirectory(directory, "*.dll");
        if (listing.Failed)
        {
            Debugging.LogWarning($"Could not list gems in {directory}: {listing.Message}");
            return;
        }
        Load(listing.Payload, []);
    }

    /// <summary>
    /// Loads the gems at <paramref name="paths"/>: inspects each, then constructs them in dependency order,
    /// loggers first. <paramref name="state"/> is hot reload state by path, handed back to the rebuilt gem.
    /// </summary>
    private void Load(IEnumerable<string> paths, Dictionary<string, byte[]> state)
    {
        List<Gem> pending = [];
        foreach (string path in paths)
        {
            if (Find(path) is not null)
                Debugging.LogWarning($"{path} is already loaded.");
            else if (Parse(path) is { } gem)
                pending.Add(gem);
        }

        // Kahn's algorithm by hand: take any gem whose needs are all met (services registered by the host or a
        // gem already up, and every gem it names in DependsOn loaded).
        while (pending.Count > 0)
        {
            Gem? next = pending
                .OrderBy(g => g.Provides.Contains(typeof(ILogger)) ? 0 : 1).ThenBy(g => g.Name)
                .FirstOrDefault(g => g.Requires.All(t => container.IsRegistered(t)) && g.DependsOn.All(n => Find(n) is not null || g.Name == n));
            if (next is null)
                break;
            pending.Remove(next);

            if (Construct(next, state.GetValueOrDefault(next.Path)))
            {
                _gems.Add(next);
                Debugging.LogInfo($"Loaded gem: {next.Name} v{next.Version}{(next.Author is null ? "" : $" by {next.Author}")}{(next.IsStatic ? " (static)" : "")}");
            }
            else
                next.Context.Unload();
        }

        // Whatever is left needs something nobody provides, or sits in a dependency cycle.
        foreach (Gem gem in pending)
        {
            string missing = gem.Requires.FirstOrDefault(t => !container.IsRegistered(t)) is { } type
                ? $"nothing provides {type}"
                : $"gem {gem.DependsOn.First(n => Find(n) is null)} is not loaded";
            Debugging.LogWarning($"Skipping gem {gem.Name} ({gem.Path}): {missing}.");
            gem.Context.Unload();
        }
    }

    /// <summary>
    /// Loads the assembly into its own collectible context and reads what its gem declares, without
    /// constructing anything. Null (context unloaded again) for anything that is not a usable gem.
    /// </summary>
    private Gem? Parse(string path)
    {
        Result<byte[]> read = files.ReadBinary(path);
        if (read.Failed)
        {
            Debugging.LogDebug($"Skipping {path} for now: {read.Message}"); // still being written; the next change event retries
            return null;
        }
        if (!IsGemFile(read.Payload))
            return null;

        LoadContext context = new(path, read.Payload, files);
        try
        {
            Type[] types;
            try
            {
                types = context.LoadGem().GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }

            Type[] gemTypes = [.. types.Where(t => t.IsDefined(typeof(GemAttribute), inherit: false))];
            if (gemTypes.Length == 0)
            {
                context.Unload(); // a helper library that happens to reference the host
                return null;
            }
            if (gemTypes.Length > 1)
                Debugging.LogWarning($"{path} holds several [Gem] classes; using {gemTypes[0].FullName} and ignoring the rest.");
            Type gemType = gemTypes[0];

            // The gem class first, so it is constructed first unless it needs one of its own exports.
            List<GemPart> parts = [];
            foreach (Type type in types.Where(t => t != gemType && t.IsDefined(typeof(GemExportAttribute), inherit: false)).Prepend(gemType))
            {
                if (GemPart.Of(type) is not { } part)
                {
                    context.Unload();
                    return null;
                }
                parts.Add(part);
            }
            return new Gem(context, gemType, parts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.LogWarning($"Skipping {path}: could not inspect it. {ex}");
            context.Unload();
            return null;
        }
    }

    /// <summary>
    /// Constructs the gem class and its exports, registers the exports, and hands back hot reload
    /// <paramref name="state"/>. False (with everything torn down again) on any failure.
    /// </summary>
    private bool Construct(Gem gem, byte[]? state)
    {
        try
        {
            // Same rule as between gems: build whichever part has everything its constructor needs.
            List<GemPart> parts = [.. gem.Parts];
            while (parts.Count > 0)
            {
                GemPart part = parts.FirstOrDefault(p => p.Requires.All(t => container.IsRegistered(t)))
                    ?? throw new InvalidOperationException($"{parts[0].Type.FullName} needs {parts[0].Requires.First(t => !container.IsRegistered(t))}, which nothing provides.");
                parts.Remove(part);

                object instance = part.Ctor.Invoke([.. part.Requires.Select(t => container.Resolve(t))]);
                gem.Instances.Add(instance);
                if (part.Type == gem.Type)
                    gem.Instance = instance;

                foreach (Type contract in part.Contracts)
                {
                    if (Register(contract, instance))
                        gem.Registrations.Add(contract);
                }

                if (instance is ILogger logger && part.Contracts.Contains(typeof(ILogger)))
                {
                    gem.Loggers.Add(logger);
                    Debugging.RegisterLogger(logger);
                }
                else if (instance is IAssetLoader loader)
                {
                    // Also kept by the asset type each declared IAssetLoader<T> contract names, since a type can only have one loader.
                    foreach (Type assetType in part.Contracts
                        .Where(c => c.IsGenericType && c.GetGenericTypeDefinition() == typeof(IAssetLoader<>))
                        .Select(c => c.GenericTypeArguments[0]))
                    {
                        AssetLoaderRegistry.Register(assetType, loader);
                        gem.Loaders.Add((assetType, loader));
                    }
                }
            }

            if (state is not null && gem.Instance is IHotReloadable reloadable)
                reloadable.Restore(state);

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Log the text only: holding on to the exception would pin the gem's types.
            Debugging.LogWarning($"Failed to load gem {gem.Name}: {(ex as TargetInvocationException)?.InnerException ?? ex}");
            Teardown(gem);
            return false;
        }
    }

    /// <summary>
    /// Offers an instance a gem owns to the host and other gems. Registered weakly: the gem keeps the instance
    /// alive while it is loaded, so nothing in the container pins the gem's assembly after it unloads. First
    /// gem to provide a contract wins; a later one is logged and ignored.
    /// </summary>
    private bool Register(Type contract, object instance)
    {
        if (container.IsRegistered(contract))
        {
            Debugging.LogWarning($"{contract} is already provided; keeping the existing one and ignoring {instance.GetType().FullName}.");
            return false;
        }
        container.RegisterInstance(contract, instance, IfAlreadyRegistered.Replace,
            // asResolutionCall keeps consumers from inlining this instance into their cached resolve expressions
            Setup.With(weaklyReferenced: true, asResolutionCall: true));
        return true;
    }

    /// <summary>Queues dll changes in <paramref name="directory"/> (which must exist); <see cref="ProcessChanges"/> applies them.</summary>
    private void Watch(string directory)
    {
        _watcher?.Dispose();
        _watcher = files.Watch(directory, "*.dll", path => Notify(path, deleted: false), path => Notify(path, deleted: true));
    }

    /// <summary>Newest change per path wins. Safe from any thread.</summary>
    private void Notify(string path, bool deleted)
    {
        lock (_pendingLock)
        {
            _pending[path] = (deleted, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// The frame after a reload: were the old assemblies collected? One still loaded means something kept a
    /// reference into the old gem (a delegate, an instance, an export), and the reload leaked it.
    /// </summary>
    private void CheckUnloaded()
    {
        if (_unloaded.Count == 0)
            return;
        // Unloading takes a few collections to go through; this is the documented way to wait it out.
        for (int i = 0; i < 10 && _unloaded.Any(u => u.TryGetTarget(out _)); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        foreach (WeakReference<LoadContext> reference in _unloaded)
        {
            if (reference.TryGetTarget(out LoadContext? context))
                Debugging.LogWarning($"The old assembly of {context.Path} is still loaded after its reload: a system, subscription or other handle it registered was not disposed. Every handle a gem takes from a host service must be disposed in its Dispose.");
        }
        _unloaded.Clear();
    }

    /// <summary>
    /// Hot reload: saves state from the gem and everything depending on it, unloads them all (dependents
    /// first), then loads them again in dependency order and hands the state back. An unknown path is just loaded.
    /// </summary>
    private void Reload(string path)
    {
        if (Find(path) is not { } gem)
        {
            Load([path], []);
            return;
        }
        if (Group(gem, "reloading") is not { } group)
            return;

        Dictionary<string, byte[]> state = [];
        foreach (Gem g in group.AsEnumerable().Reverse())
        {
            try
            {
                if (g.Instance is IHotReloadable reloadable)
                    state[g.Path] = reloadable.Persist();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.LogWarning($"Gem {g.Name}: saving reload state failed. {ex}");
            }
            _unloaded.Add(new WeakReference<LoadContext>(g.Context)); // checked next frame
            Unload(g);
        }
        Load([.. group.Select(g => g.Path)], state);
    }

    /// <summary>Unloads the gem at <paramref name="path"/> and everything depending on it, dependents first.</summary>
    private void Unload(string path)
    {
        if (Find(path) is { } gem && Group(gem, "unloading") is { } group)
        {
            foreach (Gem g in group.AsEnumerable().Reverse())
                Unload(g);
        }
    }

    private void Unload(Gem gem)
    {
        Debugging.LogInfo($"Unloading gem: {gem.Name}");
        _gems.Remove(gem);
        Teardown(gem);
        gem.Context.Unload();
    }

    /// <summary>
    /// Unregisters everything the gem provided and disposes what was built: the gem class first, so it can
    /// still use its exports, then the exports newest first. Loggers are flushed before they go.
    /// </summary>
    private void Teardown(Gem gem)
    {
        foreach (Type contract in gem.Registrations)
        {
            container.Unregister(contract, null, FactoryType.Service, null);
            container.ClearCache(contract, FactoryType.Service, null); // compiled resolve delegates pin the gem's types too
        }
        if (gem.Loggers.Count > 0)
        {
            Debugging.Flush();
            gem.Loggers.ForEach(Debugging.UnregisterLogger);
        }
        foreach ((Type assetType, IAssetLoader loader) in gem.Loaders)
            AssetLoaderRegistry.Unregister(assetType, loader);

        IEnumerable<object> order = gem.Instances.AsEnumerable().Reverse();
        if (gem.Instance is not null)
            order = order.Where(i => i != gem.Instance).Prepend(gem.Instance);
        foreach (IDisposable instance in order.OfType<IDisposable>())
        {
            try
            {
                instance.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.LogWarning($"Gem {gem.Name}: disposing {instance.GetType().FullName} failed. {ex}");
            }
        }

        gem.Instance = null;
        gem.Instances.Clear();
        gem.Registrations.Clear();
        gem.Loggers.Clear();
        gem.Loaders.Clear();
    }

    /// <summary>
    /// <paramref name="gem"/> and every gem that (transitively) needs something it registered or names it
    /// in DependsOn, in load order; or null, logged, when one of them is static and so cannot go.
    /// </summary>
    private List<Gem>? Group(Gem gem, string action)
    {
        HashSet<Gem> group = [gem];
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Gem other in _gems)
            {
                if (!group.Contains(other) && group.Any(g => g.Registrations.Any(other.Requires.Contains) || other.DependsOn.Contains(g.Name)))
                    grew |= group.Add(other);
            }
        }

        if (group.FirstOrDefault(g => g.IsStatic) is { } pinned)
        {
            string who = pinned == gem ? "it" : $"{pinned.Name}, which depends on it,";
            Debugging.LogWarning($"Not {action} {gem.Path}: {who} is static. Restart to apply the change.");
            return null;
        }
        return [.. _gems.Where(group.Contains)];
    }

    /// <summary>The loaded gem at <paramref name="path"/> or, failing that, with that name (as DependsOn refers to it).</summary>
    private Gem? Find(string path)
    {
        return _gems.Find(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? _gems.Find(g => string.Equals(g.Name, path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Cheap check, without loading anything, that a file is a managed assembly referencing the host. Gems
    /// land in a flat folder next to their own dependencies and native dlls, and none of those reference it.
    /// </summary>
    private static bool IsGemFile(byte[] bytes)
    {
        try
        {
            using PEReader pe = new(new MemoryStream(bytes));
            if (!pe.HasMetadata)
                return false; // native dll
            MetadataReader metadata = pe.GetMetadataReader();
            return metadata.IsAssembly && metadata.AssemblyReferences
                .Any(h => metadata.GetString(metadata.GetAssemblyReference(h).Name) == HostAssembly);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return false; // not a PE file
        }
    }

    /// <summary>A loaded gem: metadata copied out of its attribute, plus what was built and registered for it.</summary>
    internal sealed class Gem
    {
        public Gem(LoadContext context, Type type, List<GemPart> parts)
        {
            GemAttribute meta = type.GetCustomAttribute<GemAttribute>()!;
            Context = context;
            Type = type;
            Parts = parts;
            Name = meta.Name;
            Version = meta.Version;
            Description = meta.Description;
            Author = meta.Author;
            IsStatic = meta.Static;
            DependsOn = meta.DependsOn;
            Provides = [.. parts.SelectMany(p => p.Contracts)];
            Requires = [.. parts.SelectMany(p => p.Requires).Where(t => !Provides.Contains(t))];
        }

        public LoadContext Context { get; }
        public string Path => Context.Path;
        public Type Type { get; }
        public List<GemPart> Parts { get; }

        public string Name { get; }
        public string Version { get; }
        public string Description { get; }
        public string? Author { get; }
        public bool IsStatic { get; }

        /// <summary>Names of gems this one must load after and unload before.</summary>
        public string[] DependsOn { get; }

        /// <summary>Contracts this gem's exports register.</summary>
        public HashSet<Type> Provides { get; }

        /// <summary>Types this gem's constructors need from outside itself: host services or other gems' exports.</summary>
        public HashSet<Type> Requires { get; }


        /// <summary>The <see cref="GemAttribute"/> class instance.</summary>
        public object? Instance { get; set; }
        /// <summary>Every instance built for this gem, in construction order (includes <see cref="Instance"/>).</summary>
        public List<object> Instances { get; } = [];

        /// <summary>Contracts this gem really registered (not ones another gem already provided).</summary>
        public List<Type> Registrations { get; } = [];

        public List<ILogger> Loggers { get; } = [];

        /// <summary>Asset loaders this gem put in <see cref="AssetLoaderRegistry"/>, by asset type.</summary>
        public List<(Type AssetType, IAssetLoader Loader)> Loaders { get; } = [];
    }

    /// <summary>One class to construct: the gem class or an export. Reflection metadata only, no instances.</summary>
    internal sealed record GemPart(Type Type, ConstructorInfo Ctor, Type[] Contracts)
    {
        public IEnumerable<Type> Requires => Ctor.GetParameters().Select(p => p.ParameterType);

        /// <summary>Reads a class's constructor and contracts; null, logged, when it cannot be a gem part.</summary>
        public static GemPart? Of(Type type)
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition)
            {
                Debugging.LogWarning($"Skipping {type.FullName}: a gem class or export must be a concrete, non-generic class.");
                return null;
            }
            ConstructorInfo? ctor = type.GetConstructors().MaxBy(c => c.GetParameters().Length);
            if (ctor is null)
            {
                Debugging.LogWarning($"Skipping {type.FullName}: it has no public constructor.");
                return null;
            }

            Type[] contracts = type.GetCustomAttribute<GemExportAttribute>()?.Contracts ?? [];
            if (contracts.FirstOrDefault(c => !c.IsAssignableFrom(type)) is { } bad)
            {
                Debugging.LogWarning($"Skipping {type.FullName}: it does not implement its declared contract {bad}.");
                return null;
            }
            return new GemPart(type, ctor, contracts);
        }
    }

    /// <summary>
    /// One gem's collectible load context. The assembly and its managed dependencies are read through
    /// <see cref="IFileSystem"/> and loaded from memory, so their files stay free for the next build while
    /// they are loaded (<see cref="Assembly.Location"/> is therefore empty for gem code; use <see cref="Project"/>
    /// for paths). Native libraries stay file-mapped; they only change when a package does.
    /// </summary>
    internal sealed class LoadContext(string path, byte[] bytes, IFileSystem files) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        public string Path { get; } = path;

        public Assembly LoadGem()
        {
            return LoadBytes(Path, bytes);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Anything the host already has loaded (Core, DryIoc, ...) must be shared, never loaded a second
            // time here: types from two copies of one assembly are not interchangeable, and the gem's [Gem]
            // attribute would not be the host's GemAttribute.
            if (Default.Assemblies.Any(a => a.GetName().Name == assemblyName.Name))
                return null;
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath is null)
                return null;
            Result<byte[]> read = files.ReadBinary(assemblyPath);
            return read.Ok ? LoadBytes(assemblyPath, read.Payload) : throw new FileLoadException(read.Message, assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }

        /// <summary>Loads an assembly from its bytes, with its portable pdb when one sits next to it (symbols are optional).</summary>
        private Assembly LoadBytes(string file, byte[] assembly)
        {
            string pdbPath = System.IO.Path.ChangeExtension(file, ".pdb");
            Result<byte[]>? pdb = files.Exists(pdbPath) ? files.ReadBinary(pdbPath) : null;
            return LoadFromStream(new MemoryStream(assembly), pdb is { Ok: true } ? new MemoryStream(pdb.Payload) : null);
        }
    }
}
