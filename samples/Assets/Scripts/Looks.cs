using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Steps the world through <see cref="Options"/>, its looks, on <see cref="Key"/>, easing from one to the next over
/// about <see cref="Seconds"/>: it sets the params of the passes the world's effects pipeline runs, the tonemap's
/// exposure, the bloom's strength and the samples' grade, by each pass's name, while the pipeline is drawn, which
/// whatever draws the world takes from the next frame. The name of the look it is going to shows on screen.
/// <c>{ "id": 3445, "options": [ { "name": "Clear" }, { "name": "Noir", "saturation": 0 } ] }</c>.
/// </summary>
internal sealed class Looks(World world, IInput input, Services.Assets assets, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// The world's effects pipeline, once it has loaded: written whole by the load, read on the main thread.
    /// </summary>
    private Pipeline? _effects;

    /// <summary>
    /// Which of <see cref="Options"/> it is going to.
    /// </summary>
    private int _shown;

    /// <summary>
    /// The params as the world shows them now, easing toward the look it is going to.
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

    public void Start()
    {
        // The world's effects pipeline, from the first post-processing there is.
        foreach ((_, Span<PostProcessing> effects) in world.Query<PostProcessing>())
        {
            if (effects.Length > 0) LoadAsync(effects[0].Effects).FireAndForget();

            return;
        }
    }

    public void Update(in Frame frame)
    {
        if (Options.Count == 0 || _effects is not { } effects) return;

        if (input[Key].WasPressed) _shown = (_shown + 1) % Options.Count;

        // Eased toward the look at a pace no frame rate changes, nearly there once the seconds have gone by.
        Look to = Options[_shown];
        float t = 1 - MathF.Exp(-frame.DeltaSeconds * 3 / Seconds);
        _now = new Look
        {
            Exposure = float.Lerp(_now.Exposure, to.Exposure, t),
            Bloom = float.Lerp(_now.Bloom, to.Bloom, t),
            Saturation = float.Lerp(_now.Saturation, to.Saturation, t),
            Contrast = float.Lerp(_now.Contrast, to.Contrast, t),
            Tint = Vector3.Lerp(_now.Tint, to.Tint, t),
            Vignette = float.Lerp(_now.Vignette, to.Vignette, t),
        };

        // A param set to the value it has counts nothing, so once the look has settled nothing is redone.
        Set(effects, "Tonemap", "exposureBias", _now.Exposure);
        Set(effects, "Bloom", "strength", _now.Bloom);
        Set(effects, "Grade", "saturation", _now.Saturation);
        Set(effects, "Grade", "contrast", _now.Contrast);
        Set(effects, "Grade", "tint", _now.Tint);
        Set(effects, "Grade", "vignette", _now.Vignette);
        if (debugUI is { Enabled: true }) debugUI.Text($"Look: {to.Name} (L for the next)");
    }

    private static void Set(Pipeline effects, string pass, string name, MaterialParam value)
    {
        if (effects.Params.TryGetValue(pass, out MaterialParamDictionary? passParams)) passParams[name] = value;
    }

    private async Task LoadAsync(Handle<Pipeline> handle)
    {
        Result<Pipeline> loaded = await assets.LoadAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (loaded.Ok) _effects = loaded.Value;
    }
}
