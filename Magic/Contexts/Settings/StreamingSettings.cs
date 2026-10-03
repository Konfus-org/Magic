namespace Magic.Contexts.Settings;

/// <summary>
/// How the world streams in around the cameras. What is loaded beyond this is whatever the cameras see, and then
/// whatever is nearest until the Chunk budget of <see cref="AssetSettings"/> is full.
/// </summary>
public sealed class StreamingSettings
{
    /// <summary>
    /// Metres around each camera that are always loaded, wherever it looks, so turning never waits on a load. The
    /// chunk a camera stands in is loaded however small this is.
    /// </summary>
    public float Radius { get; set; } = 128f;
}
