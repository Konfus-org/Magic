// What the output shows: the lit picture scaled by the exposure, also under the probes in their view, or each pixel's
// GBuffer as the view the pass header's y names asks, encoded for the screen, so a roughness of a half reads as a half
// and an albedo as the colour it is. A GBuffer view is black where nothing was drawn, but for the velocity, which the
// sky has too. The header's z is the HDR picture and w the exposure's bits.
module "ShowSurface";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Core.Pack;

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
    default:
        return ToSrgb(baseTexture.Load(pixel, 0u).rgb);
    }
}

// The lit picture for the screen. PROTO: a plain exposure curve until the tonemap is ported.
float3 GetPicture(uint hdr, float exposure, uint2 pixel)
{
    TextureRef picture = { hdr };
    return ToSrgb(1.0 - exp(-picture.Load(pixel, 0u).rgb * exposure));
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
    float3 color = isPicture ? GetPicture(header.z, asfloat(header.w), thread.xy) : GetSurfaceView(frame, view, thread.xy);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(color, 1.0));
}
