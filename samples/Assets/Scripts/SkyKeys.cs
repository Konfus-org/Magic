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
/// Changes the world's sky while keys are held: the left and right arrows how hazy its air is, and page down and page up
/// how high its clouds lie. The sky is a material, the world's <see cref="Sky"/> names it, and these are its params,
/// set on it while it is drawn, so the sky and the light it sheds change from the next frame.
/// <c>{ "id": 3447, "haze": 1, "cloudHeight": 2400 }</c>.
/// </summary>
internal sealed class SkyKeys(World world, IInput input, Services.Assets assets, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// The world's sky material, once it has loaded: written whole by the load, read on the main thread.
    /// </summary>
    private Material? _sky;

    /// <summary>
    /// Dust in the air, as a multiple of a clear day's, where it starts.
    /// </summary>
    public float Haze { get; set; } = 1;

    /// <summary>
    /// Metres above the world's origin the clouds' bases lie at, where they start.
    /// </summary>
    public float CloudHeight { get; set; } = 1500;

    public void Start()
    {
        foreach ((_, Span<Sky> skies) in world.Query<Sky>())
        {
            if (skies.Length > 0) LoadAsync(skies[0].Material).FireAndForget();

            return;
        }
    }

    public void Update(in Frame frame)
    {
        if (_sky is not { } sky) return;

        float hazier = input[Control.KeyRight].Value - input[Control.KeyLeft].Value;
        float higher = input[Control.KeyPageUp].Value - input[Control.KeyPageDown].Value;
        Haze = Math.Clamp(Haze * MathF.Exp(hazier * frame.DeltaSeconds), 0.2f, 12);
        CloudHeight = Math.Clamp(CloudHeight + (higher * 1500 * frame.DeltaSeconds), 300, 8000);

        // A param set to the value it has counts nothing, so while no key is held nothing is redone.
        sky.Params["haze"] = Haze;
        sky.Params["cloudHeight"] = CloudHeight;

        if (debugUI is not { Enabled: true }) return;

        debugUI.Text($"Haze {Haze:0.0} (left and right), clouds at {CloudHeight:0} m (page up and down)");
    }

    private async Task LoadAsync(Handle<Material> handle)
    {
        Result<Material> loaded = await assets.LoadAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (loaded.Ok) _sky = loaded.Value;
    }
}
