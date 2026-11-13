using System.Numerics;
using Magic.Ecs;

namespace Magic.CSharpScripting;

/// <summary>
/// Tells how often a behaviour runs its <see cref="IBehavior.Update"/> and <see cref="IBehavior.LateUpdate"/>, by its
/// distance from the nearest camera of the player: the farther, the less often.
/// </summary>
internal static class Pacing
{
    // PROTO: hard-coded until a slice decides what a project tunes here.
    private const float FullRateMetres = 64;
    private const int MaxIntervalFrames = 32;

    /// <summary>
    /// Gets the frames between two updates of a behaviour at <paramref name="position"/>: 1 within
    /// <see cref="FullRateMetres"/> of the nearest of <paramref name="cameras"/>, n at n times that distance, up to
    /// <see cref="MaxIntervalFrames"/>. It is 1 when there is no camera.
    /// </summary>
    public static int GetIntervalFrames(Vector3 position, ReadOnlySpan<Vector3> cameras)
    {
        if (cameras.IsEmpty)
            return 1;

        float nearest = float.MaxValue;
        foreach (Vector3 camera in cameras)
        {
            nearest = MathF.Min(nearest, Vector3.DistanceSquared(position, camera));
        }

        return (int)Math.Clamp(MathF.Ceiling(MathF.Sqrt(nearest) / FullRateMetres), 1, MaxIntervalFrames);
    }

    /// <summary>
    /// Whether the behaviour of <paramref name="entity"/> updates in frame <paramref name="frameIndex"/>.
    /// </summary>
    /// <remarks>
    /// The entities take turns by their ids, so that the far behaviours do not all update in the same frame.
    /// </remarks>
    public static bool IsDue(Entity entity, uint frameIndex, int intervalFrames) =>
        (frameIndex + (uint)entity.Id) % (uint)intervalFrames == 0;
}
