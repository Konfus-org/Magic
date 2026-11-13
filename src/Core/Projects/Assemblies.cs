using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Magic.Debugging;
using Magic.Files;
using Magic.Utils;

namespace Magic.Projects;

/// <summary>
/// The code of a run: it loads the code of the gems and the project, watches it for builds, and finds the types that
/// a file names, such as a component's struct.
/// </summary>
/// <remarks>
/// Each run makes one, and disposing it unloads the code of the run. <see cref="Get{T}"/> finds a type of Core's code,
/// or of code that a gem or the project registered, on any thread.
/// </remarks>
public sealed class Assemblies : IDisposable
{
    /// <summary>
    /// How long loaded code must have been left alone after a build wrote it.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromSeconds(1);

    private static ImmutableArray<Assembly> _assemblies = [typeof(Assemblies).Assembly];

    /// <summary>
    /// Replaced whole on the game thread, and read whole on any.
    /// </summary>
    private static FrozenDictionary<string, ImmutableArray<Type>> _byName = Index(_assemblies);

    private readonly IFileSystem _files;

    /// <summary>
    /// The context of each assembly that the run can load, by assembly name.
    /// </summary>
    private readonly Dictionary<string, CodeLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    private readonly Subscriptions _watches = [];

    /// <summary>
    /// When a watch last saw a build write code of the run, 0 when none has: written whole from the watches' threads.
    /// </summary>
    private long _buildTimestamp;

    internal Assemblies(IFileSystem files) => _files = files;

    /// <summary>
    /// Whether a build wrote code of the run and then left it alone a while, so the run must restart on the new code.
    /// </summary>
    /// <remarks>
    /// A build writes a gem and its dependencies in turn, so one change waits for the build to finish.
    /// </remarks>
    internal bool IsBuilt
    {
        get
        {
            long built = Volatile.Read(ref _buildTimestamp);

            return built != 0 && Stopwatch.GetElapsedTime(built) >= _settleTime;
        }
    }

    /// <summary>
    /// Gets the one <typeparamref name="T"/> named <paramref name="name"/>, or why not when none or more than one is
    /// loaded.
    /// </summary>
    public static Result<Type> Get<T>(string name)
    {
        ImmutableArray<Type> named = Volatile.Read(ref _byName).GetValueOrDefault(name, []);
        ImmutableArray<Type> matching = named.RemoveAll(type => !typeof(T).IsAssignableFrom(type));

        return matching.Length switch
        {
            1 => Result.Success(matching[0]),
            0 => Result.Failure($"no code loaded declares a {typeof(T).Name} named {name}."),
            _ => Result.Failure(
                $"more than one {typeof(T).Name} is named {name}: "
                    + $"{string.Join(", ", matching.Select(type => type.FullName))}."
            ),
        };
    }

    /// <summary>
    /// Unloads the code of the run. Dispose it after the gems, which use it.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        foreach (CodeLoadContext context in _contexts.Values)
        {
            context.Unload();
        }

        _contexts.Clear();
    }

    /// <summary>
    /// Adds a gem's or a project's <paramref name="assembly"/> to the types that <see cref="Get{T}"/> finds, until the
    /// subscription is disposed. Game thread.
    /// </summary>
    /// <remarks>
    /// Dispose the subscription, or the code cannot unload.
    /// </remarks>
    internal static Subscription Register(Assembly assembly)
    {
        _assemblies = _assemblies.Add(assembly);
        Volatile.Write(ref _byName, Index(_assemblies));

        return new Subscription(() =>
        {
            _assemblies = _assemblies.Remove(assembly);
            Volatile.Write(ref _byName, Index(_assemblies));
        });
    }

    /// <summary>
    /// Makes the assembly at <paramref name="path"/> code that the run can load, and watches its folder for a build.
    /// </summary>
    /// <remarks>
    /// Nothing loads until <see cref="Load"/> or a reference from other code of the run. Add all the code before any
    /// loads, so each finds the code that it references in any order.
    /// </remarks>
    internal void Add(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        _contexts[name] = new CodeLoadContext(path, _files, _contexts);
        Result<IDisposable> watched = _files.Watch(
            _files.GetParent(path),
            "*.dll",
            _ => Volatile.Write(ref _buildTimestamp, Stopwatch.GetTimestamp())
        );
        if (watched.Ok)
            _watches.Add(new Subscription(watched.Value.Dispose));
        else
            Log.Warn($"{name} is not watched, so a build of it won't restart the run: {watched.Error}");
    }

    /// <summary>
    /// Whether <paramref name="name"/> is the assembly name of code that the run can load.
    /// </summary>
    internal bool Has(string name) => _contexts.ContainsKey(name);

    /// <summary>
    /// Loads the added assembly named <paramref name="name"/>, or gives why it could not be read.
    /// </summary>
    internal Result<Assembly> Load(string name) => _contexts[name].LoadCode();

    /// <summary>
    /// Indexes the top-level types of <paramref name="assemblies"/> by name.
    /// </summary>
    private static FrozenDictionary<string, ImmutableArray<Type>> Index(ImmutableArray<Assembly> assemblies)
    {
        Dictionary<string, ImmutableArray<Type>> byName = new(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetLoadableTypes())
            {
                if (!type.IsNested)
                    byName[type.Name] = byName.GetValueOrDefault(type.Name, []).Add(type);
            }
        }

        return byName.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

public static class AssemblyExtensions
{
    extension(Assembly assembly)
    {
        /// <summary>
        /// Gets what the assembly's build recorded as <paramref name="key"/>
        /// (<c>&lt;AssemblyMetadata Include="key"&gt;</c>); null when it is missing or empty.
        /// </summary>
        public string? GetMetadata(string key)
        {
            foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (
                    string.Equals(metadata.Key, key, StringComparison.Ordinal)
                    && metadata.Value is { Length: > 0 } value
                )
                    return value;
            }

            return null;
        }

        /// <summary>
        /// Gets each type of the assembly that loads.
        /// </summary>
        internal Type[] GetLoadableTypes()
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
    }
}
