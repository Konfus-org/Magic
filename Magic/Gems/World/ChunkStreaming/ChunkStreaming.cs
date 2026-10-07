using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace ChunkStreamingGem;

/// <summary>
/// The gem: the streaming system on the Update phase. Far chunks spawn as their stand-ins, which whatever
/// <see cref="ILODGenerator{T}"/> of <see cref="Chunk"/> is loaded makes (the ChunkLodGenerator gem). Scripts a chunk lists go to
/// the <see cref="IScripting"/> gem; without it, entities spawn without them. It keeps the
/// component types of every loaded assembly (a registrar of <see cref="IComponent"/>): what a chunk names by type name.
/// </summary>
internal sealed class ChunkStreaming : IGem, ITypeRegistrar<IComponent>
{
    private readonly Types<IComponent> _components = new();
    private readonly StreamingSystem _system;
    private readonly Subscription _scheduled;

    public ChunkStreaming(IEcs ecs, Assets assets, StreamingSettings settings, LodSettings lod, World world, Threads threads, ITelemetry telemetry, IScripting? scripting, IRendering? rendering)
    {
        _system = new StreamingSystem(ecs, assets, _components, settings, lod, scripting, world, threads, telemetry, rendering);
        _scheduled = ecs.Add(_system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }

    object? ITypeRegistrar.Register(Type type)
    {
        return ((ITypeRegistrar)_components).Register(type);
    }

    void ITypeRegistrar.Unregister(Type type)
    {
        ((ITypeRegistrar)_components).Unregister(type);
    }
}
