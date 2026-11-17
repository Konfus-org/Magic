using System.Numerics;
using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.SamplesShared.Resources.Scripts;

/// <summary>
/// Turns the directional light of its entity through a day: the sun rises at 6, is highest at noon, sets at 18 and is
/// below the horizon at night.
/// </summary>
/// <remarks>
/// The rotation of the prefab faces west, where the sun sets, and the sun rises opposite it. At noon the sun stands
/// toward the south, the left of that facing, as high as <see cref="Latitude"/> lets it. Its colour and its intensity stay as they are, and the
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
    /// The line that the sun turns about at the equator: the one through north and south, level with the ground.
    /// </summary>
    private Vector3 _axis;

    /// <summary>
    /// The line through east and west, level with the ground, that <see cref="Latitude"/> tilts the path of the sun
    /// about.
    /// </summary>
    private Vector3 _west;

    /// <summary>
    /// The time of day in hours, from 0 to 24: the sun is highest at 12, noon, and lowest at 0, midnight.
    /// </summary>
    public float Hour { get; set; } = 12;

    /// <summary>
    /// How many hours of the day pass in a minute of real time. 0 holds the sun where it is.
    /// </summary>
    public float HoursPerMinute { get; set; } = 1;

    /// <summary>
    /// How far north of the equator the place is, in degrees. The sun stands 90 minus this above the horizon at noon:
    /// 0 puts it straight overhead, and a negative value puts it toward the north.
    /// </summary>
    public float Latitude { get; set; } = 35;

    public void Start()
    {
        Quaternion rotation = world.Get<Transform>(owner).Rotation;

        // The light shines along the +Z of its entity, so the ground under that is the way the sun goes, and the
        // line across it is the one it turns about.
        Vector3 travel = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
        Vector3 ground = travel with { Y = 0 };
        _axis = ground.Length() > 0 ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, ground)) : Vector3.UnitX;
        _west = Vector3.Cross(_axis, Vector3.UnitY);

        // At the equator the sun is straight overhead at noon, so the rotation at noon there is the one of the prefab
        // turned until its light goes straight down.
        _noon = Turn(rotation, MathF.Acos(Math.Clamp(-travel.Y, -1, 1)));
    }

    public void Update(in Frame frame)
    {
        float gone = Hour + (HoursPerMinute * (float)frame.SimDeltaSec / 60);
        Hour = ((gone % 24) + 24) % 24;

        // A paused world, or a day that stands still, leaves the entity unchanged, so its renderer uploads nothing.
        // The path at the equator, tilted about the line through east and west: the sun still rises and sets there, and
        // stands lower toward the south at noon.
        Quaternion tilt = Quaternion.CreateFromAxisAngle(_west, float.DegreesToRadians(Latitude));
        Quaternion day = Turn(_noon, (Hour - 12) * RadiansPerHour);
        Quaternion turned = Quaternion.Normalize(Quaternion.Concatenate(day, tilt));
        if (turned != world.Get<Transform>(owner).Rotation)
            world.Mutate<Transform>(owner).Rotation = turned;
    }

    /// <summary>
    /// <paramref name="rotation"/> turned <paramref name="radians"/> about <see cref="_axis"/> in the world.
    /// </summary>
    private Quaternion Turn(Quaternion rotation, float radians) =>
        Quaternion.Normalize(Quaternion.Concatenate(rotation, Quaternion.CreateFromAxisAngle(_axis, radians)));
}
