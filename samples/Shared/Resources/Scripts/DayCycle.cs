using System.Numerics;
using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Turns the directional light of its entity through a day: the sun is highest at noon and below the horizon at night.
/// </summary>
/// <remarks>
/// It turns the light in the plane that the rotation of the prefab stands in, so the sun rises and sets where that
/// rotation faces, and the hour alone says how high it stands. Its colour and its intensity stay as they are, and the
/// sky follows its direction. A prefab sets the hour it starts at and how fast the day goes beside the handle of the
/// script.
/// </remarks>
public sealed class DayCycle(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// What the sun turns in an hour: one turn in a day of 24 hours.
    /// </summary>
    private const float RadiansPerHour = MathF.Tau / 24;

    /// <summary>
    /// The rotation that the entity has at noon, which the rotation of the prefab gives.
    /// </summary>
    private Quaternion _noon;

    /// <summary>
    /// The line that the sun turns about: the one through east and west of the plane that it starts in.
    /// </summary>
    private Vector3 _axis;

    /// <summary>
    /// The time of day in hours, from 0 to 24: the sun is highest at 12, noon, and lowest at 0, midnight.
    /// </summary>
    public float Hour { get; set; } = 12;

    /// <summary>
    /// How many hours of the day pass in a minute of real time. 0 holds the sun where it is.
    /// </summary>
    public float HoursPerMinute { get; set; } = 1;

    public void Start()
    {
        Quaternion rotation = world.Get<Transform>(owner).Rotation;

        // The light shines along the +Z of its entity, so the ground under that is the way the sun goes, and the
        // line across it is the one it turns about.
        Vector3 travel = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
        Vector3 ground = travel with { Y = 0 };
        _axis = ground.Length() > 0 ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, ground)) : Vector3.UnitX;

        // The sun is highest at noon, where its light goes straight down, so the rotation at noon is the one of the
        // prefab turned until it does.
        _noon = Turn(rotation, MathF.Acos(Math.Clamp(-travel.Y, -1, 1)));
    }

    public void Update(in Frame frame)
    {
        float gone = Hour + (HoursPerMinute * (float)frame.SimDeltaSec / 60);
        Hour = ((gone % 24) + 24) % 24;

        // A paused world, or a day that stands still, leaves the entity unchanged, so its renderer uploads nothing.
        Quaternion turned = Turn(_noon, (Hour - 12) * RadiansPerHour);
        if (turned != world.Get<Transform>(owner).Rotation)
            world.Mutate<Transform>(owner).Rotation = turned;
    }

    /// <summary>
    /// <paramref name="rotation"/> turned <paramref name="radians"/> about <see cref="_axis"/> in the world.
    /// </summary>
    private Quaternion Turn(Quaternion rotation, float radians) =>
        Quaternion.Normalize(Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(_axis, radians)));
}
