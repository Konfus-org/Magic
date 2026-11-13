// What the output shows, as the view the pass header's y names asks: the picture as its effects finished it, encoded
// for the screen with its middle tones raised to the gamma the header's w holds, or each pixel's GBuffer, so a
// roughness of a half reads as a half and an albedo as the colour it is. A GBuffer view is black where nothing was
// drawn, but for the velocity, which the sky has too. A depth view is also black from the metres the header's z holds.
// A view of light alone first writes it into the picture in place of the lit one, so the effects finish it as they
// would the picture: the traced diffuse light as shading reads it, which a white diffuse surface would reflect, or the
// traced reflections' light, which it reads from the frame's indirect light, whose ids are 0 where nothing is
// traced. With no effects, which the header's y tells with its top bit, the picture is first tonemapped.
module "ShowSurface";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Color;
import Light.Indirect;

// C#'s DebugView.
enum DebugView : uint
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

// Set in the pass header's y, over the view, when no effects finished the picture; C#'s Renderer.NoEffects.
static const uint NoEffects = 0x80000000u;

// The view of a pixel's surface. A depth view is black from depthViewFar metres away.
float3 GetSurfaceView(FrameConstants frame, DebugView view, float depthViewFar, uint2 pixel)
{
    if (view == DebugView.Velocity)
    {
        TextureRef velocityTexture = { frame.velocity };
        return float3(abs(velocityTexture.Load(pixel, 0u).xy) * 10.0, 0.0);
    }

    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
        return float3(0.0);

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef surfaceTexture = { frame.gbufferSurface };
    switch (view)
    {
    case DebugView.Normal:
        return LoadNormal(frame.gbufferNormal, pixel) * 0.5 + 0.5;
    case DebugView.Depth:
        // On a log scale, so what is near and what is far both read: white at the camera.
        float viewDepth = ToViewDepth(depth, frame.near);
        return float3(1.0 - saturate(log2(1.0 + viewDepth) / log2(1.0 + depthViewFar)));
    case DebugView.Roughness:
        return float3(surfaceTexture.Load(pixel, 0u).x);
    case DebugView.Metalness:
        return float3(baseTexture.Load(pixel, 0u).a);
    case DebugView.Occlusion:
        return float3(surfaceTexture.Load(pixel, 0u).z);
    default:
        return ToSrgb(baseTexture.Load(pixel, 0u).rgb);
    }
}

// The light the indirect diffuse or the reflections view shows at the pixel, black where none was traced.
float3 GetLightView(FrameConstants frame, DebugView view, uint2 pixel)
{
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
        return float3(0.0);

    IndirectLight indirect = GetIndirectLight();
    if (view == DebugView.Reflections)
    {
        TextureRef reflections = { indirect.reflections };
        if (reflections.id == 0u)
            return float3(0.0);

        return reflections.Load(GetTracedTexel(frame, pixel, reflections.GetSize(0u)), 0u).rgb;
    }

    if (indirect.diffuse == 0u)
        return float3(0.0);

    float3 relative = ReconstructPixel(frame, pixel, depth);
    return SampleIndirectDiffuse(frame, indirect.diffuse, pixel, relative, LoadNormal(frame.gbufferNormal, pixel));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShowLight(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    StorageRef picture = { frame.hdr, 0u };
    picture.Store(thread.xy, float4(GetLightView(frame, (DebugView)GetPassHeader().y, thread.xy), 1.0));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ShowSurface(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    PassHeader header = GetPassHeader();
    DebugView view = (DebugView)(header.y & ~NoEffects);
    float depthViewFar = asfloat(header.z);
    float gamma = asfloat(header.w);
    bool isPicture = view == DebugView.Final || view == DebugView.IndirectDiffuse || view == DebugView.Reflections;
    TextureRef picture = { frame.hdr };
    float3 light = picture.Load(thread.xy, 0u).rgb;
    // Tonemapped as Reinhard does, so no light is above white.
    if ((header.y & NoEffects) != 0u)
        light /= 1.0 + light;

    float3 color = isPicture
        ? pow(saturate(ToSrgb(light)), 1.0 / gamma)
        : GetSurfaceView(frame, view, depthViewFar, thread.xy);
    StorageRef output = { frame.output, 0u };
    output.Store(thread.xy, float4(color, 1.0));
}
