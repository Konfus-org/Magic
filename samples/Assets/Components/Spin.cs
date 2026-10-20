using Magic.Interfaces;
using Magic.Samples.Assets.Scripts;

namespace Magic.Samples.Assets.Components;

/// <summary>
/// Turns its entity about the world's up axis, as <see cref="SpinSystem"/> runs: a component, which a prefab names as
/// <c>"Spin": { "degreesPerSecond": 45 }</c>.
/// </summary>
internal record struct Spin : IComponent
{
    public float DegreesPerSecond;
}
