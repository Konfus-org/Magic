using Magic.Assets;
using Magic.Ecs;
using Magic.Projects;

namespace Magic.Volumetrics;

/// <summary>
/// The gem: its <see cref="Renderer"/> fills the air with the world's haze, as finely as its <see cref="Settings"/>
/// say. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class EntryPoint(VolumetricsSettings settings, AssetManager assets, IEcs ecs) : IGem
{
    /// <summary>
    /// How finely the air is filled, which the renderer reads each frame. Game thread.
    /// </summary>
    public VolumetricsSettings Settings { get; } = settings;

    /// <summary>
    /// The air's passes and the world's weather they read.
    /// </summary>
    public Renderer Renderer { get; } = new(settings, assets, ecs);

    public void LateUpdate(in Frame frame) => Renderer.PublishWeather();
}
