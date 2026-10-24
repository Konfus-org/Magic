using System.Reflection;
using Magic.Contexts.Assets;
using Magic.Contexts.Subscriptions;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Internal;

/// <summary>
/// Loads the code that is no gem, scripts and the components they use, and loads it again when it is built while the
/// engine runs, until disposed: the project's own, the <c>.cs</c> files under its <c>Assets/</c>, which the csproj
/// beside its <c>.magic</c> file compiles into Projects/Name/Name.dll beside Magic, Name being the project's; and
/// Magic's, the <c>.cs</c> files among the bundled resources, compiled into Resources/Resources.dll beside it. Each
/// loads into a context of its own, as a gem does (<see cref="GemAssembly"/>), though it provides nothing and takes no
/// settings, and its assembly is taken in as it loads (<see cref="Assemblies"/>), so files can name its components and
/// scripts. Loaded again, it goes into a new context beside the last, which stays for whatever holds its types, and
/// the scripts are made again, then the prefabs, so each names the scripts' classes as they are now.
/// </summary>
internal sealed class CodeReloader : IDisposable
{
    /// <summary>
    /// How long a code folder must have been left alone before what was built there is loaded: a build writes the
    /// assembly, its symbols and what it depends on one after another.
    /// </summary>
    private static readonly TimeSpan _settleTime = TimeSpan.FromMilliseconds(300);

    private readonly IFileSystem _files;
    private readonly Assemblies _assemblies;
    private readonly Assets _assets;

    /// <summary>
    /// The code, as far as it was there to load, to load again when it is built.
    /// </summary>
    private readonly List<CodeFile> _code = [];

    /// <summary>
    /// The watches on the folders <see cref="_code"/> is built in.
    /// </summary>
    private readonly SubscriptionList _watches = new();

    /// <summary>
    /// Loads Magic's scripts and <paramref name="project"/>'s code, as far as each is built, into
    /// <paramref name="assemblies"/>, and watches where each is built to load it again.
    /// </summary>
    internal CodeReloader(IFileSystem files, Project project, Assemblies assemblies, Assets assets)
    {
        _files = files;
        _assemblies = assemblies;
        _assets = assets;

        Load(files.Combine(AppContext.BaseDirectory, "Resources", "Resources.dll"), "Magic's scripts");
        Load(
            files.Combine(AppContext.BaseDirectory, "Projects", project.Name, project.Name + ".dll"),
            $"{project.Name}'s code");

        foreach (CodeFile code in _code)
        {
            _watches.Add(FileSystem.Watch(files.GetParent(code.Path), _ => ReloadAsync(code).FireAndForget()));
        }
    }

    /// <summary>
    /// Stops watching the code's folders, and ends what is registered for the code.
    /// </summary>
    public void Dispose()
    {
        _watches.Dispose();
        foreach (CodeFile code in _code)
        {
            code.Dispose();
        }
    }

    /// <summary>
    /// Loads the code at <paramref name="path"/> and takes in its assembly, and keeps it to load again when it is
    /// built; nothing when it is not built, as a project needs no code.
    /// </summary>
    private void Load(string path, string name)
    {
        if (!_files.FileExists(path) || LoadAssembly(path, name, isReload: false) is not { } assembly) return;

        CodeFile code = new(path, name);
        code.SwapRegistration(_assemblies.Register(path, assembly));
        _code.Add(code);
    }

    /// <summary>
    /// Something in <paramref name="code"/>'s folder changed: once it has settled, the last change to come loads the
    /// code again and has the scripts made again, then the prefabs.
    /// </summary>
    private async Task ReloadAsync(CodeFile code)
    {
        int change = code.NoteChange();
        await Task.Delay(_settleTime).ConfigureAwait(false);

        if (!code.IsLatest(change) || LoadAssembly(code.Path, code.Name, isReload: true) is not { } assembly) return;

        code.SwapRegistration(_assemblies.Register(code.Path, assembly));
        await _assets.RecreateAsync<Script>().ConfigureAwait(false);
        await _assets.RecreateAsync<Prefab>().ConfigureAwait(false);
    }

    /// <summary>
    /// The code at <paramref name="path"/>, called <paramref name="name"/> in the log, loaded into a context of its own;
    /// null when it cannot be, which is logged, a reload whose file is gone by then among it.
    /// </summary>
    private Assembly? LoadAssembly(string path, string name, bool isReload)
    {
        Result<Assembly> loaded = GemAssembly.LoadAssembly(_files, path, name, isCode: true, isReload);
        if (!loaded.Ok)
        {
            Debugging.Error(loaded.Error);

            return null;
        }

        Debugging.Info(isReload ? $"Loaded {name} again" : $"Loaded {name}");

        return loaded.Value;
    }

    /// <summary>
    /// Code that is no gem: where it is built, what to call it in the log, its assembly's registration as last loaded,
    /// and the changes its folder has had, of which the latest loads it once it has settled.
    /// </summary>
    private sealed class CodeFile(string path, string name) : IDisposable
    {
        private int _changes;
        private Subscription _registration = Subscription.None;

        internal string Path { get; } = path;

        internal string Name { get; } = name;

        public void Dispose() => _registration.Dispose();

        /// <summary>
        /// Keeps <paramref name="registration"/>, of the code loaded again, and ends the one it takes the place of.
        /// </summary>
        internal void SwapRegistration(Subscription registration)
            => Interlocked.Exchange(ref _registration, registration).Dispose();

        /// <summary>
        /// Notes one more change to its folder: which one, to ask <see cref="IsLatest"/> of after the settle.
        /// </summary>
        internal int NoteChange() => Interlocked.Increment(ref _changes);

        internal bool IsLatest(int change) => change == Volatile.Read(ref _changes);
    }
}
