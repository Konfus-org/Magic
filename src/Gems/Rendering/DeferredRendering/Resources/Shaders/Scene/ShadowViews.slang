// The views that shadow maps are drawn from, C#'s GpuShadowView, 224 bytes each: what the geometry gem draws each
// shadow map's depth through, and what the shadow gem reads it back by. Positions are camera-relative.
module "Scene/ShadowViews";

import Core.Heap;

public struct GpuShadowView
{
    // The camera-relative world to the view's clip space, reversed depth, row-major, for row vectors.
    public float4x4 viewProjection;
    // The sides, the near plane and the far plane: a unit normal that points in, and the offset.
    public float4 planes[6];
    // The view's rectangle of its atlas, in texels from the top left: x, y, width and height.
    public uint4 rect;
    // What takes the view's clip x and y into the atlas's: the scale in xy, then the offset in zw.
    public float4 atlasTransform;
    // The unit direction towards the light, and the metres that one texel covers: at any depth in an orthographic
    // view, and a metre from the light in a perspective one.
    public float3 towards;
    public float texelSize;
    // 1 for an orthographic view: a caster nearer the light than its near plane is drawn at the near plane.
    public uint isOrthographic;
    // An orthographic view's metres from the near plane to the far plane, which a difference of reversed depth is a
    // share of, or a perspective view's metres to its near plane, which a reversed depth is over the view depth.
    public float depthRange;
    // Which casters it draws: ShadowCasters.
    public uint casters;
    public uint padding;
}

// Which casters a shadow view draws, C#'s ShadowCasters: all, only the still ones that a cache holds, or only the
// dynamic bodies, drawn over a copy of the cache.
public enum ShadowCasters : uint
{
    All,
    Still,
    Moving,
}

// The metres one texel of the view covers at a view depth.
public float GetTexelSizeAt(GpuShadowView view, float viewDepth)
{
    return view.isOrthographic != 0u ? view.texelSize : view.texelSize * viewDepth;
}

// A local light's row of the shadow table, C#'s GpuLightShadow, 16 bytes: its first tile, NoShadowTile for none, its
// faces, and the width of its source in metres.
public struct GpuLightShadow
{
    public uint firstTile;
    public uint faceCount;
    public float lightWidth;
    public float padding;
}

public static const uint NoShadowTile = 0xFFFFFFFFu;

// A tile of the local atlas, C#'s GpuShadowTile, 80 bytes: the anchor-relative world to its clip space, with reversed
// depth and no far plane, and its rectangle of the atlas in texels, of width 0 for a face that is not drawn.
public struct GpuShadowTile
{
    public float4x4 viewProjection;
    public uint4 rect;
}

public GpuShadowView LoadShadowView(uint table, uint index)
{
    ArrayRef<GpuShadowView> views = { table };
    return views[index];
}

// Whether a sphere in the camera-relative world is at least partly inside the view.
public bool IsSphereInShadowView(GpuShadowView view, float3 center, float radius)
{
    [unroll]
    for (uint i = 0u; i < 6u; i++)
    {
        float4 plane = view.planes[i];
        if (dot(plane.xyz, center) + plane.w < -radius)
            return false;
    }

    return true;
}

// The view's clip position of a camera-relative point, with an orthographic view's pancake: a caster nearer the
// light than the near plane clamps to it, so its shadow stays.
public float4 ToShadowClip(GpuShadowView view, float3 relative)
{
    float4 clip = mul(float4(relative, 1.0), view.viewProjection);
    if (view.isOrthographic != 0u)
        clip.z = min(clip.z, clip.w);

    return clip;
}

// The view's clip position moved into its rectangle of the atlas, which a draw over the whole atlas rasterises.
public float4 ToAtlasClip(GpuShadowView view, float4 clip)
{
    return float4(clip.xy * view.atlasTransform.xy + view.atlasTransform.zw * clip.w, clip.zw);
}

// The four clip distances that keep a triangle inside the view's rectangle of the atlas: each side, positive inside.
public float4 GetShadowClipDistances(float4 clip)
{
    return float4(clip.w + clip.x, clip.w - clip.x, clip.w + clip.y, clip.w - clip.y);
}

// Where a camera-relative point lands in the view: its texel position in the atlas, in texels from the top left, and its
// reversed depth.
public float3 GetShadowTexel(GpuShadowView view, float3 relative)
{
    float4 clip = ToShadowClip(view, relative);
    float3 ndc = clip.xyz / clip.w;
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    return float3(float2(view.rect.xy) + uv * float2(view.rect.zw), ndc.z);
}
