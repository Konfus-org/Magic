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
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * frame.Delta));
        transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(transform.Rotation, turn));
    }
}
