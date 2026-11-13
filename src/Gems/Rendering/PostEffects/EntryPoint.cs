using Magic.Assets;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;

namespace Magic.PostEffects;

/// <summary>
/// Runs the post-processing chain over each view's lit picture.
/// </summary>
public sealed class EntryPoint(
    PostEffectsSettings settings,
    IShaderCompiler shaders,
    AssetManager assets,
    IEcs ecs,
    Threads threads
) : IGem
{
    /// <summary>
    /// The brightness and the post effects left out, which the renderer reads each frame. Game thread.
    /// </summary>
    public PostEffectsSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, shaders, assets, ecs, threads);

    public void LateUpdate(in Frame frame) => Renderer.PublishPostProcessing(frame);
}
