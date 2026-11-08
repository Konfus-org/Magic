// What the output shows: the lit picture at the metered exposure through the tonemap, also under the probes in their
// view, or each pixel's GBuffer as the view the pass header's y names asks, encoded for the screen, so a roughness of a
// half reads as a half and an albedo as the colour it is, or the traced diffuse light as shading reads it, which a white
// diffuse surface would reflect, or the traced reflections' light, both through the tonemap. A GBuffer view is black
// where nothing was drawn, but for the velocity, which the sky has too. The header's z is the traced reflections' light
// and w the traced diffuse constants, each 0 where there are none.
module "ShowSurface";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;
import Post.Agx;
import Scene.TracedDiffuse;
import Scene.Traced;

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
    Probes,
    TracedDiffuse,
    Reflections,
}

// How far a depth view reaches, in metres: what is this far away or farther is black.
static const float DepthViewFar = 1000.0;

float3 GetSurfaceView(FrameConstants frame, SurfaceView view, uint2 pixel)
{
    if (view == SurfaceView.Velocity)
    {
        TextureRef velocityTexture = { frame.velocity };
        return float3(abs(velocityTexture.Load(pixel, 0u).xy) * 10.0, 0.0);
    }

    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(pixel, 0u) == 0u)
        return float3(0.0);

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef depthTexture = { frame.depth };
    switch (view)
    {
    case SurfaceView.Normal:
        return DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0) * 0.5 + 0.5;
    case SurfaceView.Depth:
        // On a log scale, so what is near and what is far both read: white at the camera.
        float viewDepth = ToViewDepth(depthTexture.Load(pixel, 0u).x, frame.near);
        return float3(1.0 - saturate(log2(1.0 + viewDepth) / log2(1.0 + DepthViewFar)));
    case SurfaceView.Roughness:
        return float3(surfaceTexture.Load(pixel, 0u).x);
    case SurfaceView.Metalness:
        return float3(baseTexture.Load(pixel, 0u).a);
    case SurfaceView.Occlusion:
        return float3(surfaceTexture.Load(pixel, 0u).z);
    case SurfaceView.TracedDiffuse:
        if (GetPassHeader().w == 0u)
            return float3(0.0);

        float2 uv = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
        float3 relative = ReconstructRelative(frame, uv, depthTexture.Load(pixel, 0u).x);
        float3 normal = DecodeOctahedral(normalTexture.Load(pixel, 0u).xy * 2.0 - 1.0);
        float3 light = SampleTracedDiffuse(frame, GetTracedDiffuse(), pixel, relative, normal);
        return ToSrgb(ToAgx(light, GetExposure(frame)));
    case SurfaceView.Reflections:
        TextureRef reflections = { GetPassHeader().z };
        return reflections.id == 0u
            ? float3(0.0)
            : ToSrgb(ToAgx(reflections.Load(pixel / TracedScale, 0u).rgb, GetExposure(frame)));
    default:
        return ToSrgb(baseTexture.Load(pixel, 0u).rgb);
    }
}

// The lit picture for the screen.
float3 GetPicture(FrameConstants frame, uint2 pixel)
{
    TextureRef picture = { frame.hdr };
    return ToSrgb(ToAgx(picture.Load(pixel, 0u).rgb, GetExposure(frame)));
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
    bool isPicture = view == SurfaceView.Final || view == SurfaceView.Probes;
    float3 color = isPicture ? GetPicture(frame, thread.xy) : GetSurfaceView(frame, view, thread.xy);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(color, 1.0));
}
