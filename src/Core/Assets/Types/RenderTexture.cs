namespace Magic.Assets.Types;

/// <summary>
/// A texture a camera draws into, which materials sample like any other (<see cref="MaterialParam.RenderTexture"/>).
/// It has no image: it is black until a camera has drawn into it. A <c>.rtex</c> file is this class as JSON:
/// <c>{ "width": 1024, "height": 512 }</c>.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class RenderTexture : IAsset<RenderTexture>
{
    /// <summary>
    /// Texels across: what the camera draws at.
    /// </summary>
    public int Width { get; set; } = 512;

    public int Height { get; set; } = 512;
}
