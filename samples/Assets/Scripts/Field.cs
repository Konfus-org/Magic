using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Fills a square of ground around its entity with instances of a model and with point lights, spread evenly and the
/// same way every run (the R2 sequence, Roberts 2018): a script a prefab attaches as
/// <c>{ "id": 3314, "instances": 2000, "lights": 500, "model": { "id": 517 }, "material": { "id": 3322 } }</c>, to see many
/// of each at once and measure what they cost. Each instance is a dynamic ball, resting on the ground at its size
/// until something knocks it. They are its own, so they go when its entity does.
/// </summary>
internal sealed class Field(Entity owner, World world) : IScript, IDisposable
{
    private readonly List<Entity> _spawned = [];

    public void Dispose()
    {
        foreach (Entity spawned in _spawned)
        {
            if (world.IsAlive(spawned))
            {
                world.Destroy(spawned);
            }
        }
    }

    /// <summary>
    /// How many instances of <see cref="Model"/> it spawns.
    /// </summary>
    public int Instances { get; set; }

    /// <summary>
    /// How many point lights it spawns, each a colour of its own.
    /// </summary>
    public int Lights { get; set; }

    /// <summary>
    /// Metres a side of the square it fills.
    /// </summary>
    public float Size { get; set; } = 200;

    public Handle<Model> Model { get; set; }

    public Handle<Material> Material { get; set; }

    /// <summary>
    /// Spawns everything around its entity.
    /// </summary>
    public void Start()
    {
        Vector3 centre = world.Get<Transform>(owner).Position;
        Transform[] transforms = new Transform[Instances];
        Renderer[] renderers = new Renderer[Instances];
        for (int i = 0; i < Instances; i++)
        {
            float scale = 0.5f + Spread(i, 2);
            transforms[i] = new Transform
            {
                Position = centre + new Vector3((Spread(i, 0) - 0.5f) * Size, scale, (Spread(i, 1) - 0.5f) * Size),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Spread(i, 3) * MathF.Tau),
                Scale = new Vector3(scale),
            };
            renderers[i] = new Renderer { Model = Model, Materials = new MaterialSlots { Slot0 = Material } };
        }

        Transform[] places = new Transform[Lights];
        PointLight[] lights = new PointLight[Lights];
        for (int i = 0; i < Lights; i++)
        {
            places[i] = new Transform { Position = centre + new Vector3((Spread(i + Instances, 0) - 0.5f) * Size, 1.5f + (Spread(i, 4) * 2), (Spread(i + Instances, 1) - 0.5f) * Size) };
            lights[i] = new PointLight { Color = Hue(Spread(i, 5)), Intensity = 50 + (Spread(i, 6) * 150), Range = 8 + (Spread(i, 7) * 6) };
        }

        _spawned.AddRange(world.Create<Transform, Renderer>(transforms, renderers));
        Physics body = new() { Motion = Motion.Dynamic, Shape = Shape.Sphere, Bounce = 0.3f };
        foreach (Entity instance in _spawned)
        {
            world.Set(instance, body);
        }

        _spawned.AddRange(world.Create<Transform, PointLight>(places, lights));
    }

    /// <summary>
    /// The <paramref name="index"/>th value of an even sequence in 0 to 1, one of several that do not line up.
    /// </summary>
    private static float Spread(int index, int sequence)
    {
        float step = sequence switch
        {
            0 => 0.7548776662f,
            1 => 0.5698402910f,
            _ => 0.6180339887f + (sequence * 0.1234567f),
        };
        return (0.5f + (index * step)) % 1;
    }

    /// <summary>
    /// A fully saturated colour at a hue from 0 to 1.
    /// </summary>
    private static Color Hue(float hue)
    {
        float r = Math.Clamp(MathF.Abs((hue * 6) - 3) - 1, 0, 1);
        float g = Math.Clamp(2 - MathF.Abs((hue * 6) - 2), 0, 1);
        float b = Math.Clamp(2 - MathF.Abs((hue * 6) - 4), 0, 1);
        return new Color(r, g, b);
    }
}
