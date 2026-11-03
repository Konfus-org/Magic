// The air as a grid of froxels over the view, each holding what it scatters towards the camera and how much it takes
// out, then summed from the camera outward so a pixel reads what lies in front of it in one fetch.
module "Fog/Froxels";

import Core.Heap;
import Core.Frame;
import Core.Math;

public static const uint3 FogGrid = uint3(96u, 54u, 64u);
public static const float FogNear = 0.2;
public static const float FogFar = 300.0;

// What the frame's air is: C#'s FogConstants.
public struct FogConstants
{
    public float3 albedo;
    public float density;
    public float height;
    public float halfHeight;
    public float anisotropy;
    public uint isPresent;
}

// The view depth at a slice's fraction of the grid's depth, spread so near froxels are shallow.
public float GetFogDepth(float t) { return FogNear * pow(FogFar / FogNear, t); }

public float GetFogSliceFraction(float viewDepth)
{
    return saturate(log(max(viewDepth, FogNear) / FogNear) / log(FogFar / FogNear));
}

// The camera-relative position at the centre of a froxel.
public float3 GetFroxelCenter(FrameConstants frame, uint3 froxel)
{
    float2 uv = (float2(froxel.xy) + 0.5) / float2(FogGrid.xy);
    float depth = GetFogDepth((float(froxel.z) + 0.5) / float(FogGrid.z));
    float3 ray = ReconstructRelative(frame, uv, 0.5);
    float3 forward = float3(frame.view[0][2], frame.view[1][2], frame.view[2][2]);
    return ray * (depth / max(dot(ray, forward), Epsilon));
}

// How thick the air is at a height, per metre.
public float GetFogDensity(FogConstants fog, float height)
{
    return fog.density * exp2(-max(height - fog.height, 0.0) / max(fog.halfHeight, Epsilon));
}

// Henyey and Greenstein's phase: how much of the light scattered at a point goes on at a cosine to it.
public float PhaseHenyeyGreenstein(float cosine, float g)
{
    float d = 1.0 + g * g - 2.0 * g * cosine;
    return (1.0 - g * g) / (4.0 * Pi * pow(max(d, Epsilon), 1.5));
}

// The volume's coordinates for a pixel's receiver: across and down as the pixel, in as its depth.
public float3 GetFogUvw(FrameConstants frame, float2 uv, float viewDepth)
{
    return float3(uv, GetFogSliceFraction(viewDepth));
}
