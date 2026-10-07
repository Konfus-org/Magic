using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Samples.Shared;

/// <summary>
/// Turns its entity around the world's up axis, keeping its tilt: a spot light pointing down at an angle sweeps its
/// cone round the floor.
/// </summary>
internal sealed class Turn(Handle entity, IEcs ecs) : IBehavior
{
    /// <summary>
    /// The chunk sets it beside the script's id.
    /// </summary>
    public float DegreesPerSecond { get; set; }

    public void Update(in Frame frame)
    {
        ref Transform transform = ref ecs.Get<Transform>(entity);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(transform.Rotation, turn));
    }
}
