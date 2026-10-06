using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace StreamingGem;

/// <summary>
/// The gem: the streaming system on the Update phase, and the stand-in generator for far chunks
/// (<see cref="ILODGenerator{T}"/> of <see cref="Chunk"/>) the asset system asks for. Scripts a chunk lists go to
/// whichever <see cref="IScripting"/> gems are loaded; without any, entities spawn without them.
/// </summary>
internal sealed class WorldStreaming : IGem, ILODGenerator<Chunk>
{
    private readonly ChunkLods _lods;
    private readonly StreamingSystem _system;
    private readonly IDisposable _scheduled;

    public WorldStreaming(IEcs ecs, Assets assets, Types<IComponent> components, StreamingSettings settings, LodSettings lod, World world, Threads threads, IFileSystem files, Scheduler scheduler, IScripting[] scripting, IRendering? rendering)
    {
        _lods = new ChunkLods(assets, files);
        _system = new StreamingSystem(ecs, assets, components, settings, lod, scripting, world, threads, rendering);
        _scheduled = scheduler.Add(ecs, _system);
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        _system.Dispose();
    }

    public int Version => _lods.Version;

    public Task<Result<Lods>> GenerateAsync(Chunk asset, string folder, IProgress<float>? progress, CancellationToken cancel)
    {
        return _lods.GenerateAsync(asset, folder, progress, cancel);
    }
}
