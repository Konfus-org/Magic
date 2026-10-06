// What the mesh fragment templates (Templates/GBuffer.frag.hlsl, Templates/Forward.frag.hlsl) share: the surface's
// inputs from the varyings, the dither a blending instance is drawn with, and the reference that keeps every
// binding alive. Included after Include/Surface.hlsli by the composed text.

#ifndef MAGIC_MESH_FRAGMENT_HLSLI
#define MAGIC_MESH_FRAGMENT_HLSLI

#include "Include/GBuffer.hlsli"
#include "Include/MeshVaryings.hlsli"
#include "Include/Surface.hlsli"
#include "Include/Impostor.hlsli"

// Keeps every pool and every interpolant alive whatever the surface reads; returns zero. DXC drops
// resources and inputs nothing uses; SDL lays a stage's storage buffers out after its samplers, so a
// surface that samples nothing would leave READ(0)..READ(7) missing and the Materials buffer at the wrong
// slot; and on the DXIL path the fragment signature is rebuilt from the inputs that survive, so a dropped
// input (a surface that ignores the tangent, say) shifts the registers away from the vertex stage's and the
// pipeline fails to link. The condition never holds (no instance has every flag set) and the texture
// reference is a runtime value, so nothing folds away: one SampleTextureGrad touches all eight pools.
float3 KeepBindingsAlive(MeshVaryings input)
{
    float3 nothing = float3(0.0, 0.0, 0.0);
    [branch] if (input.flags == 0xFFFFFFFFu)
    {
        nothing = SampleTextureGrad(input.flags, input.uv, float2(0.0, 0.0), float2(0.0, 0.0)).rgb
            + input.tangent.xyz + input.worldPosition + input.normal + float3(input.uv, asfloat(input.material))
            + asfloat(input.impostor.xyz) + asfloat(input.impostor.w);
    }

    return nothing;
}

// Negative for a pixel this version of a blending instance leaves to the other one (GpuVisible.lodFade).
float LodCoverage(float lodFade, float2 pixel)
{
    float dither = DitherValue(pixel);
    return lodFade >= 0.0 ? lodFade - dither : dither + lodFade;
}

// The vertex normal, or for a mesh without normals (all zero: a far chunk's stand-in boxes) the normal of
// the triangle itself, from how the position changes across the screen, turned to face the camera. Such a
// mesh is drawn flat, every face lit as the face it is.
float3 MeshNormal(float3 vertexNormal, float3 worldPosition, float3 positionDdx, float3 positionDdy)
{
    float3 toCamera = IsOrthographic != 0u ? -ViewToWorld(float3(0.0, 0.0, 1.0)) : CameraPos - worldPosition;
    float3 faceNormal = NormalizeOrZero(cross(positionDdx, positionDdy));
    faceNormal = dot(faceNormal, toCamera) < 0.0 ? -faceNormal : faceNormal;

    bool hasNormal = dot(vertexNormal, vertexNormal) > 1e-8;
    return hasNormal ? NormalizeOrZero(vertexNormal) : faceNormal;
}

// What the surface shader is handed for a fragment: where it is, its normal (flipped on a double-sided surface's back),
// its uv and their derivatives, where it is on screen, and which face of it the camera sees.
SurfaceInputs SurfaceInputsOf(MeshVaryings input, bool isFrontFace)
{
    SurfaceInputs surfaceInputs;
    surfaceInputs.worldPosition = input.worldPosition;
    surfaceInputs.normal = MeshNormal(input.normal, input.worldPosition, ddx(input.worldPosition), ddy(input.worldPosition));
    surfaceInputs.tangent = input.tangent;
    surfaceInputs.uv = input.uv;
    surfaceInputs.uvDdx = ddx(input.uv);
    surfaceInputs.uvDdy = ddy(input.uv);
    surfaceInputs.screenUv = input.position.xy * ViewTexel;
    surfaceInputs.time = Time;

    // A mirrored instance's winding is inverted, so what the rasteriser calls front is its back.
    bool isMirrored = (input.flags & InstanceMirrored) != 0u;
    surfaceInputs.isFrontFace = isFrontFace != isMirrored;
#if SURFACE_DOUBLE_SIDED
    surfaceInputs.normal = surfaceInputs.isFrontFace ? surfaceInputs.normal : -surfaceInputs.normal;
#endif

#if SURFACE_IMPOSTOR
    // An impostor's card: the surface is what the frame shows here, the frame picked with a dither offset from the LOD
    // fade's so the two do not line up. Its uv and normal (the model's, through the model's axes, which the varyings
    // carry) stand in for the mesh's. A card is a few pixels tall, so the material's textures are read near their
    // smallest mip (the uv steps as if the card covered the texture twice a pixel): their average colour, which is all
    // there is to see, where full detail through baked 8-bit uvs would be noise.
    uint frame = ImpostorFrame(input.impostor.z, asfloat(input.impostor.w), DitherValue(input.position.xy + float2(2.0, 1.0)));
    ImpostorTexel texel = ImpostorSample(input.impostor.xy, frame, input.uv);
    clip(texel.coverage - 0.5);
    float3 axisZ = cross(input.normal, input.tangent.xyz);
    surfaceInputs.normal = NormalizeOrZero(texel.normal.x * input.normal + texel.normal.y * input.tangent.xyz + texel.normal.z * axisZ);
    surfaceInputs.tangent = float4(0.0, 0.0, 0.0, 1.0);
    surfaceInputs.uv = texel.uv;
    surfaceInputs.uvDdx = float2(0.5, 0.0);
    surfaceInputs.uvDdy = float2(0.0, 0.5);
    surfaceInputs.isFrontFace = true;
#endif

    return surfaceInputs;
}

#endif
