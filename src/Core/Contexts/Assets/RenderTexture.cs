using Magic.Contexts.Components;
using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// A texture a <see cref="Camera"/> draws into (<see cref="CameraTarget.Texture"/>), which materials sample like any
/// other (<see cref="MaterialParam.RenderTexture"/>). It has no image: it is black until a camera has drawn into it. A
/// <c>.rtex</c> file is this class as JSON: <c>{ "width": 1024, "height": 512 }</c>.
/// </summary>
public sealed class RenderTexture : IJsonAsset<RenderTexture>
{
    public static IReadOnlyList<string> Extensions { get; } = [".rtex"];

    public Handle<RenderTexture> Handle { get; init; }

    /// <summary>
    /// Texels across: what the camera draws at.
    /// </summary>
    public int Width { get; set; } = 512;

    public int Height { get; set; } = 512;
}
