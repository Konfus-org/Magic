using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Services;

/// <summary>
/// The host's own services, made once for the run. Nothing outside reads a property for one: they are in the
/// <see cref="Container"/> this answers, asked for by type like any gem's.
/// </summary>
internal static class CoreServices
{
    /// <summary>
    /// The container gem constructors ask by type, holding the services for <paramref name="project"/> and the
    /// <paramref name="settings"/> made from the command line; gems add
    /// theirs as they load. Whoever calls disposes the <see cref="Assets"/> and then the <see cref="Threads"/> in
    /// it, and says which thread is which: none is anybody's yet.
    /// </summary>
    public static Container Create(Project project, IFileSystem files, Settings settings)
    {
        Container container = new();
        container.Add<IServices>(container); // the read side, for whoever must look services up by type
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
        container.Add(new Scheduler());
        container.Add(new World(events, assets, threads) { Loading = project.Loading });
        container.Add(settings);
        Types<IComponent> components = new();
        container.Add(components);
        Types<IScript> scripts = new();
        container.Add(scripts);

        // The services that register what assemblies declare, which Gems finds by this contract.
        container.Add<IRegisterFromGem>(settings);
        container.Add<IRegisterFromGem>(assets);
        container.Add<IRegisterFromGem>(components);
        container.Add<IRegisterFromGem>(scripts);

        // The engine's own types go to the services that register them (its settings, asset and component types), as
        // each gem's will; the engine never unloads, so nothing keeps the registrations.
        Gems.Register(container, Gems.TypesOf(typeof(Project).Assembly));

        return container;
    }
}
