using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;

namespace Samples.Lights.Scripts;

/// <summary>
/// Floats each point light up and down a little, each light on its own beat.
/// </summary>
/// <remarks>
/// Each light moves on its own, so the chunks run at the same time on the worker threads.
/// </remarks>
public sealed class Bob : IParallelSystem<Transform, PointLight>
{
    private const float HeightMetres = 0.15f;
    private const float PeriodSec = 3;

    public void Run(in Frame frame, EcsMutableChunk<Transform, PointLight> chunk)
    {
        if (frame.SimDeltaSec <= 0)
            return;

        float rate = 2 * MathF.PI / PeriodSec;
        float time = (float)frame.SimSec;
        float deltaSec = (float)frame.SimDeltaSec;
        for (int i = 0; i < chunk.Entities.Length; i++)
        {
            // The speed of a sine wave, so each light rises and falls about where it started, with no state kept.
            float speed = HeightMetres * rate * MathF.Cos((time * rate) + chunk.Entities[i].Id);
            chunk.First[i].Position.Y += speed * deltaSec;
        }
    }
}
