using Magic.Assets;
using Magic.Ecs;
using Magic.Projects;

namespace Magic.WeatherFX;

/// <summary>
/// The gem: its <see cref="Renderer"/> fills the air with the world's haze, as finely as its <see cref="Settings"/>
/// say.
/// </summary>
public sealed class EntryPoint(WeatherFXSettings settings, AssetManager assets, IEcs ecs) : IGem
{
    /// <summary>
    /// How finely the air is filled, which the renderer reads each frame. Game thread.
    /// </summary>
    public WeatherFXSettings Settings { get; } = settings;

    public Renderer Renderer { get; } = new(settings, assets, ecs);

    public void LateUpdate(in Frame frame) => Renderer.PublishWeather();
}
