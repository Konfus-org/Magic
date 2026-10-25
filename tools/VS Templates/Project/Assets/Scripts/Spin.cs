using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace ProjectName.Assets.Scripts;

/// <summary>
/// Turns its entity about its up axis, <see cref="DegreesPerSecond"/> a second. A script is the class named like its
/// file, an asset by the id in the file's <c>.meta</c>: a prefab attaches it by that id and sets its properties, as
/// the Main domain's <c>globals.prefab</c> does with <c>"degreesPerSecond": 45</c>. It is made with its entity, and
/// its constructor takes the entity, then what else it needs.
/// </summary>
internal sealed class Spin(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// How fast it turns.
    /// </summary>
    public float DegreesPerSecond { get; set; } = 30;

    public void Update(in Frame frame)
    {
        ref Transform transform = ref world.Get<Transform>(owner);
        float radians = float.DegreesToRadians(DegreesPerSecond * frame.WorldDeltaSec);
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians);
        transform.Rotation = Quaternion.Normalize(transform.Rotation * turn);
    }
}
