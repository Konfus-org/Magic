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

// The most that a receiver's reversed depth changes over a texel of its tile, as a share of the depth: a surface
// almost edge-on to the light keeps this slope, so its plane does not run off past every caster.
static const float SteepestSlope = 0.05;

// The change of a receiver's reversed depth per texel of its tile across the receiver's plane: its point and two
// points a texel away along the plane, each put into the tile, give the depth's change along the two texel axes.
float2 GetReceiverSlope(GpuShadowTile tile, float3 point, float3 normal, float2 texel, float depth, float step)
{
    float3 hint = abs(normal.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 across = normalize(cross(normal, hint));
    float3 along = cross(normal, across);
    float3 a = ToTileTexel(tile, point + across * step);
    float3 b = ToTileTexel(tile, point + along * step);
    float2x2 moved = float2x2(a.xy - texel, b.xy - texel);
    float det = determinant(moved);
    if (abs(det) < 1e-6)
        return float2(0.0);

    // The slope that gives each point's change of depth from its change of texels.
    float2 change = float2(a.z - depth, b.z - depth);
    float2x2 inverse = float2x2(moved[1][1], -moved[0][1], -moved[1][0], moved[0][0]) / det;
    float steepest = SteepestSlope * abs(depth);
    return clamp(mul(inverse, change), -steepest, steepest);
}

// A point, relative to the lights' anchor, as a texel of a tile of the atlas and its reversed depth.
float3 ToTileTexel(GpuShadowTile tile, float3 point)
{
    float4 clip = mul(float4(point, 1.0), tile.viewProjection);
    float3 ndc = clip.xyz / max(clip.w, Epsilon);
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    return float3(float2(tile.rect.xy) + uv * float2(tile.rect.zw), ndc.z);
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
    float3x3 basis = GetLightBasis(light);
    uint face = shadow.faceCount > 1u ? GetFace(mul(basis, fromLight)) : 0u;
    if (face >= shadow.faceCount)
        return 1.0;

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
    // The face is the one that the moved point lies in: near an edge of the cube, the move can take the point over
    // into the next face, whose tile holds what is in front of it.
    bool isCube = shadow.faceCount > 1u;
    if (isCube)
    {
        uint moved = GetFace(mul(basis, fromLight + normal * bias));
        if (moved != face)
        {
            if (moved >= shadow.faceCount)
                return 1.0;

            face = moved;
            tile = tiles[shadow.firstTile + face];
            if (tile.rect.z == 0u)
                return 1.0;

            focal = length(float3(tile.viewProjection[0][0], tile.viewProjection[1][0], tile.viewProjection[2][0]));
            texelAtMetre = 2.0 / max(focal * float(tile.rect.z), Epsilon);
            near = tile.viewProjection[3][2];
        }
    }

    float4 clip = mul(float4(position + normal * bias, 1.0), tile.viewProjection);
    if (clip.w <= near)
        return 1.0;

    float3 ndc = clip.xyz / clip.w;
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    // A spot light's tile ends where its light does. A point in a cube face is in its tile, past the rounding of a
    // point on the edge between two faces.
    if (!isCube && (any(uv < 0.0) || any(uv > 1.0)))
        return 1.0;

    uv = saturate(uv);

    float2 texel = float2(tile.rect.xy) + uv * float2(tile.rect.zw);
    float receiverDepth = clip.w;
    float2 slope = GetReceiverSlope(tile, position + normal * bias, normal, texel, ndc.z, texelAtMetre * clip.w);
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
    Blockers blockers = FindBlockers(atlas, texel, ndc.z, slope, search, tile.rect, rotation, maps.blockerTaps);
    if (blockers.count == 0.0)
        return 1.0;

    float blockerDepth = near / max(blockers.depth, Epsilon);
    float penumbra = max(receiverDepth - blockerDepth, 0.0) * shadow.lightWidth / max(blockerDepth, Epsilon);
    float radius = clamp(0.5 * penumbra / max(texelHere, Epsilon), 1.0, widest);
    return FilterShadow(atlas, texel, ndc.z, slope, radius, tile.rect, rotation, maps.filterTaps);
}
