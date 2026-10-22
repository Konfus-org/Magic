using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Makes its entity's point light flicker as a fire's does: its intensity wanders about <see cref="Intensity"/> by a
/// few overlapping waves, never in step, and the light shifts a little as flames do: a behaviour a prefab attaches as
/// <c>{ "id": 3449, "intensity": 90 }</c>.
/// </summary>
internal sealed class Flicker(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Where it was put, which the light moves about.
    /// </summary>
    private Vector3 _home;

    /// <summary>
    /// The light's intensity the flicker wanders about, in candela.
    /// </summary>
    public float Intensity { get; set; } = 64;

    /// <summary>
    /// How far it wanders, as a share of <see cref="Intensity"/>.
    /// </summary>
    public float Depth { get; set; } = 0.35f;

    public void Start() => _home = world.Get<Transform>(owner).Position;

    public void Update(in Frame frame)
    {
        // Three waves at rates that never fall in step, -1 to 1 together, swing the intensity across the depth; two
        // more sway the light a few centimetres.
        float t = (float)frame.Seconds;
        float wave = (MathF.Sin(t * 7.3f) * 0.5f) + (MathF.Sin((t * 13.1f) + 1.7f) * 0.3f)
            + (MathF.Sin((t * 23.7f) + 4.1f) * 0.2f);
        world.Get<PointLight>(owner).Intensity = Intensity * (1 + (Depth * wave * 0.5f));
        world.Get<Transform>(owner).Position = _home
            + new Vector3(MathF.Sin(t * 5.1f) * 0.03f, MathF.Sin(t * 9.7f) * 0.04f, 0);
    }
}
