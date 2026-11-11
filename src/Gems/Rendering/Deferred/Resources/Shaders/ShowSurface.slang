// What the output shows, as the view the pass header's y names asks: the picture as its effects finished it, encoded
// for the screen, or each pixel's GBuffer, so a roughness of a half reads as a half and an albedo as the colour it is.
// A GBuffer view is black where nothing was drawn, but for the velocity, which the sky has too; a depth view is also
// black from the metres the header's z holds. A view of light alone first writes it into the picture in place of the
// lit one, so the effects finish it as they would the picture: the traced diffuse light as shading reads it, which a
// white diffuse surface would reflect, or the traced reflections' light, which it reads from the frame's GI constants,
// whose light ids are 0 where nothing is traced.
module "ShowSurface";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Light.Indirect;

// C#'s DebugView.
enum SurfaceView : uint
{
    Final,
    Albedo,
    Normal,
    Depth,
    Roughness,
    Metalness,
    Occlusion,
    Velocity,
    IndirectDiffuse,
    Reflections,
}

// The view of a pixel's surface; a depth view is black from depthViewFar metres away.
float3 GetSurfaceView(FrameConstants frame, SurfaceView view, float depthViewFar, uint2 pixel)
{
    if (view == SurfaceView.Velocity)
    {
        TextureRef velocityTexture = { frame.velocity };
        return float3(abs(velocityTexture.Load(pixel, 0u).xy) * 10.0, 0.0);
    }

    TextureRef depthTexture = { frame.depth };
    if (depthTexture.Load(pixel, 0u).x == 0.0)
        return float3(0.0);

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    TextureRef normalTexture = { frame.gbufferNormal };
    switch (view)
    {
    case SurfaceView.Normal:
        return DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0) * 0.5 + 0.5;
    case SurfaceView.Depth:
        // On a log scale, so what is near and what is far both read: white at the camera.
        float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
        return float3(1.0 - saturate(log2(1.0 + viewDepth) / log2(1.0 + depthViewFar)));
    case SurfaceView.Roughness:
        return float3(surfaceTexture.Load(pixel, 0u).x);
    case SurfaceView.Metalness:
        return float3(baseTexture.Load(pixel, 0u).a);
    case SurfaceView.Occlusion:
        return float3(surfaceTexture.Load(pixel, 0u).z);
    default:
        return ToSrgb(baseTexture.Load(pixel, 0u).rgb);
    }
}

// The light the indirect diffuse or the reflections view shows at the pixel, black where none was traced.
float3 GetLightView(FrameConstants frame, SurfaceView view, uint2 pixel)
{
    TextureRef depthTexture = { frame.depth };
    if (depthTexture.Load(pixel, 0u).x == 0.0)
        return float3(0.0);

    if (view == SurfaceView.Reflections)
    {
        TextureRef reflections = { GetIndirectLight().reflections };
        if (reflections.id == 0u)
            return float3(0.0);

        return reflections.Load(GetTracedTexel(frame, pixel, reflections.GetSize(0u)), 0u).rgb;
    }

    uint diffuse = GetIndirectLight().diffuse;
    if (diffuse == 0u)
        return float3(0.0);

    TextureRef normalTexture = { frame.gbufferNormal };
    float2 uv = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
    float3 relative = ReconstructRelative(frame, uv, depthTexture.Load(pixel, 0u).x);
    float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
    return SampleIndirectDiffuse(frame, diffuse, pixel, relative, normal);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShowLight(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    StorageRef picture = { frame.hdr, 0u };
    picture.Store(thread.xy, float4(GetLightView(frame, (SurfaceView)GetPassHeader().y, thread.xy), 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShowSurface(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    PassHeader header = GetPassHeader();
    SurfaceView view = (SurfaceView)header.y;
    float depthViewFar = asfloat(header.z);
    bool isPicture = view == SurfaceView.Final || view == SurfaceView.IndirectDiffuse || view == SurfaceView.Reflections;
    TextureRef picture = { frame.hdr };
    float3 color = isPicture
        ? ToSrgb(picture.Load(thread.xy, 0u).rgb)
        : GetSurfaceView(frame, view, depthViewFar, thread.xy);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(color, 1.0));
}
