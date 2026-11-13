using Magic.Assets;
using Magic.Ecs;
using Magic.Projects;

namespace Magic.WeatherFX;

/// <summary>
/// The gem: its <see cref="Renderer"/> fills the air with the haze and the clouds of the world's weather.
/// </summary>
public sealed class EntryPoint(AssetManager assets, IEcs ecs) : IGem
{
    public Renderer Renderer { get; } = new(assets, ecs);

    public void LateUpdate(in Frame frame) => Renderer.PublishWeather();
}
