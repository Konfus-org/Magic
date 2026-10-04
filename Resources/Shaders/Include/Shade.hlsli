// The lighting's constants: the frame block followed by everything the shading reads beyond the camera's view,
// exactly as ShadeConstants in Gems/DeferredRenderer/GpuStructs.cs lays it out. Included first by the shaders
// that take it (Lighting/Lighting.comp.hlsl, the ambient occlusion and GI passes), the way Include/Pass.hlsli
// appends a pass's parameters: FRAME_APPEND is defined before Frame.hlsli is reached. Rows are 16 bytes.

#ifndef MAGIC_SHADE_HLSLI
#define MAGIC_SHADE_HLSLI

#define SHADOW_MAX_CASCADES 4

#define FRAME_APPEND \
    float4x4 CascadeViewProj[SHADOW_MAX_CASCADES]; /* camera-relative world to a cascade's clip space, reverse-Z */ \
    float4 Cascade[SHADOW_MAX_CASCADES];           /* x the view depth the cascade ends at, y metres per shadow texel, zw its tile's uv origin */ \
    float4 ShadowParams;                           /* x cascade count, y blend fraction, z filter radius in metres, w normal bias in texels */ \
    float4 ShadowAtlasUv;                          /* xy a cascade tile's size in uv, zw one atlas texel in uv */ \
    uint ShadowFlags;                              /* bit 0 the sun casts, bit 1 local lights cast */ \
    uint ShadowRecordCount;                        /* rows in the shadow record buffer */ \
    float ShadowDepthBias;                         /* shadow texels a receiver is moved towards the light */ \
    float ShadowPad1; \
    float4 ClipmapOrigin[4];                       /* xyz the absolute min corner of a GI level, w its voxel size */ \
    float4 ClipmapCameraOffset[4];                 /* xyz CameraPos minus the level's origin, w 1 over the level's extent */ \
    uint GiLevels; \
    uint GiResolution; \
    uint GiFlags;                                  /* bit 0 enabled, bit 1 specular, bits 8.. a level is valid */ \
    uint GiUpdateLevel; \
    float4 GiInteriorTint;                         /* rgb the tint times its strength, w the GI intensity */ \
    float4 SkyColor;                               /* rgb the sky's linear colour, w fade voxels */ \
    float4 GiShift;                                /* xyz the voxel shift of the level updating, w the propagation damping */ \
    float4 AoParams;                               /* x radius in metres, y the most pixels, z thickness, w strength */ \
    uint AoSlices; \
    uint AoSteps; \
    uint AoFlags;                                  /* bit 0 enabled, bit 1 multi-bounce */ \
    float AoScale;                                 /* 1 at full resolution, 0.5 at half */ \
    uint DebugView;                                /* RenderDebugView: 0 shows the scene */ \
    uint ShadePad0; \
    uint ShadePad1; \
    uint ShadePad2;
#include "Include/Frame.hlsli"

static const uint ShadowSunFlag = 1u;
static const uint ShadowLocalFlag = 2u;
static const uint ShadowNone = 0xFFFFFFFFu;

static const uint GiEnabledFlag = 1u;
static const uint GiSpecularFlag = 2u;

static const uint AoEnabledFlag = 1u;
static const uint AoMultiBounceFlag = 2u;

// RenderDebugView, as the settings enum counts it.
static const uint DebugViewNone = 0u;
static const uint DebugViewShadows = 1u;
static const uint DebugViewAo = 2u;
static const uint DebugViewBentNormal = 3u;
static const uint DebugViewGiRadiance = 4u;
static const uint DebugViewSkyVisibility = 5u;
static const uint DebugViewVoxelAlbedo = 6u;
static const uint DebugViewVoxelCoverage = 7u;

#endif
