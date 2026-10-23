using System.Numerics;
using Magic.Contexts.Components;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Changes the world's weather while keys are held: [ and ] the cloud cover, - and = the wind, which carries the
/// clouds, ruffles the trees and sends waves over the grass, and , and . the fog, each added to the weather when it
/// lists none. The weather is a component, read by whatever draws the sky and the air every frame, so writing it is all
/// it takes, every frame if need be. <c>{ "id": 3448 }</c>.
/// </summary>
internal sealed class WeatherKeys(World world, IInput input) : IBehavior
{
    /// <summary>
    /// The most wind the keys raise, in metres per second.
    /// </summary>
    private const float MostWind = 25;

    /// <summary>
    /// The most fog the keys raise: its share of the light it puts out a metre.
    /// </summary>
    private const float MostFog = 0.001f;

    /// <summary>
    /// How much of its range each changes in a second of a key held.
    /// </summary>
    public float Rate { get; set; } = 0.35f;

    public void Update(in Frame frame)
    {
        // How far each pair of keys moves its part of the weather this frame, a share of that part's range.
        float step = Rate * frame.DeltaSeconds;
        float cover = Read(Control.KeyRightBracket, Control.KeyLeftBracket) * step;
        float wind = Read(Control.KeyEquals, Control.KeyMinus) * step * MostWind;
        float fog = Read(Control.KeyPeriod, Control.KeyComma) * step * MostFog;

        if (cover == 0 && wind == 0 && fog == 0) return;

        foreach ((_, Span<Weather> weathers) in world.Query<Weather>())
        {
            for (int i = 0; i < weathers.Length; i++)
            {
                ref RenderEffects<IWeatherEffect> effects = ref weathers[i].Effects;
                if (cover != 0)
                {
                    effects.TryGet(out Clouds clouds);
                    effects.Set(clouds with { Coverage = Math.Clamp(clouds.Coverage + cover, 0, 1) });
                }

                // The wind keeps its way and changes only its speed, blowing along +X when it had none.
                if (wind != 0)
                {
                    effects.TryGet(out Wind blowing);
                    Vector3 velocity = blowing.Velocity;
                    Vector3 way = velocity == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(velocity);
                    effects.Set(new Wind(way * Math.Clamp(velocity.Length() + wind, 0, MostWind)));
                }

                if (fog != 0)
                {
                    effects.TryGet(out Fog misty);
                    effects.Set(misty with { Density = Math.Clamp(misty.Density + fog, 0, MostFog) });
                }
            }
        }
    }

    /// <summary>
    /// One key's value less another's: -1 to 1.
    /// </summary>
    private float Read(Control more, Control less) => input[more].Value - input[less].Value;
}
