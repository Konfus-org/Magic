using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Breathes its entity's point light from dark up to <see cref="Intensity"/> and back every <see cref="PeriodSec"/>: a
/// behaviour writing a light, which the renderer lights the world by as it is that frame.
/// <c>{ "id": 3442, "intensity": 200, "periodSec": 2 }</c>.
/// </summary>
internal sealed class Pulse(Entity owner, World world) : IBehavior
{
    /// <summary>
    /// The light's brightest, in candela.
    /// </summary>
    public float Intensity { get; set; } = 100;

    public float PeriodSec { get; set; } = 2;

    public void Update(in Frame frame)
    {
        // 0 to 1 and back once a period, easing at each end.
        float rise = (1 - MathF.Cos((float)(frame.WorldSec * Math.Tau / PeriodSec))) / 2;
        world.Get<PointLight>(owner).Intensity = Intensity * rise;
    }
}
