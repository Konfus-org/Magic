using Magic.Interfaces;
using Magic.Samples.Assets.Scripts;

namespace Magic.Samples.Assets.Components;

/// <summary>
/// Bobs its entity up and down about <see cref="Base"/>, <see cref="Height"/> metres each way, as
/// <see cref="WaveSystem"/> runs, <see cref="Phase"/> radians along the wave: entities set one after another along a
/// row make a wave run down it. <c>"Wave": { "base": 0.5, "height": 0.4, "phase": 1.2 }</c>.
/// </summary>
internal record struct Wave : IComponent
{
    public float Base;

    public float Height;

    public float Phase;
}
