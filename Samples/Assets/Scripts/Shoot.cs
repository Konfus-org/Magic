using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Throws a ball from its entity, the player's camera, along where it looks, on a left click or the gamepad's south
/// button: a small sphere with a dynamic <see cref="Physics"/> body sent off at <see cref="Speed"/>, which the physics
/// gem flies from there. A prefab attaches it as
/// <c>{ "id": 3317, "speed": 40, "most": 50, "model": { "id": 517 }, "material": { "id": 3322 } }</c>. Past
/// <see cref="Most"/> balls the oldest goes, and all go when the entity does.
/// </summary>
internal sealed class Shoot(Entity owner, World world, IInput input) : IBehavior, IDisposable
{
    private readonly Queue<Entity> _balls = new();

    public void Dispose()
    {
        foreach (Entity ball in _balls)
        {
            if (world.IsAlive(ball))
            {
                world.Destroy(ball);
            }
        }

        _balls.Clear();
    }

    /// <summary>
    /// Metres per second a ball leaves at.
    /// </summary>
    public float Speed { get; set; } = 40;

    /// <summary>
    /// How many balls there are at most; the oldest goes to make room.
    /// </summary>
    public int Most { get; set; } = 50;

    /// <summary>
    /// A ball's radius in metres, for a model a metre in radius.
    /// </summary>
    public float Radius { get; set; } = 0.25f;

    public Handle<Model> Model { get; set; }

    public Handle<Material> Material { get; set; }

    public void Update(in Frame frame)
    {
        if (!input[Control.MouseLeft].WasPressed && !input[Control.GamepadSouth].WasPressed)
        {
            return;
        }

        Transform camera = world.Get<Transform>(owner);
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, camera.Rotation);
        Transform at = new()
        {
            Position = camera.Position + (forward * (1 + Radius)),
            Rotation = camera.Rotation,
            Scale = new Vector3(Radius),
        };
        Renderer renderer = new() { Model = Model, Materials = new MaterialSlots { Slot0 = Material } };
        Physics body = new() { Motion = Motion.Dynamic, Shape = Shape.Sphere, Bounce = 0.4f, Velocity = forward * Speed };
        _balls.Enqueue(world.Create([at, renderer, body]));
        while (_balls.Count > Most)
        {
            Entity oldest = _balls.Dequeue();
            if (world.IsAlive(oldest))
            {
                world.Destroy(oldest);
            }
        }
    }
}
