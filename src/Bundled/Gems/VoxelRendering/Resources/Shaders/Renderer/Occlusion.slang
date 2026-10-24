// How much of the world around each pixel's surface hides it, traced through the voxels (Renderer/Voxels), a thread a
// pixel of the view: six cones of 60 degrees over the surface's hemisphere, one along its normal and five about it,
// weighted by the share of the hemisphere's light each gathers (Crassin et al. 2011), traced a short way for what is
// near and on far for the sky. It writes, in the occlusion texture, the direction the sky is least hidden in (the bent
// normal, on the octahedron), what lets light through nearby, and how much of the sky is seen, 1 open to 0 hidden.

module "Renderer/Occlusion";

import Core.GBuffer;
import Core.Math;
import Renderer.Voxels;

// The pipeline script's OcclusionConstants, member for member.
struct OcclusionConstants
{
    GBuffer gbuffer;
    VoxelScene voxels;
    TextureRef occlusion;    // written where something was drawn
}

// Metres the cones are traced for what is near, and for the sky; and the tangent of their half-angle.
static const float NearRange = 2.0;
static const float SkyRange = 64.0;
static const float ConeSlope = 0.57735;

// The cones' weights: the one along the normal, and each of the five about it, which together make π.
static const float AlongWeight = 0.785398;
static const float AboutWeight = 0.471239;

[shader("compute")]
[numthreads(8, 8, 1)]
void TraceOcclusion(uint3 id : SV_DispatchThreadID, uniform PassHeader pass, uniform OcclusionConstants c)
{
    BeginPass(pass);
    int2 pixel = int2(id.xy);
    if (any(id.xy >= (uint2)View.viewSize))
    {
        return;
    }

    float depth = c.gbuffer.depth.Load(pixel, 0).r;
    if (depth <= 0.0)
    {
        return;
    }

    float3 normal = DecodeOctahedral(c.gbuffer.normal.Load(pixel, 0).xy * 2.0 - 1.0);
    float3 position = View.ToWorld(View.ToViewPosition(float2(id.xy) + 0.5, View.GetViewDepth(depth)))
        + View.cameraPosition;
    uint finest = c.voxels.GetFinestLevel(position);
    if (finest >= c.voxels.levelCount)
    {
        c.occlusion.GetLevel(0u)[id.xy] = float4(EncodeOctahedral(normal) * 0.5 + 0.5, 1.0, 1.0);
        return;
    }

    // The cones leave a voxel and a half off the surface, so it does not hide itself.
    float voxel = c.voxels.levels[finest].voxelSize;
    float3 side = abs(normal.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 tangent = normalize(cross(side, normal));
    float3 bitangent = cross(normal, tangent);
    Cone cone;
    cone.origin = position + normal * (1.5 * voxel);
    cone.normal = normal;
    cone.tanHalfAngle = ConeSlope;
    cone.start = voxel;

    float nearOpen = 0.0;
    float skyOpen = 0.0;
    float3 bent = float3(0.0, 0.0, 0.0);
    [loop] for (uint i = 0u; i < 6u; i++)
    {
        // The first along the normal; the rest 60 degrees from it, a fifth of a turn apart.
        float turn = (float)i * (6.2831853 / 5.0);
        cone.direction = i == 0u
            ? normal
            : normal * 0.5 + (tangent * cos(turn) + bitangent * sin(turn)) * 0.8660254;
        float weight = i == 0u ? AlongWeight : AboutWeight;
        cone.end = NearRange;
        nearOpen += c.voxels.TraceTransmittance(cone) * weight;
        cone.end = SkyRange;
        float open = c.voxels.TraceTransmittance(cone);
        skyOpen += open * weight;
        bent += cone.direction * (open * weight);
    }

    float3 bentNormal = dot(bent, bent) > 1e-6 ? normalize(bent) : normal;
    float nearSeen = nearOpen / 3.1415927;
    float sky = skyOpen / 3.1415927;
    c.occlusion.GetLevel(0u)[id.xy] = float4(EncodeOctahedral(bentNormal) * 0.5 + 0.5, nearSeen, sky);
}
