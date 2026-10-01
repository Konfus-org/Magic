// The per-view constants (256 B), exactly as FrameConstants in Core/Contexts/Rendering/GpuStructs.cs lays
// them out, uploaded untransposed, at uniform slot 0 of every stage that reads one.
//
// One block per stage on purpose: DXC drops a cbuffer nothing reads, and SDL needs the uniform bindings a
// stage does use to be consecutive from 0, so a second block only works while the first is always used.
// Everything a stage needs per frame therefore lives here; a header that needs more of its own (Include/
// Pass.hlsli) defines FRAME_APPEND as the extra members before including this file.
//
// Members are grouped into 16-byte rows (a float3 with a scalar, two float2, four scalars), which pack the
// same under HLSL's cbuffer rules and std140. Camera-relative rendering: ViewProj and View expect positions
// relative to CameraPos.

#ifndef MAGIC_FRAME_HLSLI
#define MAGIC_FRAME_HLSLI

#include "Include/Bindings.hlsli"

cbuffer Frame : UNIFORM(0)
{
    float4x4 ViewProj;      // camera-relative view * reverse-Z projection
    float4x4 View;          // rotation only

    float3 CameraPos;       // absolute world position
    float Time;             // seconds since the renderer was built

    float2 ViewSize;        // the view's rectangle in pixels
    float2 ViewTexel;       // 1 / ViewSize

    float2 ViewOrigin;      // where the view's rectangle sits in its render target, in pixels
    float2 ProjScale;       // the projection's diagonal: P11, P22

    float Near;             // the near plane's view-space distance
    float MinPixels;        // an instance whose bounds project to a smaller radius is culled
    uint IsOrthographic;    // 1 = orthographic: the culler lets everything through
    uint HiZLevelCount;     // levels in the view's depth pyramid

    uint2 HiZSize;          // level 0 of the depth pyramid, in texels
    uint HiZFirstLevel;     // the first level the pyramid build pass being dispatched writes
    uint FramePad;

    float3 SunDirection;    // the direction the sun's light travels, normalised
    float SunDirectionPad;

    float3 SunColor;        // linear colour times intensity
    float SunColorPad;

    float3 Ambient;         // linear
    float AmbientPad;

#ifdef FRAME_APPEND
    FRAME_APPEND
#endif
};

#endif
