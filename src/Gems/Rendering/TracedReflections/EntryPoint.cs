using Magic.Assets;
using Magic.GlobalIllumination;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;

namespace Magic.TracedReflections;

/// <summary>
/// The gem: its <see cref="Renderer"/> traces what smooth surfaces reflect, as its <see cref="Settings"/> say.
/// </summary>
/// <remarks>
/// It traces at the indirect light's scale, from the indirect light's settings.
/// </remarks>
public sealed class EntryPoint(
    TracedReflectionsSettings settings,
    GlobalIlluminationSettings indirect,
    AssetManager assets,
    Threads threads,
    IGraphicsApi gpu
) : IGem
{
    /// <summary>
    /// How the reflections are traced, which the renderer reads each frame. Game thread.
    /// </summary>
    public TracedReflectionsSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, indirect, assets, threads, gpu);
}
