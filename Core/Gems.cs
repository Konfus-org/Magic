using Magic.Contexts.Events;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Magic;

/// <summary>
/// The whole gem system: load, watch, reload, unload.
///
/// <para>A gem is one dll holding one class that implements <see cref="IGem"/>. Its name is the assembly name; the
/// gem's csproj stamps whether it is static and which gems it depends on into the assembly
/// (<c>GemStatic</c>, <c>GemDependsOn</c>, see Directory.Build.props). The class is constructed once, its constructor
/// parameters taken from the <see cref="Container"/>, and put in the container under every Core interface it
/// implements. What its constructor needs is what it depends on: gems load after whatever provides that, and unload
/// before it. A static gem is never unloaded before shutdown, and neither is anything it depends on. Whatever a gem
/// takes from a host service (an event watch, an ECS query) the gem disposes in its own Dispose: the host tracks none
/// of it, and a handle left behind keeps the old assembly alive after a reload.</para>
///
/// <para>A dll with no <see cref="IGem"/> class but with <see cref="IScript"/> classes is a project's scripts: it is
/// loaded, watched and reloaded the same way, only nothing is constructed here. The script system makes the
/// instances, and hears of a reload as it does of any gem's.</para>
///
/// Gems come from two places: the engine's own folder, of which the project lists the ones it wants by name
/// (<c>"default"</c> for all of them), and the project's folder, searched top to bottom, from which everything loads.
/// One name loads once: of a project gem built in several configurations, the build in the host's own is taken.
///
/// Gems only change on the main thread: <see cref="Load"/> at startup, <see cref="ProcessChanges"/> at the top of
/// every frame, and <see cref="Dispose"/> on the way out. The folder watchers only queue changes.
/// </summary>
internal sealed class Gems(Container container, IFileSystem files, Events events) : IDisposable
{
    /// <summary>The list entry that stands for every gem in the engine folder.</summary>
    internal const string Default = "default";

    private static readonly Assembly Host = typeof(IGem).Assembly;

    /// <summary>The configuration the host was built in (Debug, Release...), which is a folder of a project's build output.</summary>
    private static readonly string Configuration = Host.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "";

    private readonly List<Gem> _gems = []; // in load order
    private readonly List<GemSource> _sources = []; // engine folder first, so a path under both is the engine's
    private readonly List<IDisposable> _watchers = [];
    private readonly ChangeQueue _changes = new();
    private readonly List<WeakReference<GemLoadContext>> _unloaded = [];

    /// <summary>Every loaded gem, in load order (dependencies first): the order the frame loop calls them in.</summary>
    public IGem[] Loaded { get; private set; } = [];

    public void Dispose()
    {
        _watchers.ForEach(watcher => watcher.Dispose());
        _watchers.Clear();
        _sources.Clear();

        foreach (Gem gem in _gems.Where(loaded => !loaded.Provides.Contains(typeof(ILogger))).Reverse().ToList())
            Unload(gem);

        foreach (Gem gem in _gems.AsEnumerable().Reverse().ToList())
            Unload(gem);
    }

    /// <summary>
    /// Loads, in one dependency-ordered pass, the gems in <paramref name="engineDirectory"/> whose name is in
    /// <paramref name="names"/> (<see cref="Default"/> takes them all) and every gem dll anywhere under
    /// <paramref name="projectRoot"/>, then watches both for changes. The project tree is left alone when the engine
    /// folder sits inside it (the engine running as its own project), and <c>obj</c>, <c>Cache</c> and dot folders are
    /// skipped: a build's intermediate copy of a dll is not a second gem.
    /// </summary>
    public void Load(string engineDirectory, IReadOnlyCollection<string> names, string projectRoot)
    {
        HashSet<string>? wanted = names.Contains(Default) ? null : new HashSet<string>(names, StringComparer.Ordinal);
        GemSource engine = new(engineDirectory, Recursive: false, wanted);
        List<string> paths = [];

        Add(engine, paths);
        if (!IsUnder(engineDirectory, projectRoot))
            Add(new GemSource(projectRoot, Recursive: true, Names: null), paths);

        HashSet<string> seen = Load(paths, []);

        foreach (string name in engine.Names?.Where(listed => !seen.Contains(listed)) ?? [])
            Debugging.Log.Warn($"Gem \"{name}\" is listed in the project but no dll in {engineDirectory} provides it.");
    }

    /// <summary>
    /// Applies every gem file change that has settled. Called by the frame loop at the start of each frame, so gems
    /// never come or go in the middle of one.
    /// </summary>
    public void ProcessChanges()
    {
        CheckUnloaded();

        foreach (string path in _changes.TakeSettled())
        {
            if (SourceOf(path) is null)
                continue;

            try
            {
                if (files.FileExists(path))
                    Reload(path);
                else
                    Unload(path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.Log.Warn($"Handling a change to {path} failed. {ex}");
            }
        }
    }

    /// <summary>Registers a source, lists the dlls it accepts into <paramref name="paths"/> (once each) and watches it.</summary>
    private void Add(GemSource source, List<string> paths)
    {
        _sources.Add(source);

        Result<string[]> listing = source.Recursive
            ? files.ReadDirectoryRecursive(source.Directory, "*.dll")
            : files.ReadDirectory(source.Directory, "*.dll");
        if (listing.Failed)
        {
            Debugging.Log.Warn($"Could not list gems in {source.Directory}: {listing.Message}");
            return;
        }

        // A project keeps a build per configuration; the one matching the host comes first and so is the one loaded.
        foreach (string path in listing.Payload.OrderBy(path => path.Split('\\', '/').Contains(Configuration, StringComparer.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (SourceOf(path) == source && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        }

        _watchers.Add(files.Watch(source.Directory, "*.dll", _changes.Add, source.Recursive));
    }

    /// <summary>Is <paramref name="path"/> <paramref name="root"/> itself or somewhere beneath it?</summary>
    private bool IsUnder(string path, string root)
    {
        string relative = files.Relative(root, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    /// <summary>The source <paramref name="path"/> falls under, engine folder first; null when no source accepts it.</summary>
    private GemSource? SourceOf(string path)
    {
        return _sources.Find(source => source.Accepts(files, path));
    }

    /// <summary>
    /// Loads the gems at <paramref name="paths"/>: inspects each, then constructs them in dependency order, loggers
    /// first. <paramref name="state"/> is hot reload state by path, handed back to the rebuilt gem. Returns the name of
    /// every gem inspected, wanted by its source or not.
    /// </summary>
    private HashSet<string> Load(IEnumerable<string> paths, Dictionary<string, byte[]> state)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<Gem> pending = [];
        foreach (string path in paths)
        {
            if (Find(path) is not null)
            {
                Debugging.Log.Warn($"{path} is already loaded.");
                continue;
            }

            if (Parse(path) is not { } gem)
                continue;

            seen.Add(gem.Name);
            if ((Find(gem.Name) ?? pending.Find(other => other.Name == gem.Name)) is { } loaded)
            {
                Debugging.Log.Verbose($"Skipping {path}: {gem.Name} is loaded from {loaded.Path}.");
                gem.Context.Unload();
                continue;
            }

            if (SourceOf(path) is { } source && source.Wants(gem))
            {
                pending.Add(gem);
                continue;
            }

            Debugging.Log.Verbose($"Skipping gem {gem.Name} ({path}): the project does not list it.");
            gem.Context.Unload();
        }

        // Kahn's algorithm by hand: take any gem whose needs are all met (services in the container and every gem it
        // names in GemDependsOn loaded).
        while (pending.Count > 0)
        {
            Gem? next = pending
                .OrderBy(candidate => candidate.Provides.Contains(typeof(ILogger)) ? 0 : 1).ThenBy(candidate => candidate.Name)
                .FirstOrDefault(candidate => candidate.Requires.All(container.Has) && candidate.DependsOn.All(dependency => Find(dependency) is not null));
            if (next is null)
                break;

            pending.Remove(next);
            if (!Construct(next, state.GetValueOrDefault(next.Path)))
            {
                next.Context.Unload();
                continue;
            }

            _gems.Add(next);
            PublishChanged();

            string pinned = next.IsStatic ? " (static)" : "";
            string kind = next.Type is null ? "scripts" : "gem";
            Debugging.Log.Info($"Loaded {kind}: {next.Name} v{next.Version}{pinned}");
        }

        // Whatever is left needs something nobody provides, or sits in a dependency cycle.
        foreach (Gem gem in pending)
        {
            string missing = gem.Requires.FirstOrDefault(required => !container.Has(required)) is { } type
                ? $"nothing provides {type}"
                : $"gem {gem.DependsOn.First(dependency => Find(dependency) is null)} is not loaded";
            Debugging.Log.Warn($"Skipping gem {gem.Name} ({gem.Path}): {missing}.");
            gem.Context.Unload();
        }

        return seen;
    }

    /// <summary>
    /// Loads the assembly into its own collectible context and reads what the gem declares, without constructing
    /// anything. Null (context unloaded again) for anything that is not a usable gem.
    /// </summary>
    private Gem? Parse(string path)
    {
        Result<byte[]> read = files.ReadBinary(path);
        if (read.Failed)
        {
            Debugging.Log.Verbose($"Skipping {path} for now: {read.Message}"); // still being written; the next change retries
            return null;
        }

        if (!IsGemFile(read.Payload))
            return null;

        GemLoadContext context = new(path, read.Payload, files);
        try
        {
            Assembly assembly = context.LoadGem();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.OfType<Type>()];
            }

            Type[] gemTypes = [.. types.Where(candidate => typeof(IGem).IsAssignableFrom(candidate) && !candidate.IsAbstract && !candidate.IsInterface)];
            if (gemTypes.Length == 0)
            {
                // A project's scripts stay loaded with nothing to construct; anything else is a helper library that
                // happens to reference the host.
                if (types.Any(candidate => typeof(IScript).IsAssignableFrom(candidate) && !candidate.IsAbstract && !candidate.IsInterface))
                    return new Gem(context, assembly, null);

                context.Unload();
                return null;
            }

            if (gemTypes.Length > 1)
                Debugging.Log.Warn($"{path} holds several IGem classes; using {gemTypes[0].FullName} and ignoring the rest.");

            if (gemTypes[0].Constructor() is null)
            {
                Debugging.Log.Warn($"Skipping {gemTypes[0].FullName}: it has no public constructor.");
                context.Unload();
                return null;
            }

            return new Gem(context, assembly, gemTypes[0]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Warn($"Skipping {path}: could not inspect it. {ex}");
            context.Unload();
            return null;
        }
    }

    /// <summary>
    /// Constructs the gem, puts it in the container and hands back hot reload <paramref name="state"/>. False, logged,
    /// on failure. A project's scripts have no gem to construct: true, with nothing done.
    /// </summary>
    private bool Construct(Gem gem, byte[]? state)
    {
        if (gem.Type is null)
            return true;

        try
        {
            gem.Instance = (IGem)gem.Type.Create(container);

            // Core decides when rendering debugs, before a gem constructed after this one can reach the renderer.
#if DEBUG
            if (gem.Instance is IRendering rendering)
                rendering.Debug = true;
#endif

            foreach (Type contract in gem.Provides)
                container.Add(contract, gem.Instance);

            // Loggers and debug UIs are also driven through Debugging, which fans every call out to all of them.
            if (gem.Instance is ILogger logger)
                Debugging.Log.Register(logger);
            if (gem.Instance is IDebugUI ui)
                Debugging.UI.Register(ui);

            if (state is not null)
                gem.Instance.Reloaded(state);

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Log the text only: holding on to the exception would pin the gem's types.
            Debugging.Log.Warn($"Failed to load gem {gem.Name}: {ex}");
            Teardown(gem);
            return false;
        }
    }

    /// <summary>
    /// The frame after a reload: were the old assemblies collected? One still loaded means something kept a reference
    /// into the old gem (a delegate, an instance, an export), and the reload leaked it.
    /// </summary>
    private void CheckUnloaded()
    {
        if (_unloaded.Count == 0)
            return;

        // Unloading takes a few collections to go through; this is the documented way to wait it out.
        for (int i = 0; i < 10 && _unloaded.Any(context => context.TryGetTarget(out _)); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        foreach (WeakReference<GemLoadContext> reference in _unloaded)
        {
            if (reference.TryGetTarget(out GemLoadContext? context))
                Debugging.Log.Warn($"The old assembly of {context.Path} is still loaded after its reload: an event watch, query or other handle it took was not disposed. Every handle a gem takes from a host service must be disposed in its Dispose.");
        }

        _unloaded.Clear();
    }

    /// <summary>
    /// Hot reload: saves state from the gem and everything depending on it, unloads them all (dependents first), then
    /// loads them again in dependency order and hands the state back. An unknown path is just loaded.
    /// </summary>
    private void Reload(string path)
    {
        if (Find(path) is not { } gem)
        {
            Load([path], []);
            return;
        }

        if (WithDependents(gem, "reloading") is not { } group)
            return;

        Dictionary<string, byte[]> state = [];
        foreach (Gem member in group.AsEnumerable().Reverse())
        {
            try
            {
                if (member.Instance is { } instance)
                    state[member.Path] = instance.Reloading();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Debugging.Log.Warn($"Gem {member.Name}: Reloading failed. {ex}");
            }

            _unloaded.Add(new WeakReference<GemLoadContext>(member.Context)); // checked next frame
            Unload(member);
        }

        Load([.. group.Select(member => member.Path)], state);
    }

    /// <summary>Unloads the gem at <paramref name="path"/> and everything depending on it, dependents first.</summary>
    private void Unload(string path)
    {
        if (Find(path) is not { } gem || WithDependents(gem, "unloading") is not { } group)
            return;

        foreach (Gem member in group.AsEnumerable().Reverse())
            Unload(member);
    }

    private void Unload(Gem gem)
    {
        Debugging.Log.Verbose($"Unloading {(gem.Type is null ? "scripts" : "gem")}: {gem.Name}");
        _gems.Remove(gem);
        PublishChanged();
        Teardown(gem);
        gem.Context.Unload();
    }

    /// <summary>The set of gems changed: the frame loop's list follows, and whoever caches types by name hears of it next frame.</summary>
    private void PublishChanged()
    {
        Loaded = [.. _gems.Select(loaded => loaded.Instance).OfType<IGem>()];
        events.Publish(new Event(EventType.GemsChanged));
    }

    /// <summary>Takes the gem out of the container and Debugging and disposes it; a logger is flushed before it goes.</summary>
    private void Teardown(Gem gem)
    {
        if (gem.Instance is null)
            return;

        container.Remove(gem.Instance);

        if (gem.Instance is ILogger logger)
        {
            Debugging.Log.Flush();
            Debugging.Log.Unregister(logger);
        }

        if (gem.Instance is IDebugUI ui)
            Debugging.UI.Unregister(ui);

        try
        {
            gem.Instance.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debugging.Log.Warn($"Gem {gem.Name}: Dispose failed. {ex}");
        }

        gem.Instance = null;
    }

    /// <summary>
    /// <paramref name="gem"/> and every gem that (transitively) needs something it provides or names it in
    /// GemDependsOn, in load order; or null, logged, when one of them is static and so cannot go.
    /// </summary>
    private List<Gem>? WithDependents(Gem gem, string action)
    {
        HashSet<Gem> group = [gem];
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Gem other in _gems)
            {
                if (!group.Contains(other) && group.Any(member => member.Provides.Overlaps(other.Requires) || other.DependsOn.Contains(member.Name)))
                    grew |= group.Add(other);
            }
        }

        if (group.FirstOrDefault(member => member.IsStatic) is { } pinned)
        {
            string who = pinned == gem ? "it" : $"{pinned.Name}, which depends on it,";
            Debugging.Log.Warn($"Not {action} {gem.Path}: {who} is static. Restart to apply the change.");
            return null;
        }

        return [.. _gems.Where(group.Contains)];
    }

    /// <summary>The loaded gem at <paramref name="pathOrName"/>, or failing that with that name (as GemDependsOn refers to it).</summary>
    private Gem? Find(string pathOrName)
    {
        return _gems.Find(loaded => string.Equals(loaded.Path, pathOrName, StringComparison.OrdinalIgnoreCase))
            ?? _gems.Find(loaded => string.Equals(loaded.Name, pathOrName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Cheap check, without loading anything, that a file is a managed assembly referencing the host. Gems land in a
    /// flat folder next to their own dependencies and native dlls, and none of those reference it.
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
                .Any(reference => metadata.GetString(metadata.GetAssemblyReference(reference).Name) == Host.GetName().Name);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return false; // not a PE file
        }
    }

    /// <summary>
    /// A gem: what its assembly declares, read before construction, and the instance once built. A project's scripts
    /// are one without a <see cref="Type"/>: an assembly kept loaded, with no instance.
    /// </summary>
    private sealed class Gem
    {
        public Gem(GemLoadContext context, Assembly assembly, Type? type)
        {
            Context = context;
            Type = type;
            Name = assembly.GetName().Name!;
            Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
            IsStatic = bool.TryParse(Metadata(assembly, "MagicGem.Static"), out bool isStatic) && isStatic;
            DependsOn = Metadata(assembly, "MagicGem.DependsOn")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            Provides = [.. type?.GetInterfaces().Where(i => i.Assembly == Host && i != typeof(IGem)) ?? []];
            Requires = [.. type?.Constructor()?.GetParameters().Select(parameter => parameter.ParameterType) ?? []];
        }

        public GemLoadContext Context { get; }

        /// <summary>The gem's class; null for a project's scripts.</summary>
        public Type? Type { get; }

        public string Path => Context.Path;

        public string Name { get; }

        public string Version { get; }

        public bool IsStatic { get; }

        /// <summary>Names of gems this one must load after and unload before.</summary>
        public string[] DependsOn { get; }

        /// <summary>The Core interfaces the gem class implements: what it is put in the container under.</summary>
        public HashSet<Type> Provides { get; }

        /// <summary>Its constructor's parameter types: host services or other gems' interfaces.</summary>
        public HashSet<Type> Requires { get; }

        public IGem? Instance { get; set; }

        private static string? Metadata(Assembly assembly, string key)
        {
            return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;
        }
    }

    /// <summary>
    /// One gem's collectible load context. The assembly and its managed dependencies are read through
    /// <see cref="IFileSystem"/> and loaded from memory, so their files stay free for the next build while they are
    /// loaded (<see cref="Assembly.Location"/> is therefore empty for gem code; use <see cref="Project"/> for paths).
    /// Native libraries stay file-mapped; they only change when a package does.
    /// </summary>
    private sealed class GemLoadContext(string path, byte[] bytes, IFileSystem files) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        public string Path { get; } = path;

        public Assembly LoadGem()
        {
            return LoadBytes(Path, bytes);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Anything the host already has loaded (Core, CommandLineParser, ...) must be shared, never loaded a
            // second time here: types from two copies of one assembly are not interchangeable, and the gem's IGem
            // would not be the host's IGem.
            if (Default.Assemblies.Any(loaded => loaded.GetName().Name == assemblyName.Name))
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
            MemoryStream? symbols = pdb is { Ok: true } ? new MemoryStream(pdb.Value.Payload) : null;
            return LoadFromStream(new MemoryStream(assembly), symbols);
        }
    }

    /// <summary>
    /// A folder gems come from: the engine's, flat and filtered to <paramref name="Names"/> (null takes all), or the
    /// project's, searched recursively with build and cache folders left out.
    /// </summary>
    private sealed record GemSource(string Directory, bool Recursive, HashSet<string>? Names)
    {
        private static readonly char[] Separators = ['\\', '/'];

        /// <summary>Does <paramref name="path"/> lie in this source's territory?</summary>
        public bool Accepts(IFileSystem files, string path)
        {
            string relative = files.Relative(Directory, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
                return false;

            string[] folders = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries)[..^1];
            return Recursive
                ? folders.All(folder => folder is not ("obj" or "Cache") && !folder.StartsWith('.'))
                : folders.Length == 0;
        }

        public bool Wants(Gem gem)
        {
            return Names is null || Names.Contains(gem.Name);
        }
    }
}
