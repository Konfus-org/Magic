using Magic.Contexts.Settings;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StreamingGem;

/// <summary>
/// How the world streams in around the cameras. What is loaded beyond this is whatever the cameras see, and then
/// whatever is nearest until the Chunk budget of <see cref="AssetSettings"/> is full.
/// </summary>
[Settings("Streaming")]
[Category("World/Streaming")]
public sealed class StreamingSettings
{
    /// <summary>
    /// Metres around each camera that are always loaded, wherever it looks, so turning never waits on a load. The
    /// chunk a camera stands in is loaded however small this is.
    /// </summary>
    [DisplayName("Always loaded radius (m)")]
    [Description("Metres around each camera that stay loaded wherever it looks, so turning never waits on a load.")]
    [Range(0, 1024)]
    public float Radius { get; set; } = 128f;

    /// <summary>
    /// Metres a camera sees: the open domains' chunks its frustum touches within this are loaded. The default,
    /// <see cref="float.PositiveInfinity"/> (<c>"Infinity"</c> in a file or <c>--set</c>), is every chunk in view. The
    /// chunks within one chunk size of a camera are loaded whatever this says and wherever it looks.
    /// </summary>
    [DisplayName("View distance (m)")]
    [Description("Metres a camera sees: chunks in view within this are loaded. Infinity (the default) is every chunk in view.")]
    [Range(0, 10000)]
    public float ViewDist { get; set; } = float.PositiveInfinity;
}
