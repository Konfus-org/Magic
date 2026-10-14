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
internal sealed class Hover(World world) : IBehavior
{
    private readonly World _world = world;
    private Entity _entity;

    /// <summary>
    /// Its height as it spawned, which it rises from.
    /// </summary>
    private double _restY;

    /// <summary>
    /// How far it rises, in metres: none unless a chunk says.
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// How long it takes to rise and come down again.
    /// </summary>
    public float Seconds { get; set; } = 2;

    public void Start(Entity entity)
    {
        _entity = entity;
        _restY = _world.Get<Transform>(entity).Position.Y;
    }

    public void Update(in Frame frame)
    {
        float rise = (1 - MathF.Cos((float)(frame.Seconds * Math.Tau / Seconds))) / 2;
        _world.Get<Transform>(_entity).Position.Y = _restY + (Height * rise);
    }
}
