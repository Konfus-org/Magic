using Magic.Contexts;
using Magic.Contexts.Components;
using Magic.Interfaces;
using Magic.Services;
using System.Numerics;

namespace Samples.Shared;

/// <summary>
/// Walks its entity round the street that rings a block: to a random spot along it, the short way, a pause, then
/// another. A townsperson out on errands.
/// </summary>
internal sealed class Wander(Handle entity, IEcs ecs, Randomness randomness) : IBehavior
{
    private Random? _random;
    private float _at = -1; // metres along the ring from (MinX, MinZ); -1 until the first frame places it
    private float _target;
    private float _pause;

    /// <summary>
    /// The rectangle the ring runs round, in world metres. The chunk sets these and the rest beside the script's id.
    /// </summary>
    public float MinX { get; set; }

    public float MinZ { get; set; }

    public float MaxX { get; set; }

    public float MaxZ { get; set; }

    /// <summary>
    /// Metres per second.
    /// </summary>
    public float Speed { get; set; } = 1.4f;

    /// <summary>
    /// Which of the run's random sequences it draws its spots and pauses from, so two walkers do not walk alike.
    /// </summary>
    public int Seed { get; set; }

    public void Update(in Frame frame)
    {
        float length = 2 * ((MaxX - MinX) + (MaxZ - MinZ));
        if (length <= 0)
            return;

        _random ??= randomness.For($"Wander/{Seed}");
        if (_at < 0)
            _at = _target = _random.NextSingle() * length;

        if (_pause > 0)
        {
            _pause -= frame.Delta;
            return;
        }

        float forward = Wrap(_target - _at, length);
        float left = MathF.Min(forward, length - forward);
        float step = Speed * frame.Delta;
        if (step >= left)
        {
            _at = _target;
            _target = _random.NextSingle() * length;
            _pause = _random.NextSingle() * 3;
        }
        else
        {
            _at = Wrap(_at + (forward <= length - forward ? step : -step), length);
        }

        ref Transform transform = ref ecs.Get<Transform>(entity);
        Vector2 at = Along(_at);
        transform.Position = new Vector3(at.X, transform.Position.Y, at.Y);
    }

    /// <summary>
    /// The point <paramref name="distance"/> metres round the ring, as (x, z).
    /// </summary>
    private Vector2 Along(float distance)
    {
        float width = MaxX - MinX, depth = MaxZ - MinZ;
        if (distance < width)
            return new Vector2(MinX + distance, MinZ);
        if ((distance -= width) < depth)
            return new Vector2(MaxX, MinZ + distance);
        if ((distance -= depth) < width)
            return new Vector2(MaxX - distance, MaxZ);
        return new Vector2(MinX, MaxZ - (distance - width));
    }

    private static float Wrap(float value, float length) => ((value % length) + length) % length;
}
