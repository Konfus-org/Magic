using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace LightStress;

/// <summary>
/// Moves its entity in a circle round a point of the ground: what carries a chunk's two moving lights about.
/// </summary>
internal sealed class Circle(Handle entity, IEcs ecs) : IBehavior
{
    private float _travelled; // degrees gone round since the start

    /// <summary>
    /// The point it goes round, in world metres. The chunk sets these and the rest beside the script's id.
    /// </summary>
    public float CenterX { get; set; }

    public float CenterZ { get; set; }

    public float Radius { get; set; }

    /// <summary>
    /// Metres above the ground.
    /// </summary>
    public float Height { get; set; }

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
        transform.Position = new Vector3(CenterX + (MathF.Cos(angle) * Radius), Height, CenterZ + (MathF.Sin(angle) * Radius));
    }
}
