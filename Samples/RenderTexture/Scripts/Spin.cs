using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using System.Numerics;

namespace RenderTexture;

/// <summary>Turns its entity around the world's up axis. Any struct implementing <see cref="IComponent"/> in a loaded gem can be written in a chunk.</summary>
public struct Spin : IComponent
{
    public float DegreesPerSecond { get; set; }
}

/// <summary>Turns every entity with a <see cref="Spin"/>, so the security camera's picture on the monitor visibly moves.</summary>
internal sealed class Spinner(IEcs ecs) : IGem
{
    private readonly IEcsQuery<Transform, Spin> _spinning = ecs.Query<Transform, Spin>().Build();

    public void Dispose()
    {
        _spinning.Dispose();
    }

    public void Update(in Frame frame)
    {
        float delta = frame.Delta;
        _spinning.Each((Handle _, ref Transform transform, ref Spin spin) =>
        {
            Quaternion rotation = transform.Rotation.LengthSquared() < 1e-6f ? Quaternion.Identity : transform.Rotation;
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(spin.DegreesPerSecond * delta));
            transform.Rotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, turn));
        });
    }
}
