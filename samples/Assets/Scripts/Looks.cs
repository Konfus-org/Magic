using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Steps the world through <see cref="Options"/>, its looks, on <see cref="Key"/>, easing from one to the next over
/// about <see cref="Seconds"/>: it sets the world's post effects, the tonemap's bias and the grade, where the world
/// lists them, which whatever draws the world takes from the next frame. The name of the look it is going to shows on
/// screen. <c>{ "id": 3445, "options": [ { "name": "Clear" }, { "name": "Noir", "saturation": 0 } ] }</c>.
/// </summary>
internal sealed class Looks(World world, IInput input, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// Which of <see cref="Options"/> it is going to.
    /// </summary>
    private int _shown;

    /// <summary>
    /// The look as the world shows it now, easing toward the look it is going to.
    /// </summary>
    private Look _now = new();

    /// <summary>
    /// The looks, in the order the key steps through them; one prefab's list, read by every copy made from it.
    /// </summary>
    public IReadOnlyList<Look> Options { get; set; } = [];

    public Control Key { get; set; } = Control.KeyL;

    /// <summary>
    /// About how long it takes to ease from one look to the next.
    /// </summary>
    public float Seconds { get; set; } = 1.5f;

    public void Update(in Frame frame)
    {
        if (Options.Count == 0) return;

        if (input[Key].WasPressed) _shown = (_shown + 1) % Options.Count;

        // Eased toward the look at a pace no frame rate changes, nearly there once the seconds have gone by.
        Look to = Options[_shown];
        float t = 1 - MathF.Exp(-frame.DeltaSeconds * 3 / Seconds);
        _now = new Look
        {
            Exposure = float.Lerp(_now.Exposure, to.Exposure, t),
            Saturation = float.Lerp(_now.Saturation, to.Saturation, t),
            Contrast = float.Lerp(_now.Contrast, to.Contrast, t),
            Tint = Vector3.Lerp(_now.Tint, to.Tint, t),
            Vignette = float.Lerp(_now.Vignette, to.Vignette, t),
        };

        // The grade's saturation and contrast are what they change by, none at zero, where a look's are what they are
        // times, none at one.
        foreach ((_, Span<PostProcessing> posts) in world.Query<PostProcessing>())
        {
            for (int i = 0; i < posts.Length; i++)
            {
                ref RenderEffects<IPostEffect> effects = ref posts[i].Effects;
                if (effects.TryGet(out Tonemap tonemap)) effects.Set(tonemap with { Bias = _now.Exposure });

                if (!effects.TryGet(out Grade grade)) continue;

                Grade now = grade with
                {
                    Saturation = _now.Saturation - 1,
                    Contrast = _now.Contrast - 1,
                    Tint = new Color(_now.Tint.X, _now.Tint.Y, _now.Tint.Z),
                    Vignette = _now.Vignette,
                };
                effects.Set(now);
            }
        }

        if (debugUI is { IsEnabled: true }) debugUI.Text($"Look: {to.Name} (L for the next)");
    }
}
