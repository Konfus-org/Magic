using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Systems.Streaming;

namespace Magic.Services;

/// <summary>
/// The host's own services, made once for the run. Nothing outside reads a property for one: they are in the
/// <see cref="Container"/> this answers, asked for by type like any gem's.
/// </summary>
internal static class CoreServices
{
    /// <summary>
    /// The container gem constructors ask by type, holding the services for <paramref name="project"/>; gems add
    /// theirs as they load. Whoever calls disposes the <see cref="Assets"/> and then the <see cref="Threads"/> in
    /// it, and says which thread is which: none is anybody's yet.
    /// </summary>
    public static Container Create(Project project, IFileSystem files)
    {
        Container container = new();
        Events events = new();

        Threads threads = new();
#pragma warning disable CA2000 // the caller owns it through the container, and disposes it
        Assets assets = new(project, files, events, container, threads); // indexes Resources and the project's Assets
#pragma warning restore CA2000
        if (!project.Icon.IsValid)
            project = project with { Icon = assets.Find<Texture>("Icons/Mage.svg") }; // the engine's own icon unless the project names one
        if (!project.Loading.IsValid)
            project = project with { Loading = assets.Find<Domain>("Domains/Loading/Loading.domain") }; // and its own loading domain

        container.Add(project);
        container.Add(files);
        container.Add(events);
        container.Add(threads);
        container.Add(new MainThread(threads));
        container.Add(assets);
        container.Add<ILODGenerator<Chunk>>(new ChunkLods(assets, files)); // far chunks' stand-ins
        container.Add(new Scheduler());
        container.Add(new World(events, assets, threads) { Loading = project.Loading });

        return container;
    }
}
