using Magic.Attributes;
using Magic.Systems.Streaming;

namespace Magic.Contexts.Assets;

/// <summary>
/// A world's global data. Its chunks are the <c>.chunk</c> files in the same folder: <c>x_y_z.chunk</c> cubes
/// of <see cref="ChunkSize"/> metres that stream by camera position, and any other <c>.chunk</c> (say
/// <c>globals.chunk</c>) that is loaded with the world and stays. <see cref="StreamingSystem.Open"/> selects one.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class World : Asset
{
    public int Seed { get; set; }

    /// <summary>Metres; chunk <c>x_y_z</c> spans <c>[x * size, (x + 1) * size)</c> on each axis.</summary>
    public float ChunkSize { get; set; } = 64f;

    /// <summary>Game time in seconds.</summary>
    public double Time { get; set; }
}
