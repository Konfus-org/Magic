// How much of a local light reaches a point, from the light's tiles of the local atlas: one perspective tile a spot
// light, a cube's six faces a point light, and five a rect light, which casts nothing behind its face. The face is the
// one whose axis, in the light's own axes, is nearest the direction from the light. The point moves along its normal
// by a part of the tile's texel at its distance first, as the sun's does (Holbert, "Normal Offset Shadows", 2010). The
// shadow is contact-hardening (Light/ShadowFilter.slang): its penumbra is as wide as the light's source seen from the
// point past its casters (Fernando, "Percentage-Closer Soft Shadows", 2005).
module "Light/LocalShadows";

import Core.Heap;
import Core.Math;
import Scene.Lights;
import Scene.Shadows;
import Scene.ShadowViews;
import Light.LocalLights;
import Light.ShadowFilter;

// The least the point moves along its normal, in texels of its tile, where the light faces the surface.
static const float LocalMinNormalBias = 0.5;

// The face of a light's tiles that a direction from the light falls in, in the light's own axes, in the order the
// shadow gem draws them: +Z, the way it shines, then +X, -X, +Y, -Y and -Z.
uint GetFace(float3 local)
{
    float3 size = abs(local);
    if (size.z >= size.x && size.z >= size.y)
        return local.z >= 0.0 ? 0u : 5u;

    if (size.x >= size.y)
        return local.x >= 0.0 ? 1u : 2u;

    return local.y >= 0.0 ? 3u : 4u;
}

// How much of light index's light reaches a point relative to the camera of the frame, whose camera is at
// cameraPosition, of a surface facing normal; noise turns the filter's taps.
public float GetLocalShadow(LocalShadowMaps maps, uint index, GpuLight light, float3 relative, float3 cameraPosition,
    float3 normal, float noise)
{
    if (maps.lights == 0u)
        return 1.0;

    ArrayRef<GpuLightShadow> shadows = { maps.lights };
    GpuLightShadow shadow = shadows[index];
    if (shadow.firstTile == NoShadowTile)
        return 1.0;

    float3 position = relative + cameraPosition - maps.anchor;
    float3 fromLight = position - (light.position - maps.anchor);
    uint face = 0u;
    if (shadow.faceCount > 1u)
    {
        float3x3 basis = GetLightBasis(light);
        face = GetFace(mul(basis, fromLight));
        if (face >= shadow.faceCount)
            return 1.0;
    }

    ArrayRef<GpuShadowTile> tiles = { maps.tiles };
    GpuShadowTile tile = tiles[shadow.firstTile + face];
    if (tile.rect.z == 0u)
        return 1.0;

    // A metre from the light, a texel covers 2 / (focal length * size); the near plane is the projection's.
    float focal = length(float3(tile.viewProjection[0][0], tile.viewProjection[1][0], tile.viewProjection[2][0]));
    float texelAtMetre = 2.0 / max(focal * float(tile.rect.z), Epsilon);
    float near = tile.viewProjection[3][2];
    float3 toLight = NormalizeSafe(-fromLight, normal);
    float glancing = 1.0 - saturate(dot(normal, toLight));
    float viewDepth = mul(float4(position, 1.0), tile.viewProjection).w;
    float bias = max(maps.normalBias * glancing, LocalMinNormalBias) * texelAtMetre * viewDepth;
    float4 clip = mul(float4(position + normal * bias, 1.0), tile.viewProjection);
    if (clip.w <= near)
        return 1.0;

    float3 ndc = clip.xyz / clip.w;
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    if (any(uv < 0.0) || any(uv > 1.0))
        return 1.0;

    float2 texel = float2(tile.rect.xy) + uv * float2(tile.rect.zw);
    float receiverDepth = clip.w;
    float texelHere = texelAtMetre * receiverDepth;
    float widest = max(maps.maxPenumbra, 1.0);
    ShadowMaps atlas = { maps.atlas, maps.sampler, maps.minMips, maps.maxMips, maps.mipLevels };
    float rotation = noise * TwoPi;

    // The casters can be anywhere in the cone from the point to the light's source: widest at the near plane.
    float search = clamp(
        0.5 * shadow.lightWidth * (receiverDepth - near) / max(receiverDepth * texelAtMetre * near, Epsilon),
        1.0,
        widest
    );
    Blockers blockers = FindBlockers(atlas, texel, ndc.z, search, tile.rect, rotation, maps.blockerTaps);
    if (blockers.count == 0.0)
        return 1.0;

    float blockerDepth = near / max(blockers.depth, Epsilon);
    float penumbra = max(receiverDepth - blockerDepth, 0.0) * shadow.lightWidth / max(blockerDepth, Epsilon);
    float radius = clamp(0.5 * penumbra / max(texelHere, Epsilon), 1.0, widest);
    return FilterShadow(atlas, texel, ndc.z, radius, tile.rect, rotation, maps.filterTaps);
}
