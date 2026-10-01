using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace RenderTexture;

/// <summary>Turns its entity around the world's up axis, so the security camera's picture on the monitor visibly moves.</summary>
internal sealed class Spin(Handle entity, IEcs ecs) : IBehavior
{
    /// <summary>The chunk sets it beside the script's id.</summary>
    public float DegreesPerSecond { get; set; }

    public void Update(in Frame frame)
    {
        ref Transform transform = ref ecs.Get<Transform>(entity);

        // A chunk that gives no rotation leaves the all-zero quaternion, which draws as identity but would stay zero here.
        Quaternion rotation = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, turn));
    }
}
