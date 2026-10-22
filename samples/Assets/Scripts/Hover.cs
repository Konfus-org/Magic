using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Floats its entity up from where it spawned and back down, <see cref="Height"/> metres every
/// <see cref="Seconds"/>: a script, a behaviour a prefab attaches as <c>{ "id": 3312, "height": 1 }</c>, which sets
/// its height.
/// </summary>
internal sealed class Hover(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// Its height as it spawned, which it rises from.
    /// </summary>
    private float _restY;

    /// <summary>
    /// How far it rises, in metres: none unless a chunk says.
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// How long it takes to rise and come down again.
    /// </summary>
    public float Seconds { get; set; } = 2;

    public void Start() => _restY = world.Get<Transform>(owner).Position.Y;

    public void Update(in Frame frame)
    {
        // 0 to 1 and back once a period, easing at each end.
        float rise = (1 - MathF.Cos((float)(frame.Seconds * Math.Tau / Seconds))) / 2;
        world.Get<Transform>(owner).Position.Y = _restY + (Height * rise);
    }
}
