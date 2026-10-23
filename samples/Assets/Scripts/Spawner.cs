using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Samples.Assets.Components;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// A fountain: every <see cref="EverySec"/> it makes an entity of its own, a small body thrown up from its entity a
/// little to one side, which falls, bounces and goes <see cref="LifetimeSec"/> later, as its
/// <see cref="Expires"/> says and <see cref="ExpireSystem"/> sees to. A behaviour making entities at run time, with
/// the components it builds them of:
/// <c>{ "id": 3440, "everySec": 0.25, "model": { "id": 514 }, "material": { "id": 3322 } }</c>. They are its own, so
/// those still there go when its entity does, as when its domain closes, which ends the system that would have ended
/// them.
/// </summary>
internal sealed class Spawner(Entity owner, World world) : IBehavior, IDisposable
{
    /// <summary>
    /// Radians between one throw's direction and the next's: the golden angle, so they spread evenly all round.
    /// </summary>
    private const float Turn = 2.39996f;

    /// <summary>
    /// The bodies it made that may still be there, oldest first, the order they go in.
    /// </summary>
    private readonly Queue<Entity> _bodies = new();

    /// <summary>
    /// When it makes the next, on the world's clock.
    /// </summary>
    private double _nextSec;

    /// <summary>
    /// How many it has made, which turns each throw on from the last.
    /// </summary>
    private int _made;

    public float EverySec { get; set; } = 0.25f;

    public float LifetimeSec { get; set; } = 6;

    /// <summary>
    /// Metres a second each is thrown up at.
    /// </summary>
    public float Speed { get; set; } = 9;

    /// <summary>
    /// How big each is, for a model 2 m across.
    /// </summary>
    public float Size { get; set; } = 0.2f;

    public Handle<Model> Model { get; set; }

    public Handle<Material> Material { get; set; }

    public void Dispose()
    {
        foreach (Entity body in _bodies)
        {
            if (world.IsAlive(body)) world.Destroy(body);
        }

        _bodies.Clear();
    }

    public void Update(in Frame frame)
    {
        if (frame.WorldSec < _nextSec) return;

        // Each throw goes the golden angle round from the last, up at the speed and out at a quarter of it, from half a
        // metre up and turned a way of its own. The bodies the system has ended since are let go from the front.
        _nextSec = frame.WorldSec + EverySec;
        Transform from = world.Get<Transform>(owner);
        float angle = _made++ * Turn;
        Vector3 aside = new(MathF.Cos(angle), 0, MathF.Sin(angle));
        Transform at = new()
        {
            Position = from.Position + (Vector3.UnitY * 0.5f),
            Rotation = Quaternion.CreateFromAxisAngle(aside, angle),
            Scale = new Vector3(Size),
        };
        Renderer renderer = new() { Model = Model, Materials = new MaterialSlots { Material } };
        Physics body = new()
        {
            Motion = Motion.Dynamic,
            Bounce = 0.5f,
            Velocity = (Vector3.UnitY * Speed) + (aside * (Speed * 0.25f)),
        };
        while (_bodies.TryPeek(out Entity oldest) && !world.IsAlive(oldest))
        {
            _bodies.Dequeue();
        }

        _bodies.Enqueue(world.Create([at, renderer, body, new Expires { At = frame.WorldSec + LifetimeSec }]));
    }
}
