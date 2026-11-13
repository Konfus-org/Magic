using Magic.CSharpScripting;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Projects;

namespace Samples.Lights.Scripts;

/// <summary>
/// Floats each point light up and down a little, each light on its own beat.
/// </summary>
public sealed class Bob : ISystem<Transform, PointLight>
{
    private const float HeightMetres = 0.15f;
    private const float PeriodSec = 3;

    public void Run(in Frame frame, EcsMutation<Transform, PointLight> entities)
    {
        if (frame.SimDeltaSec <= 0)
            return;

        float rate = 2 * MathF.PI / PeriodSec;
        float time = (float)frame.SimSec;
        float deltaSec = (float)frame.SimDeltaSec;
        foreach (EcsMutableChunk<Transform, PointLight> chunk in entities)
        {
            for (int i = 0; i < chunk.Entities.Length; i++)
            {
                // The speed of a sine wave, so each light rises and falls about where it started, with no state kept.
                float speed = HeightMetres * rate * MathF.Cos((time * rate) + chunk.Entities[i].Id);
                chunk.First[i].Position.Y += speed * deltaSec;
            }
        }
    }
}
