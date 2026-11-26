using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Graphics;

namespace Magic.DeferredRendering;

/// <summary>
/// The sun's cascades of one view: a row each in <see cref="Views"/>, and the size of the square atlas they are drawn
/// into, in texels.
/// </summary>
/// <remarks>
/// The shadow gem makes it for each view. The geometry gem draws each cascade's depth into its rectangle of the atlas
/// and gives the atlas back as <see cref="CascadeDepth"/>.
/// </remarks>
public readonly record struct CascadeViews(GpuBuffer Views, int Count, int AtlasSize);

/// <summary>
/// The depth of each of a view's <see cref="CascadeViews"/>, drawn into one atlas: reversed, 1 at the light's near plane
/// and 0 where nothing is.
/// </summary>
public readonly record struct CascadeDepth(GpuTexture Atlas);

/// <summary>
/// A view that a shadow map is drawn from, as the GPU reads it: the twin of <c>Scene/ShadowViews.slang</c>'s, 224
/// bytes.
/// </summary>
/// <remarks>
/// Its positions are relative to the camera of the frame, as every position on the GPU is.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuShadowView
{
    public const int Bytes = 224;

    /// <summary>
    /// The camera-relative world to the view's clip space, with reversed depth.
    /// </summary>
    public Matrix4x4 ViewProjection;

    /// <summary>
    /// The sides, the near plane and the far plane of the view, each a unit normal that points in and an offset, as
    /// the frame's frustum planes are.
    /// </summary>
    public Vector4 Plane0;

    public Vector4 Plane1;

    public Vector4 Plane2;

    public Vector4 Plane3;

    public Vector4 Plane4;

    public Vector4 Plane5;

    /// <summary>
    /// The view's rectangle of its atlas, in texels from the top left: x, y, width and height.
    /// </summary>
    public uint RectX;

    public uint RectY;

    public uint RectWidth;

    public uint RectHeight;

    /// <summary>
    /// What takes the view's clip x and y into the atlas's: the scale in x and y, then the offset.
    /// </summary>
    public Vector4 AtlasTransform;

    /// <summary>
    /// The unit direction towards the light.
    /// </summary>
    public Vector3 Towards;

    /// <summary>
    /// The metres that one texel covers.
    /// </summary>
    public float TexelSize;

    /// <summary>
    /// 1 for an orthographic view, whose casters nearer the light than its near plane are drawn at the near plane.
    /// </summary>
    public uint IsOrthographic;

    /// <summary>
    /// The metres from the near plane to the far plane, which a difference of reversed depth in an orthographic view
    /// is a share of.
    /// </summary>
    public float DepthRange;

    public uint Padding1;

    public uint Padding2;
}
