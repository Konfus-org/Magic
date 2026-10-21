using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Carries its entity round a circle of <see cref="Radius"/> about where it spawned, level, facing the way it goes: a
/// sound on it is heard to the left, behind, to the right and in front in turn by a listener in the middle.
/// <c>{ "id": 3434, "radius": 6, "degreesPerSecond": 40 }</c>.
/// </summary>
internal sealed class Orbit(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// The circle's middle, where its entity spawned.
    /// </summary>
    private Vector3 _middle;

    public float Radius { get; set; } = 5;

    public float DegreesPerSecond { get; set; } = 30;

    public void Start()
    {
        _middle = world.Get<Transform>(owner).Position;
    }

    public void Update(in Frame frame)
    {
        float angle = (float)(frame.Seconds * DegreesPerSecond * (Math.PI / 180));
        ref Transform transform = ref world.Get<Transform>(owner);
        transform.Position = _middle + new Vector3(MathF.Sin(angle) * Radius, 0, MathF.Cos(angle) * Radius);
        transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle + (MathF.PI / 2));
    }
}
