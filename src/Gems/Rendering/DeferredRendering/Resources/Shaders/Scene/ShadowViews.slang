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
    // The unit direction towards the light, and the metres that one texel covers.
    public float3 towards;
    public float texelSize;
    // 1 for an orthographic view: a caster nearer the light than its near plane is drawn at the near plane.
    public uint isOrthographic;
    // The metres from the near plane to the far plane, which a difference of reversed depth in an orthographic view is
    // a share of.
    public float depthRange;
    public uint2 padding;
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
