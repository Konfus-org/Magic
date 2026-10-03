using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace Samples.MyMagicSample;

/// <summary>
/// Turns its entity around the world's up axis. The chunk attaches it to the cube by this file's asset id and sets
/// <see cref="DegreesPerSecond"/> beside it.
/// </summary>
internal sealed class MyMagicSample(Handle entity, IEcs ecs) : IBehavior
{
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
