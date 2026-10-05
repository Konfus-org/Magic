// Plans the sun's cascades for the main view, on the GPU, into the shadow view rows: one group, one thread per
// cascade. Each cascade is a slice of the camera's frustum split by the practical scheme (splitLambda between
// even and logarithmic), fitted into a sphere whose radius is rounded so it is the same every frame and whose
// centre is snapped to the cascade's own texel grid in light space, so nothing shimmers as the camera moves
// (twin, kept as the reference the GPU is checked against: Reference/Cascades.cs). The casters of a cascade
// are culled against the receivers' volume: the slice's faces that face away from the sun and a plane through
// every silhouette edge along it, up to twelve planes, relative to the cascade's eye.
//
// The far cascades are drawn again on alternate frames when staggerFar is set: a cascade that keeps last
// frame's tile keeps last frame's row too. A change to the layout (the hash in the header's generation) draws
// every tile again. The header carries what the lighting needs of the atlas (whose size the parameters repeat,
// since a depth texture cannot be asked); the refreshed list the rows the cull and the draw work on, cascades
// first (the local light selection pass adds its faces after them).

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

struct PassParams
{
    uint cascades = 4;
    uint cascadeResolution = 2048;
    float distance = 300.0;
    float splitLambda = 0.7;
    float blendFraction = 0.15;
    float casterRange = 500.0;
    bool staggerFar = true;
    uint localResolution = 256;
    uint maxLocalLights = 8;
    uint atlasWidth = 8192;  // the atlas the pass creates, in texels: the same numbers as its create
    uint atlasHeight = 2560;
};

RWStructuredBuffer<GpuShadowView> ShadowViews : WRITE(0);
RWStructuredBuffer<GpuShadowHeader> ShadowHeader : WRITE(1);
RWStructuredBuffer<uint> ShadowRefreshed : WRITE(2);

groupshared uint RefreshedRows[ShadowMaxCascades];

// The far edge of each cascade (twin: Cascades.Split).
float SplitFar(uint index, uint count, float near, float reach, float lambda)
{
    float share = (float)(index + 1u) / (float)count;
    float even = near + (reach - near) * share;
    float logarithmic = near > 0.0 ? near * pow(reach / near, share) : even;
    return index + 1u == count ? reach : lerp(even, logarithmic, lambda);
}

// A plane through onPlane with the given normal, turned to face inside.
float3 Inward(float3 normal, float3 onPlane, float3 inside)
{
    normal = normalize(normal);
    return dot(normal, inside - onPlane) < 0.0 ? -normal : normal;
}

// The eight corners of the camera's frustum slice, relative to the camera (twin: Cascades.SliceCorners).
void SliceCorners(float3 right, float3 up, float3 forward, float tanHalf, float aspect, float nearSplit, float farSplit, out float3 corners[8])
{
    [unroll] for (uint end = 0u; end < 2u; end++)
    {
        float depth = end == 0u ? nearSplit : farSplit;
        float halfHeight = depth * tanHalf;
        float halfWidth = halfHeight * aspect;
        float3 middle = forward * depth;
        corners[end * 4u + 0u] = middle + right * halfWidth + up * halfHeight;
        corners[end * 4u + 1u] = middle - right * halfWidth + up * halfHeight;
        corners[end * 4u + 2u] = middle + right * halfWidth - up * halfHeight;
        corners[end * 4u + 3u] = middle - right * halfWidth - up * halfHeight;
    }
}

// The planes of the slice swept towards the sun, relative to eyeRel (the cascade's eye, relative to the camera):
// the faces that face away from the sweep and a plane through each silhouette edge (twin: Cascades.ReceiverPlanes).
uint ReceiverPlanes(float3 corners[8], float3 sunDirection, float3 eyeRel, out float4 planes[12])
{
    float3 middle = float3(0.0, 0.0, 0.0);
    [unroll] for (uint c = 0u; c < 8u; c++)
        middle += corners[c] / 8.0;
    float3 sweep = -normalize(sunDirection);

    const uint3 faces[6] = { uint3(0u, 1u, 2u), uint3(4u, 6u, 5u), uint3(0u, 2u, 4u), uint3(1u, 5u, 3u), uint3(0u, 4u, 1u), uint3(2u, 3u, 6u) };
    const uint4 edges[12] =
    {
        uint4(0u, 1u, 0u, 4u), uint4(1u, 3u, 0u, 3u), uint4(3u, 2u, 0u, 5u), uint4(2u, 0u, 0u, 2u),
        uint4(4u, 5u, 1u, 4u), uint4(5u, 7u, 1u, 3u), uint4(7u, 6u, 1u, 5u), uint4(6u, 4u, 1u, 2u),
        uint4(0u, 4u, 4u, 2u), uint4(1u, 5u, 4u, 3u), uint4(2u, 6u, 5u, 2u), uint4(3u, 7u, 5u, 3u),
    };

    bool kept[6];
    uint count = 0u;
    [unroll] for (uint i = 0u; i < 12u; i++)
        planes[i] = float4(0.0, 0.0, 0.0, 0.0);

    [unroll] for (uint face = 0u; face < 6u; face++)
    {
        float3 a = corners[faces[face].x];
        float3 normal = Inward(cross(corners[faces[face].y] - a, corners[faces[face].z] - a), a, middle);
        kept[face] = dot(normal, sweep) >= -1e-4;
        [flatten] if (kept[face])
        {
            planes[count] = float4(normal, dot(normal, eyeRel - a));
            count++;
        }
    }

    [unroll] for (uint edge = 0u; edge < 12u; edge++)
    {
        bool isSilhouette = kept[edges[edge].z] != kept[edges[edge].w];
        float3 a = corners[edges[edge].x];
        float3 along = cross(corners[edges[edge].y] - a, sweep);
        [flatten] if (isSilhouette && dot(along, along) >= 1e-12)
        {
            float3 normal = Inward(along, a, middle);
            planes[count] = float4(normal, dot(normal, eyeRel - a));
            count++;
        }
    }

    return count;
}

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    PassParams passParams = LoadPassParams();
    uint cascadeCount = clamp(passParams.cascades, 1u, ShadowMaxCascades);
    uint tid = threadId.x;

    uint atlasWidth = passParams.atlasWidth;
    uint atlasHeight = passParams.atlasHeight;
    float2 atlasSize = float2(atlasWidth, atlasHeight);

    // The layout this frame; a change to it starts the atlas over.
    uint generation = (cascadeCount * 73856093u) ^ (passParams.cascadeResolution * 19349663u) ^ (passParams.localResolution * 83492791u)
        ^ (passParams.maxLocalLights * 2654435761u) ^ (atlasWidth * 7u) ^ (atlasHeight * 13u) ^ 0x9E3779B9u;
    bool clearAll = ShadowHeader[0].generation != generation;
    bool sunCasts = FrameHas(FrameFlagSunCastsShadows) && any(SunColor > 0.0);

    [branch] if (tid < cascadeCount)
    {
        GpuShadowView row = ShadowViews[tid];
        bool wasUsed = (row.flags.x & ShadowViewUsed) != 0u;
        bool refresh = sunCasts && (clearAll || !wasUsed || !passParams.staggerFar || tid < 2u || ((FrameNumber + tid) & 1u) == 0u);
        [branch] if (refresh)
        {
            // The camera's frustum from its own constants: the view rows are its axes, the projection its angles.
            float3 right = View[0].xyz;
            float3 up = View[1].xyz;
            float3 forward = View[2].xyz;
            float tanHalf = 1.0 / ProjScale.y;
            float aspect = ProjScale.y / ProjScale.x;
            float reach = max(passParams.distance, Near + 1.0);

            float farSplit = SplitFar(tid, cascadeCount, Near, reach, passParams.splitLambda);
            float nearSplit = tid == 0u ? Near : SplitFar(tid - 1u, cascadeCount, Near, reach, passParams.splitLambda) * (1.0 - passParams.blendFraction);

            // The sphere through the slice's corners (twin: Cascades.Fit).
            float a2 = tanHalf * tanHalf * (1.0 + aspect * aspect);
            float along = min(farSplit, 0.5 * (1.0 + a2) * (nearSplit + farSplit));
            float radius = sqrt((farSplit - along) * (farSplit - along) + a2 * farSplit * farSplit);
            radius = ceil(radius * 16.0) / 16.0;
            float texel = 2.0 * radius / (float)passParams.cascadeResolution;

            float4x4 lightRotation = LightRotation(SunDirection);
            float3 center = CameraPos + forward * along;
            float3 inLight = mul(lightRotation, float4(center, 1.0)).xyz;
            inLight.xy = floor(inLight.xy / texel) * texel;
            float3 snapped = mul(transpose(lightRotation), float4(inLight, 1.0)).xyz;
            float nearPlane = -(radius + passParams.casterRange);
            float farPlane = radius;

            float3 corners[8];
            SliceCorners(right, up, forward, tanHalf, aspect, nearSplit, farSplit, corners);
            float4 planes[12];
            uint planeCount = ReceiverPlanes(corners, SunDirection, snapped - CameraPos, planes);

            float4 tileTexels = CascadeTileTexels(tid, passParams.cascadeResolution);
            row.viewProj = mul(OrthographicReverseZ(2.0 * radius, 2.0 * radius, nearPlane, farPlane), lightRotation);
            row.rotation = lightRotation;
            row.eye = float4(snapped, texel);
            row.tileUv = TexelsToUv(tileTexels, atlasSize);
            row.tileTexels = tileTexels;
            row.range = float4(farSplit, nearPlane, farPlane, 0.0);
            row.flags = uint4(ShadowViewUsed | ShadowViewOrthographic, 1u, planeCount, tid);
            [unroll] for (uint i = 0u; i < 12u; i++)
                row.planes[i] = planes[i];
        }
        else
        {
            row.flags.y = 0u;
            row.flags.w = tid;
        }

        [flatten] if (!sunCasts)
            row.flags.x = 0u;
        ShadowViews[tid] = row;
        RefreshedRows[tid] = refresh ? 1u : 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    if (tid != 0u)
        return;

    // Cascades the layout no longer has are let go; the header and the refreshed list start with the cascades.
    [loop] for (uint spare = cascadeCount; spare < ShadowMaxCascades; spare++)
    {
        GpuShadowView row = ShadowViews[spare];
        row.flags = uint4(0u, 0u, 0u, 0u);
        ShadowViews[spare] = row;
    }

    uint refreshed = 0u;
    [loop] for (uint i = 0u; i < cascadeCount; i++)
    {
        [flatten] if (RefreshedRows[i] != 0u)
        {
            ShadowRefreshed[refreshed] = i;
            refreshed++;
        }
    }

    GpuShadowHeader header;
    header.cascadeCount = sunCasts ? cascadeCount : 0u;
    header.flags = (sunCasts ? ShadowSunFlag : 0u) | (clearAll ? ShadowClearAllFlag : 0u);
    header.refreshedCount = refreshed;
    header.generation = generation;
    header.atlasTexel = float4(1.0 / atlasSize, (float)passParams.cascadeResolution / atlasSize);
    header.layout = uint4(atlasWidth, atlasHeight, passParams.cascadeResolution, passParams.localResolution);
    header.slots = uint4(min(passParams.maxLocalLights, ShadowMaxSlots), ShadowMaxCascades + min(passParams.maxLocalLights, ShadowMaxSlots) * ShadowFacesPerSlot, 0u, 0u);
    ShadowHeader[0] = header;
}
