using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Lights;

/// <summary>
/// Moves its entity round the world's up axis in a circle, bobbing up and down as it goes: what carries a point
/// light across the scene.
/// </summary>
internal sealed class Circle(Handle entity, IEcs ecs) : IBehavior
{
    private float _travelled; // degrees gone round since the start

    /// <summary>
    /// Metres from the axis. The chunk sets this and the rest beside the script's id.
    /// </summary>
    public float Radius { get; set; }

    /// <summary>
    /// Metres above the ground the bobbing is centred on.
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// Metres it rises and falls either side of <see cref="Height"/>, twice a turn.
    /// </summary>
    public float Bob { get; set; }

    /// <summary>
    /// Negative goes round the other way.
    /// </summary>
    public float DegreesPerSecond { get; set; }

    /// <summary>
    /// Degrees round the circle it starts at.
    /// </summary>
    public float Phase { get; set; }

    public void Update(in Frame frame)
    {
        _travelled += DegreesPerSecond * frame.Delta;
        float angle = float.DegreesToRadians(Phase + _travelled);

        ref Transform transform = ref ecs.Get<Transform>(entity);
        transform.Position = new Vector3(MathF.Cos(angle) * Radius, Height + (MathF.Sin(angle * 2f) * Bob), MathF.Sin(angle) * Radius);
    }
}
