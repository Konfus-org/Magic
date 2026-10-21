using Magic.Contexts.Components;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Interfaces;
using Magic.Samples.Assets.Components;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Runs the day <see cref="Faster"/> times as fast while <see cref="Key"/> is held: every directional light turning by a
/// <see cref="Spin"/>, the sun and the moon, turns that much faster, and back as it was once the key is let go, so the
/// day goes by in seconds and the night comes on. <c>{ "id": 3452, "faster": 40 }</c>.
/// </summary>
internal sealed class Clock(World world, IInput input) : IBehavior
{
    /// <summary>
    /// How fast the day runs now, as a share of how it was made.
    /// </summary>
    private float _rate = 1;

    public Control Key { get; set; } = Control.KeyT;

    public float Faster { get; set; } = 40;

    public void Update(in Frame frame)
    {
        float rate = input[Key].IsDown ? Faster : 1;
        if (rate == _rate) return;

        foreach ((_, Span<Spin> spins, _) in world.Query<Spin, DirectionalLight>())
        {
            for (int i = 0; i < spins.Length; i++)
            {
                spins[i].DegreesPerSecond *= rate / _rate;
            }
        }

        _rate = rate;
    }
}
