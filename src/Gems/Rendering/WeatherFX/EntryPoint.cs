using Magic.Assets;
using Magic.Ecs;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;

namespace Magic.WeatherFX;

/// <summary>
/// The gem: its <see cref="Renderer"/> fills the air with the haze and the clouds of the world's weather.
/// </summary>
public sealed class EntryPoint(AssetManager assets, IEcs ecs, Threads threads, IGraphicsApi gpu) : IGem
{
    public Renderer Renderer { get; } = new(assets, ecs, threads, gpu);

    public void LateUpdate(in Frame frame) => Renderer.PublishWeather();
}
