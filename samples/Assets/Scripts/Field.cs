using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Fills a square of ground around its entity with instances of a model and with point lights, spread evenly and the
/// same way every run (the R2 sequence, Roberts 2018): a script a prefab attaches as <c>{ "id": 3314, "instances":
/// 2000, "lights": 500, "model": { "id": 517 }, "material": { "id": 3322 } }</c>, to see many of each at once and
/// measure what they cost. Each instance is a dynamic ball, resting on the ground at its size until something knocks
/// it. They are its own, so they go when its entity does.
/// </summary>
internal sealed class Field(Entity owner, World world) : IScript, IDisposable
{
    private readonly List<Entity> _spawned = [];

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

    public void Dispose()
    {
        foreach (Entity spawned in _spawned)
        {
            if (world.IsAlive(spawned)) world.Destroy(spawned);
        }
    }

    /// <summary>
    /// Spawns everything around its entity.
    /// </summary>
    public void Start()
    {
        // Each instance spread evenly over the square, half to one and a half times the model's size, turned any way
        // about the up, and resting on the ground at its size.
        Vector3 centre = world.Get<Transform>(owner).Position;
        Transform[] transforms = new Transform[Instances];
        Renderer[] renderers = new Renderer[Instances];
        for (int i = 0; i < Instances; i++)
        {
            float scale = 0.5f + GetSpread(i, 2);
            transforms[i] = new Transform
            {
                Position = centre
                    + new Vector3((GetSpread(i, 0) - 0.5f) * Size, scale, (GetSpread(i, 1) - 0.5f) * Size),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, GetSpread(i, 3) * MathF.Tau),
                Scale = new Vector3(scale),
            };
            renderers[i] = new Renderer { Model = Model, Materials = { Material } };
        }

        // Each light spread over the square from past the instances' places in the sequence, so none stands on one,
        // 1.5 to 3.5 m up, with a hue, brightness and range of its own.
        Transform[] places = new Transform[Lights];
        PointLight[] lights = new PointLight[Lights];
        for (int i = 0; i < Lights; i++)
        {
            places[i] = new Transform
            {
                Position = centre + new Vector3(
                    (GetSpread(i + Instances, 0) - 0.5f) * Size,
                    1.5f + (GetSpread(i, 4) * 2),
                    (GetSpread(i + Instances, 1) - 0.5f) * Size)
            };
            lights[i] = new PointLight
            {
                Color = GetHueColor(GetSpread(i, 5)),
                Intensity = 50 + (GetSpread(i, 6) * 150),
                Range = 8 + (GetSpread(i, 7) * 6)
            };
        }

        _spawned.AddRange(world.Create(transforms, renderers));
        Physics body = new() { Motion = Motion.Dynamic, Shape = Shape.Sphere, Bounce = 0.3f };
        foreach (Entity instance in _spawned)
        {
            world.Set(instance, body);
        }

        _spawned.AddRange(world.Create(places, lights));
    }

    /// <summary>
    /// The <paramref name="index"/>th value of an even sequence in 0 to 1, one of several that do not line up.
    /// </summary>
    private static float GetSpread(int index, int sequence)
    {
        // Sequences 0 and 1 are R2's two axes, steps of 1/g and 1/g² for g the plastic number; the rest step by the
        // golden ratio's fraction, each offset from the last so no two line up.
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
    private static Color GetHueColor(float hue)
    {
        // Each channel a ramp of the hue round the colour wheel: red at 0 and 1, green at a third, blue at two thirds.
        float r = Math.Clamp(MathF.Abs((hue * 6) - 3) - 1, 0, 1);
        float g = Math.Clamp(2 - MathF.Abs((hue * 6) - 2), 0, 1);
        float b = Math.Clamp(2 - MathF.Abs((hue * 6) - 4), 0, 1);

        return new Color(r, g, b);
    }
}
