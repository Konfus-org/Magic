using Magic.Contexts.Components;
using Magic.Contexts.ECS;
using Magic.Contexts.Frames;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Sweeps its entity's sound up and down between <see cref="Low"/> and <see cref="High"/> times its pitch, once every
/// <see cref="Seconds"/>, as a siren wails: a script changing a sound by writing its <see cref="Sound"/>, which the
/// audio gem plays as it is. <c>{ "id": 3435, "low": 0.7, "high": 1.6, "seconds": 3 }</c>.
/// </summary>
internal sealed class Siren(Entity owner, World world) : IBehavior
{
    public float Low { get; set; } = 0.75f;

    public float High { get; set; } = 1.5f;

    public float Seconds { get; set; } = 3;

    public void Update(in Frame frame)
    {
        // 0 to 1 and back once a period, easing at each end.
        float rise = (1 - MathF.Cos((float)(frame.Seconds * Math.Tau / Seconds))) / 2;
        world.Get<Sound>(owner).Pitch = Low + ((High - Low) * rise);
    }
}
