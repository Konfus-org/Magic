using Magic.Contexts.Events;
using Magic.Contexts.Files;
using Magic.Contexts.Threading;
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
/// <para>A gem is a dll built on the host, usually holding one class that implements <see cref="IGem"/>. Its name is
/// the assembly name; the gem's csproj stamps whether it is static into the assembly (<c>GemStatic</c>, see
/// Directory.Build.props). The class is constructed once, its constructor parameters taken from the
/// <see cref="IServices"/>, and added to them under its own type and every engine interface (the host's or another
/// gem's) it implements. A gem depends on the gems its assembly references and on whatever provides what its constructor
/// needs: it loads after them, unloads before them, and reloads when they do. A static gem is never unloaded before
/// shutdown, and neither is anything that depends on it: a change to one is warned about and needs a restart. A
/// constructor parameter declared
/// nullable (<c>T?</c>) is optional: the gem loads without it, after whatever would provide it if that is coming; one
/// of an array of an engine interface (<c>T[]</c>) is every provider there is, possibly none. Whatever a gem takes
/// from a host service (an event watch, an ECS query) the gem disposes in its own Dispose: the host tracks none of it,
/// and a handle left behind keeps the old assembly alive after a reload.</para>
///
/// <para>A dll with no <see cref="IGem"/> class is a library: contract types other gems reference, or a project's
/// scripts. It is loaded, watched and reloaded the same way, only nothing is constructed. A gem a wanted gem
/// references is loaded whether the project lists it or not; one the project leaves out (<c>"-Name"</c>) leaves
/// out what references it too.</para>
///
/// Gems come from two places: the engine's own folder, of which the project lists the ones it wants by name
/// (<c>"default"</c> for all of them, <c>"-Name"</c> to leave one of those out), and the project's folder, searched
/// top to bottom, from which everything loads. One name loads once, the project's before the engine's, so a project
/// replaces an engine gem by shipping one of the same name; of a project gem built in several configurations, the
/// build in the host's own is taken.
///
/// Gems only change on one thread, the one that draws (their constructors and Dispose may touch the GPU and the
/// windows): <see cref="Load"/> at startup, <see cref="ProcessChanges"/> at the top of every frame, which hands a
/// settled change over to that thread and waits for it, and <see cref="Dispose"/> on the way out. The folder
/// watchers only queue changes.
/// </summary>
internal sealed class Gems(IServices services, IFileSystem files, Events events, Threads threads, GemResources resources) : IDisposable
{
    /// <summary>
    /// The list entry that stands for every gem in the engine folder.
    /// </summary>
    private const string Default = "default";

    private static readonly Assembly Host = typeof(IGem).Assembly;

    /// <summary>
    /// The configuration the host was built in (Debug, Release...), which is a folder of a project's build output.
    /// </summary>
    private static string Configuration { get; } = Host.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "";

    private readonly List<Gem> _gems = []; // in load order
    private readonly List<GemSource> _sources = []; // engine folder first, so a path under both is the engine's
    private readonly List<Subscription> _watchers = [];
    private readonly FileChanges _changes = new();
    private readonly List<WeakReference<GemLoadContext>> _unloaded = [];
    private readonly List<Registration> _hostRegistered = []; // the host's own types, as registrar gems took them

    /// <summary>
    /// Every loaded gem, in load order (dependencies first): the order the frame loop calls them in.
    /// </summary>
    public IGem[] Loaded { get; private set; } = [];

    public void Dispose()
    {
        _watchers.ForEach(watcher => watcher.Dispose());
        _watchers.Clear();
        _sources.Clear();

        // Reverse load order: loggers load first, so they hear every other gem go.
        foreach (Gem gem in _gems.AsEnumerable().Reverse().ToList())
            Unload(gem);
    }

    /// <summary>
    /// Tells <paramref name="registrar"/> of the engine's own types (its <c>[Settings]</c> classes, its asset types, its
    /// components), as a gem's types are told when it loads. For a registrar that is not a gem: nothing to undo, it lives
    /// as long as the process.
    /// </summary>
    internal static void RegisterHost(IServices services, ITypeRegistrar registrar)
    {
        Register(services, TypesOf(Host), registrar, []);
    }

    /// <summary>
    /// The asset folder <paramref name="assembly"/>'s build stamped into it (Directory.Build.props): its Resources folder,
    /// or where a shipped build copied it, relative to the exe. Null when it has none. For a gem, and for the host.
    /// </summary>
    internal static string? ResourcesOf(Assembly assembly)
    {
        return Metadata(assembly, "MagicGem.Resources") is { Length: > 0 } stamped
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, stamped))
            : null;
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
        HashSet<string> excluded = new(names.Where(name => name.StartsWith('-')).Select(name => name[1..]), StringComparer.Ordinal);
        HashSet<string>? wanted = names.Contains(Default) ? null : new HashSet<string>(names.Where(name => !name.StartsWith('-')), StringComparer.Ordinal);
        GemSource engine = new(engineDirectory, Recursive: false, wanted, excluded);
        List<string> paths = [];

        // The project's first: of two gems of one name, the first listed is the one loaded.
        if (!files.IsUnder(projectRoot, engineDirectory))
            Add(new GemSource(projectRoot, Recursive: true, Names: null, Excluded: []), paths);
        Add(engine, paths);

        HashSet<string> seen = Load(paths, []);

        foreach (string name in (engine.Names ?? []).Concat(engine.Excluded).Where(listed => !seen.Contains(listed)))
            Log.Warn($"Gem \"{name}\" is listed in the project but no dll in {engineDirectory} provides it.");
    }

    /// <summary>
    /// Applies every gem file change that has settled. Called by the frame loop at the start of each frame, so gems
    /// never come or go in the middle of one.
    /// </summary>
    public void ProcessChanges()
    {
        CheckUnloaded();

        string[] settled = _changes.TakeSettled();
        if (settled.Length > 0)
            threads.Invoke(ThreadId.Render, () => Apply(settled));
    }

    /// <summary>
    /// Reloads or unloads the gem of every changed file. On the render thread, while the main thread waits.
    /// </summary>
    private void Apply(string[] changed)
    {
        foreach (string path in changed)
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
                Log.Warn($"Handling a change to {path} failed. {ex}");
            }
        }
    }

    /// <summary>
    /// Registers a source, lists the dlls it accepts into <paramref name="paths"/> (once each) and watches it.
    /// </summary>
    private void Add(GemSource source, List<string> paths)
    {
        _sources.Add(source);

        Result<string[]> listing = source.Recursive
            ? files.ReadDirectoryRecursive(source.Directory, "*.dll")
            : files.ReadDirectory(source.Directory, "*.dll");
        if (listing.Failed)
        {
            Log.Warn($"Could not list gems in {source.Directory}: {listing.Message}");
            return;
        }

        // A project keeps a build per configuration; the one matching the host comes first and so is the one loaded.
        foreach (string path in listing.Payload.OrderBy(path =>
            files.Segments(path).Contains(Configuration, StringComparer.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (SourceOf(path) == source && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        }

        _watchers.Add(files.Watch(source.Directory, "*.dll", _changes.Add, source.Recursive));
    }

    /// <summary>
    /// The source <paramref name="path"/> falls under, engine folder first; null when no source accepts it.
    /// </summary>
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
        // Every file's name and references first, without loading anything: one name loads once, from its first path.
        Dictionary<string, GemFile> byName = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            if (Find(path) is not null)
            {
                Log.Warn($"{path} is already loaded.");
                continue;
            }

            if (Inspect(path) is not { } file)
                continue;

            if ((Find(file.Name)?.Path ?? byName.GetValueOrDefault(file.Name)?.Path) is { } first)
            {
                Log.Verbose($"Skipping {path}: {file.Name} is loaded from {first}.");
                continue;
            }

            byName[file.Name] = file;
        }

        // What its source wants, and every gem one of those references: a reference is a need, listed or not.
        List<GemFile> wanted = [.. byName.Values.Where(file => SourceOf(file.Path)?.Wants(file.Name) == true)];
        for (int i = 0; i < wanted.Count; i++)
        {
            foreach (string reference in wanted[i].References)
            {
                if (byName.TryGetValue(reference, out GemFile? needed) && !wanted.Contains(needed) && SourceOf(needed.Path)?.Excluded.Contains(needed.Name) != true)
                    wanted.Add(needed);
            }
        }

        foreach (GemFile file in byName.Values.Where(file => !wanted.Contains(file)))
            Log.Verbose($"Skipping gem {file.Name} ({file.Path}): the project {(SourceOf(file.Path)?.Excluded.Contains(file.Name) == true ? "leaves it out" : "does not list it")}.");

        // A static gem lives in a context that is never collected, which cannot resolve into one that is.
        foreach (GemFile file in wanted.Where(file => file.IsStatic).ToList())
        {
            string? reloadable = file.References.FirstOrDefault(reference => byName.TryGetValue(reference, out GemFile? referenced) && !referenced.IsStatic);
            if (reloadable is null)
                continue;

            Log.Warn($"Skipping gem {file.Name} ({file.Path}): it is static and references {reloadable}, which reloads; a static gem builds only on static ones.");
            wanted.Remove(file);
        }

        // Assemblies load referenced gems first, so each resolves a reference to the copy already loaded.
        List<Gem> pending = [];
        foreach (GemFile file in ReferencesFirst(wanted))
        {
            if (Parse(file, pending) is { } gem)
                pending.Add(gem);
        }

        // Kahn's algorithm by hand: take any gem whose needs are all met (services in the container and every gem it
        // references loaded), and whose optional services no gem still pending
        // would provide; when every gem is waiting on an optional one, the wait is given up. Names only break ties, so
        // the order is the same every run; nothing may rely on it.
        while (pending.Count > 0)
        {
            IEnumerable<Gem> ready = pending
                .OrderBy(candidate => candidate.Provides.Contains(typeof(ILogger)) ? 0 : 1).ThenBy(candidate => candidate.Name)
                .Where(candidate => candidate.Requires.All(services.Has)
                    && candidate.References.All(reference => !byName.ContainsKey(reference) || Find(reference) is not null));
            Gem? next = ready.FirstOrDefault(candidate => !pending.Any(other => other != candidate && other.Provides.Overlaps(candidate.Optional)))
                ?? ready.FirstOrDefault();
            if (next is null)
                break;

            pending.Remove(next);
            if (!Construct(next, state.GetValueOrDefault(next.Path)))
            {
                next.Context.Release();
                continue;
            }

            _gems.Add(next);
            PublishChanged();

            string pinned = next.IsStatic ? " (static)" : "";
            string kind = next.Type is null ? "library" : "gem";
            Log.Info($"Loaded {kind}: {next.Name} v{next.Version}{pinned}");
        }

        // Whatever is left needs something nobody provides, or a gem that is left out or failed.
        foreach (Gem gem in pending)
        {
            string missing = gem.Requires.FirstOrDefault(required => !services.Has(required)) is { } type
                ? $"nothing provides {type}"
                : $"gem {gem.References.First(reference => byName.ContainsKey(reference) && Find(reference) is null)}, which it references, is not loaded";
            Log.Warn($"Skipping gem {gem.Name} ({gem.Path}): {missing}.");
            gem.Context.Release();
        }

        return [.. byName.Keys];
    }

    /// <summary>
    /// <paramref name="files"/> ordered so that every gem comes after the gems it references.
    /// </summary>
    private static List<GemFile> ReferencesFirst(List<GemFile> files)
    {
        List<GemFile> ordered = [];
        foreach (GemFile file in files)
            Visit(file);

        return ordered;

        void Visit(GemFile file)
        {
            if (ordered.Contains(file))
                return;

            // Assembly references cannot form a cycle, so this always ends.
            foreach (GemFile referenced in files.Where(other => file.References.Contains(other.Name)))
                Visit(referenced);

            ordered.Add(file);
        }
    }

    /// <summary>
    /// Reads a dll and, without loading it, whether it is a gem (a managed assembly referencing the host, or stamped as
    /// a gem by its build), its name and
    /// the assemblies it references. Gems land in a flat folder next to their own dependencies and native dlls, and
    /// none of those reference the host. Null for anything else, or a file still being written.
    /// </summary>
    private GemFile? Inspect(string path)
    {
        Result<byte[]> read = files.ReadBinary(path);
        if (read.Failed)
        {
            Log.Verbose($"Skipping {path} for now: {read.Message}"); // still being written; the next change retries
            return null;
        }

        try
        {
            using PEReader pe = new(new MemoryStream(read.Payload));
            if (!pe.HasMetadata)
                return null; // native dll

            MetadataReader metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                return null;

            string[] references = [.. metadata.AssemblyReferences.Select(reference => metadata.GetString(metadata.GetAssemblyReference(reference).Name))];
            string? stamp = StaticStamp(metadata);
            if (!references.Contains(Host.GetName().Name))
                return null; // not built on the engine: a dependency of some gem

            bool isStatic = bool.TryParse(stamp, out bool parsed) && parsed;
            return new GemFile(path, read.Payload, metadata.GetString(metadata.GetAssemblyDefinition().Name), references, isStatic);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return null; // not a PE file
        }
    }

    /// <summary>
    /// The <c>MagicGem.Static</c> metadata every gem build stamps (Directory.Build.props), or null when there is none.
    /// Read before loading: it picks the load context.
    /// </summary>
    private static string? StaticStamp(MetadataReader metadata)
    {
        foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;

            EntityHandle type = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (type.Kind != HandleKind.TypeReference || metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Name) != nameof(AssemblyMetadataAttribute))
                continue;

            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "MagicGem.Static")
                return value.ReadSerializedString() ?? "";
        }

        return null;
    }

    /// <summary>
    /// Loads the assembly into its own context and reads what the gem declares, without constructing
    /// anything. A gem it references resolves to the one in <paramref name="pending"/> or already loaded. An assembly
    /// with no <see cref="IGem"/> class is a library (contract types, a project's scripts): kept loaded, with nothing to
    /// construct. Null (context unloaded again) for one that cannot be used.
    /// </summary>
    private Gem? Parse(GemFile file, List<Gem> pending)
    {
        GemLoadContext context = new(file.Path, file.Bytes, files, name => (pending.Find(gem => gem.Name == name) ?? Find(name))?.Assembly, collectible: !file.IsStatic);
        try
        {
            Assembly assembly = context.LoadGem();
            Type[] types = TypesOf(assembly);
            Type[] gemTypes = [.. types.Where(candidate => typeof(IGem).IsAssignableFrom(candidate) && !candidate.IsAbstract && !candidate.IsInterface)];
            if (gemTypes.Length == 0)
                return new Gem(context, assembly, types, null);

            if (gemTypes.Length > 1)
                Log.Warn($"{file.Path} holds several IGem classes; using {gemTypes[0].FullName} and ignoring the rest.");

            if (gemTypes[0].Constructor() is null)
            {
                Log.Warn($"Skipping {gemTypes[0].FullName}: it has no public constructor.");
                context.Release();
                return null;
            }

            return new Gem(context, assembly, types, gemTypes[0]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Skipping {file.Path}: could not inspect it. {ex}");
            context.Release();
            return null;
        }
    }

    /// <summary>
    /// Constructs the gem, puts it in the container and hands back hot reload <paramref name="state"/>. False, logged,
    /// on failure. A project's scripts have no gem to construct: true, with nothing done.
    /// </summary>
    private bool Construct(Gem gem, byte[]? state)
    {
        // Its assets first: the gem may look for them from its constructor on.
        if (gem.Resources is { } folder)
            resources.Add(folder);

        // Its types first: the gem's constructor, and any gem after it, may ask for what they register (its settings).
        foreach (ITypeRegistrar registrar in services.All<ITypeRegistrar>())
            Register(services, gem.Types, registrar, gem.Registered);

        if (gem.Type is null)
            return true;

        try
        {
            gem.Instance = (IGem)gem.Type.Create(services);

            foreach (Type contract in gem.Provides)
                services.Add(contract, gem.Instance);

            // A registrar is told of everything loaded before it, as it will be of everything after.
            if (gem.Instance is ITypeRegistrar registrar)
            {
                Register(services, TypesOf(Host), registrar, _hostRegistered);
                foreach (Gem loaded in _gems.Append(gem))
                    Register(services, loaded.Types, registrar, loaded.Registered);
            }

            // A logger also takes what Log writes.
            if (gem.Instance is ILogger logger)
                Log.Register(logger);

            if (state is not null)
                gem.Instance.Reloaded(state);

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Log the text only: holding on to the exception would pin the gem's types.
            Log.Warn($"Failed to load gem {gem.Name}: {ex}");
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
                Log.Warn($"The old assembly of {context.Path} is still loaded after its reload: an event watch, query or other handle it took was not disposed. Every handle a gem takes from a host service must be disposed in its Dispose.");
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

        if (WithDependents(gem) is not { } group)
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
                Log.Warn($"Gem {member.Name}: Reloading failed. {ex}");
            }

            _unloaded.Add(new WeakReference<GemLoadContext>(member.Context)); // checked next frame
            Unload(member);
        }

        Load([.. group.Select(member => member.Path)], state);
    }

    /// <summary>
    /// Unloads the gem at <paramref name="path"/> and everything depending on it, dependents first.
    /// </summary>
    private void Unload(string path)
    {
        if (Find(path) is not { } gem || WithDependents(gem) is not { } group)
            return;

        foreach (Gem member in group.AsEnumerable().Reverse())
            Unload(member);
    }

    private void Unload(Gem gem)
    {
        Log.Verbose($"Unloading {(gem.Type is null ? "scripts" : "gem")}: {gem.Name}");
        _gems.Remove(gem);
        PublishChanged();
        Teardown(gem);
        gem.Context.Release();
    }

    /// <summary>
    /// The set of gems changed: the frame loop's list follows, and whoever caches types by name hears of it next frame.
    /// </summary>
    private void PublishChanged()
    {
        Loaded = [.. _gems.Select(loaded => loaded.Instance).OfType<IGem>()];
        events.Publish(new GemsChanged());
    }

    /// <summary>
    /// Takes the gem out of the container and the log and disposes it, a logger flushed before it goes, then
    /// unregisters its types.
    /// </summary>
    private void Teardown(Gem gem)
    {
        if (gem.Instance is not null)
            Dispose(gem, gem.Instance);

        Unregister(services, gem.Registered, registrar: null);
        if (gem.Resources is { } folder)
            resources.Remove(folder);
    }

    private void Dispose(Gem gem, IGem instance)
    {
        foreach (Type contract in gem.Provides)
            services.Remove(contract, instance);

        if (instance is ITypeRegistrar registrar)
        {
            Unregister(services, _hostRegistered, registrar);
            foreach (Gem loaded in _gems.Append(gem))
                Unregister(services, loaded.Registered, registrar);
        }

        if (instance is ILogger logger)
            Log.Unregister(logger);

        try
        {
            instance.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Gem {gem.Name}: Dispose failed. {ex}");
        }

        gem.Instance = null;
    }

    /// <summary>
    /// <paramref name="gem"/> and every gem that (transitively) needs something it provides or references it, in load
    /// order; or null, warned about, when one of them is static and so cannot go.
    /// </summary>
    private List<Gem>? WithDependents(Gem gem)
    {
        HashSet<Gem> group = [gem];
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Gem other in _gems)
            {
                if (!group.Contains(other) && group.Any(member => member.Provides.Overlaps(other.Requires) || member.Provides.Overlaps(other.Optional) || member.Types.Overlaps(other.Requires) || other.References.Contains(member.Name)))
                    grew |= group.Add(other);
            }
        }

        // The changed gem's own reason first, then a dependent's.
        Gem? pinned = new[] { gem }.Concat(group).FirstOrDefault(member => member.IsStatic);
        if (pinned is not null)
        {
            string who = pinned == gem ? "it" : $"{pinned.Name}, which depends on it,";
            Log.Warn($"{gem.Name} has changed but will not be reloaded because {who} is marked as static. A restart is required to see changes.");
            return null;
        }

        return [.. _gems.Where(group.Contains)];
    }

    /// <summary>
    /// The loaded gem at <paramref name="pathOrName"/>, or failing that with that name (as a reference names it).
    /// </summary>
    private Gem? Find(string pathOrName)
    {
        return _gems.Find(loaded => string.Equals(loaded.Path, pathOrName, StringComparison.OrdinalIgnoreCase))
            ?? _gems.Find(loaded => string.Equals(loaded.Name, pathOrName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The types of <paramref name="assembly"/>, as many as load: one that names a missing dependency is left out.
    /// </summary>
    private static Type[] TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.OfType<Type>()];
        }
    }

    /// <summary>
    /// Hands the types <paramref name="registrar"/> takes to it (<see cref="ITypeRegistrar"/>), adds what it hands
    /// back to the services under the type, and adds each registration to <paramref name="registered"/>. A registrar
    /// that throws is logged and the rest go on.
    /// </summary>
    private static void Register(IServices services, IReadOnlyCollection<Type> types, ITypeRegistrar registrar, List<Registration> registered)
    {
        foreach (Type type in types.Where(type => Takes(registrar.Of, type)))
        {
            try
            {
                object? instance = registrar.Register(type);
                if (instance is not null)
                    services.Add(type, instance);

                registered.Add(new Registration(registrar, type, instance));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn($"Registering {type.FullName} with {registrar.GetType().Name} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Undoes the registrations in <paramref name="registered"/> made with <paramref name="registrar"/> (all of them
    /// for null), last first, and drops them from the list.
    /// </summary>
    private static void Unregister(IServices services, List<Registration> registered, ITypeRegistrar? registrar)
    {
        for (int i = registered.Count - 1; i >= 0; i--)
        {
            (ITypeRegistrar by, Type type, object? instance) = registered[i];
            if (registrar is not null && by != registrar)
                continue;

            if (instance is not null)
                services.Remove(type, instance);

            by.Unregister(type);
            registered.RemoveAt(i);
        }
    }

    /// <summary>
    /// Whether a registrar of <paramref name="of"/> takes <paramref name="type"/>: a concrete, non-generic type that
    /// carries <paramref name="of"/> (an attribute) or is assignable to it (a class or interface).
    /// </summary>
    private static bool Takes(Type of, Type type)
    {
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            return false;

        return typeof(Attribute).IsAssignableFrom(of) ? type.IsDefined(of, false) : of.IsAssignableFrom(type);
    }


    private static string? Metadata(Assembly assembly, string key)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;
    }

    /// <summary>
    /// One type handed to one registrar, and what it handed back for the container.
    /// </summary>
    private readonly record struct Registration(ITypeRegistrar Registrar, Type Type, object? Instance);

    /// <summary>
    /// A gem dll as read from disk, before anything of it is loaded: its bytes, its assembly name and the names of the
    /// assemblies it references.
    /// </summary>
    private sealed record GemFile(string Path, byte[] Bytes, string Name, string[] References, bool IsStatic);

    /// <summary>
    /// A gem: what its assembly declares, read before construction, and the instance once built. A library (contract
    /// types, a project's scripts) is one without a <see cref="Type"/>: an assembly kept loaded, with no instance.
    /// </summary>
    private sealed class Gem
    {
        public Gem(GemLoadContext context, Assembly assembly, Type[] types, Type? type)
        {
            Context = context;
            Assembly = assembly;
            Type = type;
            Name = assembly.GetName().Name ?? "?";
            Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
            Resources = ResourcesOf(assembly);
            Provides = type is null ? [] : [type, .. type.GetInterfaces().Where(i => i != typeof(IGem) && IsEngine(i.Assembly))];
            References = [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "")];
            Types = [.. types];
            Requires = [];
            Optional = [];

            NullabilityInfoContext nullability = new();
            foreach (ParameterInfo parameter in type?.Constructor()?.GetParameters() ?? [])
            {
                if (Magic.Extensions.TypeExtensions.ManyOf(parameter.ParameterType) is { } element)
                    Optional.Add(element); // every provider, so none is required, but each loads first
                else if (Magic.Extensions.TypeExtensions.IsOptional(nullability, parameter))
                    Optional.Add(parameter.ParameterType);
                else if (parameter.ParameterType.Assembly != assembly) // its own types are registered before it is constructed
                    Requires.Add(parameter.ParameterType);
            }
        }

        public GemLoadContext Context { get; }

        public Assembly Assembly { get; }

        /// <summary>
        /// The gem's class; null for a library.
        /// </summary>
        public Type? Type { get; }

        public string Path => Context.Path;

        public string Name { get; }

        public string Version { get; }

        /// <summary>
        /// Loaded once and never reloaded: its stamp chose a context that cannot unload (<see cref="Parse"/>).
        /// </summary>
        public bool IsStatic => !Context.IsCollectible;

        /// <summary>
        /// The gem's own asset folder, or null when it has none.
        /// </summary>
        public string? Resources { get; }

        /// <summary>
        /// What it is put in the container under: the gem class itself, so a gem can be a service of its own type, and
        /// every engine interface (the host's or a gem's) it implements.
        /// </summary>
        public HashSet<Type> Provides { get; }

        /// <summary>
        /// Names of the assemblies it references. Those that are gems load before it, unload after it, and reload it.
        /// </summary>
        public string[] References { get; }

        /// <summary>
        /// Every type of its assembly: registered before it is constructed (<see cref="ITypeRegistrar{T}"/>), so a gem
        /// that asks for one (its settings) loads after it and unloads before it.
        /// </summary>
        public HashSet<Type> Types { get; }

        /// <summary>
        /// What its types were registered as, to unregister when it goes.
        /// </summary>
        public List<Registration> Registered { get; } = [];

        /// <summary>
        /// Its constructor's parameter types that must be there: host services, other gems' interfaces and settings.
        /// </summary>
        public HashSet<Type> Requires { get; }

        /// <summary>
        /// The parameter types it takes when they are there (<c>T?</c>, and the element of a <c>T[]</c>): their
        /// providers load before it and unload after it, but it loads without them.
        /// </summary>
        public HashSet<Type> Optional { get; }

        public IGem? Instance { get; set; }

        /// <summary>
        /// The host, or a gem (an assembly built on it): where the interfaces a gem provides are declared.
        /// </summary>
        private static bool IsEngine(Assembly assembly)
        {
            return assembly == Host || assembly.GetReferencedAssemblies().Any(reference => reference.Name == Host.GetName().Name);
        }
    }

    /// <summary>
    /// One gem's load context: collectible for a gem that reloads, kept to the end for a static one (whose types, like
    /// the host's, are then cached by whatever caches types, System.Text.Json among them, which skips collectible ones).
    /// The assembly and its managed dependencies are read through
    /// <see cref="IFileSystem"/> and loaded from memory, so their files stay free for the next build while they are
    /// loaded (<see cref="Assembly.Location"/> is therefore empty for gem code; use <see cref="Project"/> for paths).
    /// Native libraries stay file-mapped; they only change when a package does. A gem it references is the one
    /// <paramref name="gems"/> finds by name, already loaded in that gem's own context, never a copy.
    /// </summary>
    private sealed class GemLoadContext(string path, byte[] bytes, IFileSystem files, Func<string, Assembly?> gems, bool collectible) : AssemblyLoadContext(isCollectible: collectible)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        public string Path { get; } = path;

        public Assembly LoadGem()
        {
            return LoadBytes(Path, bytes);
        }

        /// <summary>
        /// Lets the assemblies go: a static gem's context is not collectible and stays to the end, like the host.
        /// </summary>
        public void Release()
        {
            if (IsCollectible)
                Unload();
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Anything the host already has loaded (Core, CommandLineParser, ...) must be shared, never loaded a
            // second time here: types from two copies of one assembly are not interchangeable, and the gem's IGem
            // would not be the host's IGem.
            if (Default.Assemblies.Any(loaded => loaded.GetName().Name == assemblyName.Name))
                return null;

            if (assemblyName.Name is { } name && gems(name) is { } gem)
                return gem;

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath is null)
                return null;

            Result<byte[]> read = files.ReadBinary(assemblyPath);
            return read.Ok ? LoadBytes(assemblyPath, read.Payload) : throw new FileLoadException(read.Message, assemblyPath);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is null ? 0 : LoadUnmanagedDllFromPath(libraryPath);
        }

        /// <summary>
        /// Loads an assembly from its bytes, with its portable pdb when one sits next to it (symbols are optional).
        /// </summary>
        private Assembly LoadBytes(string file, byte[] assembly)
        {
            string pdbPath = System.IO.Path.ChangeExtension(file, ".pdb");
            Result<byte[]>? pdb = files.FileExists(pdbPath) ? files.ReadBinary(pdbPath) : null;
            MemoryStream? symbols = pdb is { Ok: true } ? new MemoryStream(pdb.Value.Payload) : null;
            return LoadFromStream(new MemoryStream(assembly), symbols);
        }
    }

    /// <summary>
    /// A folder gems come from: the engine's, flat and filtered to <paramref name="Names"/> (null takes all) less
    /// <paramref name="Excluded"/>, or the project's, searched recursively with build and cache folders left out.
    /// </summary>
    private sealed record GemSource(string Directory, bool Recursive, HashSet<string>? Names, HashSet<string> Excluded)
    {
        /// <summary>
        /// Does <paramref name="path"/> lie in this source's territory?
        /// </summary>
        public bool Accepts(IFileSystem files, string path)
        {
            if (!files.IsUnder(Directory, path))
                return false;

            string[] folders = files.Segments(files.Relative(Directory, path))[..^1];
            return Recursive
                ? folders.All(folder => folder is not ("obj" or "Cache") && !folder.StartsWith('.'))
                : folders.Length == 0;
        }

        public bool Wants(string name)
        {
            return !Excluded.Contains(name) && (Names is null || Names.Contains(name));
        }
    }
}
