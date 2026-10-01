using Magic.Attributes;

namespace Magic.Contexts.Assets;

/// <summary>
/// A texture a camera draws into (<see cref="Components.RenderTarget"/>) and materials sample like any other: a
/// material's texture parameter names its id. There is no image: it is black until a camera has drawn into it. A
/// <c>.rtex</c> file is this class as JSON, <c>{ "width": 512, "height": 512 }</c>; the size is what the camera renders
/// at, fitted into the renderer's texture pools like an image of that size.
/// </summary>
[AssetFormat(AssetFormat.Json)]
public sealed class RenderTexture : Asset
{
    public const string Extension = ".rtex";

    public int Width { get; set; } = 512;

    public int Height { get; set; } = 512;

    /// <summary>
    /// Whether the asset at <paramref name="path"/> is a render texture. A material's texture parameter may name either
    /// kind by id, so whoever loads what one names looks here first.
    /// </summary>
    public static bool IsAt(string? path)
    {
        return path?.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) == true;
    }
}
