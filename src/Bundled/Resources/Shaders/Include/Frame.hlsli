// The one constant block every stage of every pass is pushed, as the renderer's FrameConstants (GpuStructs.cs) lays it
// out member for member: change both together. A view's constants, then the pass's own rows: its params, which the
// generated LoadPassParams() unpacks, and in the last row its iteration. Camera-relative: ViewProj takes a position
// minus CameraPosition. Reverse-Z with no far plane: a depth is Near over the view depth, 0 where nothing was drawn.

#ifndef MAGIC_FRAME_HLSLI
#define MAGIC_FRAME_HLSLI

#include "Include/Bindings.hlsli"

// Pixels a side of a light tile; the renderer's Views.TileSize.
static const uint LightTileSize = 32u;

// The world's weather, still and clear without one: the renderer's WeatherConstants (GpuStructs.cs), member for member.
struct WeatherConstants
{
    float3 Wind;             // metres per second: its direction and strength
    float Coverage;          // how much of the sky clouds cover, 0 clear to 1 overcast
    float3 CloudColor;       // the clouds where the sun lights them, linear
    float Density;           // how thick they are, 0 wisps to 1 thick
    float3 CloudShadow;      // the clouds in their own shade and underneath, linear
    float Padding;
};

cbuffer Frame : UNIFORM(0)
{
    float4x4 ViewProj;
    float4x4 View;

    float3 CameraPosition;
    float Time;

    float2 ViewSize;
    float2 ViewTexel;

    float2 ProjScale;
    float Near;
    float Far;

    float3 SunDirection;     // the way its light travels
    float SunAngularRadius;

    float3 SunIlluminance;   // lux times its colour; zero with no sun
    uint LightCount;

    uint FrameIndex;
    uint TileCountX;
    uint TileWords;
    uint TargetEncoding;     // 0 linear as it is, 1 sRGB, 2 PQ (HDR10), 3 scRGB: see Output.frag.hlsl

    float ZBinScale;
    float ZBinBias;
    uint ZBinCount;
    uint SkyMaterial;        // the sky's material's row, which a sky draw loads its params from

    float DeltaSeconds;
    float ClusterError;      // pixels a cluster's error may cover on screen before a finer one is drawn
    float2 Padding;

    // The last frame's ViewProj without its jitter, for a position relative to the last frame's camera.
    float4x4 PrevViewProj;

    float3 CameraMotion;     // the camera's position less the last frame's: a still point's relative position plus
                             // this is where it was relative to the last frame's camera
    float PixelScale;        // pixels a metre covers one metre in front of the camera, along the view's height

    float3 PriorityPosition; // the player's camera relative to this one, which detail is kept nearest to
    float TextureMipScale;   // what uv gradients are scaled by: below 1 to sharpen textures a temporal upscale resolves

    float2 Jitter;           // this frame's sub-pixel offset of ViewProj, in clip space
    float2 OutputSize;       // the view's size in pixels as it is shown, which upscaling fills; the drawn size when the
                             // output filter is nearest, so only the output pass scales

    WeatherConstants Weather;

    uint4 PassRaw[8];
};

// Which run of a pass that runs for each of a count this is.
uint PassIteration()
{
    return PassRaw[7].x;
}

// The distance along the view's +Z a depth stands for; only for a depth above 0.
float ViewDepth(float depth)
{
    return Near / max(depth, 1e-12);
}

// A view-space position from a point of the view (pixels from its top left, a pixel's centre at +0.5) and its depth,
// the frame's jitter taken back out, so what a pixel shows stays where it is as the view is drawn a sub-pixel off.
float3 ViewPosition(float2 pixel, float viewDepth)
{
    float2 ndc = float2(pixel.x * ViewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * ViewTexel.y * 2.0) - Jitter;
    return float3(ndc / ProjScale * viewDepth, viewDepth);
}

// Where a view-space position in front of the camera falls on the view, in pixels from its top left, as the view is
// drawn a sub-pixel off (Jitter), which ViewPosition took out.
float2 ViewPixel(float3 seen)
{
    float2 ndc = seen.xy * ProjScale / seen.z + Jitter;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * ViewSize;
}

// View is a rotation, so its transpose undoes it.
float3 ViewToWorld(float3 direction)
{
    return mul(float4(direction, 0.0), View).xyz;
}

float3 WorldToView(float3 direction)
{
    return mul(View, float4(direction, 0.0)).xyz;
}

// The z-bin a view depth falls in, of ZBinCount, evenly in its logarithm.
uint ZBinOf(float viewDepth)
{
    float bin = log2(max(viewDepth, Near)) * ZBinScale + ZBinBias;
    return min((uint)max(bin, 0.0), ZBinCount - 1u);
}

#endif
