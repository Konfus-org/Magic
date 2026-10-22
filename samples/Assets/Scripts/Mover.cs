using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Carries its entity from where it spawned to <see cref="Offset"/> from there and back every <see cref="Seconds"/>,
/// easing at each end: on a kinematic body, a platform, which pushes what lies on it and what it runs into, as the
/// physics gem moves a kinematic body where its transform says.
/// <c>{ "id": 3443, "offset": { "x": 6, "y": 0, "z": 0 } }</c>.
/// </summary>
internal sealed class Mover(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Where it spawned, one end of its run.
    /// </summary>
    private Vector3 _start;

    public Vector3 Offset { get; set; }

    public float Seconds { get; set; } = 4;

    public void Start() => _start = world.Get<Transform>(owner).Position;

    public void FixedUpdate(in Frame frame)
    {
        float along = (1 - MathF.Cos((float)(frame.Seconds * Math.Tau / Seconds))) / 2;
        world.Get<Transform>(owner).Position = _start + (Offset * along);
    }
}
