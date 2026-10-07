// The one constant block (384 B), exactly as PassConstants in the renderer's Internal/GpuStructs.cs lays it
// out, uploaded untransposed, at uniform slot 0 of every stage: the view's constants (FrameConstants, 256 B),
// then the pass's own words (PassRaw, 128 B): the parameters its .pass or .post file set, packed by the CPU
// and unpacked by the generated LoadPassParams(), with the last row the executor's (PassIteration() and
// friends).
//
// One block per stage on purpose: DXC drops a cbuffer nothing reads, and SDL needs the uniform bindings a
// stage does use to be consecutive from 0, so a second block only works while the first is always used.
// Everything a stage needs per frame therefore lives here; a header that still needs more of its own
// defines FRAME_APPEND as the extra members before including this file.
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
    uint Flags;             // FrameFlag* bits, and the view's index in the frame's view list in the top 16
    float LodBias;          // scales an instance's height on screen before its LOD thresholds

    float3 SunDirection;    // the direction the sun's light travels, normalised
    float Far;              // the far plane's view-space distance; infinite for a perspective view without one

    float3 SunColor;        // linear colour times intensity
    uint FrameNumber;       // the frame counter

    float3 Ambient;         // linear: the sky's colour when a Sky entity set one
    uint LightCount;        // rows in the lights buffer (GpuLight, generated from GpuStructs.cs): the point and spot lights

    uint4 PassRaw[8];       // the pass's parameters (rows 0..6, LoadPassParams) and the executor's row 7

#ifdef FRAME_APPEND
    FRAME_APPEND
#endif
};

// The FrameFlag* bits of Flags are written into the header from FrameConstants (GpuStructs.cs).

// What is shown in place of the lit scene is in bits 8..15 of Flags: one of the DebugView* the header written
// from DeferredRenderer's DebugView defines.

uint FrameDebugView() { return (Flags >> 8u) & 0xFFu; }

// Which run of a pass that runs several times this is, how many there are, and the view's index.
uint PassIteration() { return PassRaw[7].x; }
uint PassIterationCount() { return PassRaw[7].y; }
uint PassViewIndex() { return Flags >> 16u; }
bool FrameHas(uint flag) { return (Flags & flag) != 0u; }

#endif
