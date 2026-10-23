using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Samples.Assets.Components;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Bobs every entity with a <see cref="Wave"/>, on the ECS's threads, a chunk of entities to a thread: a parallel
/// system, for work over thousands of entities, which may only write what the chunk it is handed holds. A prefab
/// attaches it to an entity of its own, <c>{ "id": 3438 }</c>, so it runs while that entity's domain is open.
/// </summary>
internal sealed class WaveSystem : IParallelSystemScript<Transform, Wave>
{
    /// <summary>
    /// Radians a second the wave moves on.
    /// </summary>
    private const float Speed = 2;

    public void Run(in Frame frame, EcsChunk<Transform, Wave> chunk)
    {
        (_, Span<Transform> transforms, Span<Wave> waves) = chunk;
        float along = (float)frame.WorldSec * Speed;
        for (int i = 0; i < transforms.Length; i++)
        {
            transforms[i].Position.Y = waves[i].Base + (waves[i].Height * MathF.Sin(along - waves[i].Phase));
        }
    }
}
