using Magic.Attributes.Assets;

namespace Magic.Contexts.Assets;

/// <summary>
/// A domain's global data: one openable piece of the world. Its chunks are the <c>.chunk</c> files in the same
/// folder: <c>x_y_z.chunk</c> cubes of <see cref="ChunkSize"/> metres that stream by camera position, and any other
/// <c>.chunk</c> (say <c>globals.chunk</c>) that is loaded with the domain and stays. <c>Services.World.Open</c>
/// opens one, alone or on top of those already open.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class Domain : Asset
{
    public int Seed { get; set; }

    /// <summary>
    /// Metres; chunk <c>x_y_z</c> spans <c>[x * size, (x + 1) * size)</c> on each axis.
    /// </summary>
    public float ChunkSize { get; set; } = 64f;

    /// <summary>
    /// Game time in seconds.
    /// </summary>
    public double Time { get; set; }
}
