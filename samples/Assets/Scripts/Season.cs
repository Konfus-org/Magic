using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Input;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Brings the snow down the mountains, and back up, while the down and up arrows are held: it sets the snow line of
/// every one of <see cref="Materials"/>, the terrain's and the trees', on the materials themselves while they are drawn,
/// so the slopes and the forest whiten together from the next frame, and only what those materials draw is redone. The
/// line it is at shows on screen.
/// <c>{ "id": 3446, "materials": [ { "id": 3600 }, { "id": 3602 } ], "snowLine": 2700 }</c>.
/// </summary>
internal sealed class Season(Services.Assets assets, IInput input, IDebugUI? debugUI = null) : IBehavior
{
    /// <summary>
    /// The materials, once they have loaded: written whole by the load, read on the main thread.
    /// </summary>
    private Material[]? _loaded;

    /// <summary>
    /// The materials whose <c>snowLine</c> param it moves; one prefab's list, read by every copy made from it.
    /// </summary>
    public IReadOnlyList<Handle<Material>> Materials { get; set; } = [];

    /// <summary>
    /// Metres above the world's origin snow lies above, where it starts.
    /// </summary>
    public float SnowLine { get; set; } = 2700;

    public float MetresPerSecond { get; set; } = 500;

    public float Lowest { get; set; }

    public float Highest { get; set; } = 4400;

    public void Start() => LoadAsync().FireAndForget();

    public void Update(in Frame frame)
    {
        if (_loaded is not { } materials) return;

        float move = Axis.Read(input, Control.KeyUp, Control.KeyDown);
        SnowLine = Math.Clamp(SnowLine + (move * MetresPerSecond * frame.WorldDeltaSec), Lowest, Highest);

        // A param set to the value it has counts nothing, so while no key is held nothing is redone.
        foreach (Material material in materials)
        {
            material.Params["snowLine"] = SnowLine;
        }

        if (debugUI is { IsEnabled: true }) debugUI.Text($"Snow line: {SnowLine:0} m (up and down arrows)");
    }

    private async Task LoadAsync()
    {
        List<Material> loaded = [];
        foreach (Handle<Material> handle in Materials)
        {
            Result<Material> material = await assets.LoadAsync(handle, CancellationToken.None).ConfigureAwait(false);
            if (material.Ok) loaded.Add(material.Value);
        }

        _loaded = [.. loaded];
    }
}
