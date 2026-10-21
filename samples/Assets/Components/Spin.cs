using System.Numerics;
using Magic.Interfaces;
using Magic.Samples.Assets.Scripts;

namespace Magic.Samples.Assets.Components;

/// <summary>
/// Turns its entity about an axis of the world, as <see cref="SpinSystem"/> runs: a component, which a prefab names as
/// <c>"Spin": { "degreesPerSecond": 45 }</c>, about the world's up, or with an <c>"axis"</c>, about that: a sun and a
/// moon turned about a tilted one cross the sky.
/// </summary>
internal record struct Spin : IComponent
{
    public float DegreesPerSecond;

    /// <summary>
    /// The axis it turns about, in the world; zero is the world's up.
    /// </summary>
    public Vector3 Axis;
}
