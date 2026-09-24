// The per-frame constants (256 B), uploaded untransposed from Gems/Render/Scene/FrameConstants.cs. The
// register depends on the stage, so the including shader says where it goes first:
//   #define FRAME_REGISTER VS_CB(0)    // or PS_CB(0), CS_CB(0)
//   #include "Include/Frame.hlsli"
// Camera-relative rendering: ViewProj and View expect positions relative to CameraPos.xyz, which keeps
// float precision flat however far from the origin the camera is.
#ifndef MAGIC_FRAME_HLSLI
#define MAGIC_FRAME_HLSLI

#ifndef FRAME_REGISTER
#error "Define FRAME_REGISTER (VS_CB(n), PS_CB(n) or CS_CB(n)) before including Frame.hlsli."
#endif

cbuffer Frame : FRAME_REGISTER
{
    float4x4 ViewProj;     // camera-relative view * reverse-Z projection
    float4x4 PrevViewProj; // last frame's, for motion vectors (M12)
    float4x4 View;         // rotation only
    float4 CameraPos;      // xyz absolute world position, w = time in seconds
    float4 Viewport;       // width, height, 1 / width, 1 / height
    float4 Proj;           // P11, P22, near, 0
    float4 Cull;           // minPixels, lodTarget, hizWidth, hizHeight (M3+)
};

#endif
