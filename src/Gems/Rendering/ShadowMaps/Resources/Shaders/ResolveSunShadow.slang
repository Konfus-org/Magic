// How much of the sun reaches each pixel, from the depth of the sun's cascades, into the sun's slot of the shadow mask.
// The pixel's point moves along its normal by a part of its cascade's texel before the test (Holbert, "Normal Offset
// Shadows", 2010): more where the light is glancing. It takes the first cascade whose rectangle holds it, or, in a band
// at that rectangle's edge, the next one for a share of the pixels that grows to the edge, picked by noise that
// changes each frame. The shadow is contact-hardening (Light/ShadowFilter.slang): the penumbra is as wide as the sun's
// disc seen from the point over the distance to its casters. A short march through the depth buffer towards the sun
// then closes the gap that the map leaves at contact (Epic Games, "Contact Shadows", Unreal Engine documentation), and
// multiplies the map's result. The mask's other slots are lit. The details texture keeps the penumbra, the cascade and
// the contact shadow of each pixel for the debug views.
module "ResolveSunShadow";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Scene.Lights;
import Scene.Shadows;
import Scene.ShadowViews;
import Light.ShadowFilter;

// What the resolve reads beyond the frame, C#'s SunShadowConstants, 64 bytes, the buffer the pass header's y names.
struct SunShadowConstants
{
    // The heap ids of the cascades, how many there are, of their atlas, its sampler and its min and max mips, how many
    // levels those have, and of the mask and the details written.
    uint views;
    uint viewCount;
    uint atlas;
    uint sampler;
    uint minMips;
    uint maxMips;
    uint mipLevels;
    uint mask;
    uint details;
    // How far the point moves along its normal, in texels of its cascade, at a glancing light.
    float normalBias;
    // The share of a cascade's side, at its edge, over which it blends into the next.
    float cascadeBlend;
    // The taps of the blocker search and of the filter, and the widest penumbra in texels.
    uint blockerTaps;
    uint filterTaps;
    float maxPenumbra;
    // The contact shadow's march: its metres, its steps and the metres behind the depth buffer that a step still hits.
    // No steps is no contact shadow.
    float contactLength;
    uint contactSteps;
    float contactThickness;
}

// The least the point moves along its normal, in texels of its cascade, where the light faces the surface.
static const float MinNormalBias = 0.5;

// The metres along its normal that the contact shadow's march starts from the point.
static const float ContactStartOffset = 0.01;

// The share of the screen at its edges over which the contact shadow fades out, and the metres from the camera at
// which it is gone.
static const float ContactEdgeFade = 0.05;
static const float ContactFarthest = 50.0;

// How the sun reaches a pixel: its share of the light, and how the shadow map found it.
struct SunShadow
{
    float lit;
    float penumbra;
    float cascade;
    float blend;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveSunShadow(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    if (any(thread.xy >= uint2(frame.width, frame.height)))
        return;

    SunShadowConstants constants = LoadConstants<SunShadowConstants>(GetPassHeader().y);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(thread.xy, 0u).x;
    SunShadow sun = { 1.0, 0.0, 0.0, 0.0 };
    float contact = 1.0;
    if (depth != 0.0)
    {
        // The point lifted off its flat triangle towards the curve its vertex normals describe, as the resolve of the
        // GBuffer found it, so that a curve is not shadowed a facet at a time.
        TextureRef surfaceTexture = { frame.gbufferSurface };
        float3 relative = ReconstructPixel(frame, thread.xy, depth);
        float3 normal = LoadNormal(frame.gbufferNormal, thread.xy);
        relative += normal * DecodeLift(frame, relative, surfaceTexture.Load(thread.xy, 0u).w);
        float3 toSun = GetLights().sunDirection.xyz;
        float noise = GetInterleavedGradientNoise(thread.xy, frame.frameIndex);
        sun = GetSunShadow(constants, relative, normal, toSun, noise);
        if (sun.lit > 0.0 && constants.contactSteps != 0u)
            contact = GetContactShadow(frame, constants, thread.xy, relative, normal, toSun, noise);
    }

    // Slot 0 is the sun's, and every other slot is lit.
    uint4 words = EncodeShadow(uint4(0xFFFFFF00u, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu), 0u, sun.lit * contact);
    StoreShadowMask(constants.mask, thread.xy, frame.width, words);
    StorageRef details = { constants.details, 0u };
    details.Store(
        thread.xy,
        float4(saturate(sun.penumbra / DetailsWidestPenumbra), sun.cascade / 4.0, contact, sun.blend)
    );
}

// Where a point lands in a cascade, in texels of the atlas, and how far it is inside the cascade's rectangle, in
// texels, negative outside.
struct CascadeHit
{
    float3 texel;
    float inside;
}

CascadeHit GetCascadeHit(GpuShadowView view, float3 relative, float3 normal, float glancing, float normalBias)
{
    float bias = max(normalBias * glancing, MinNormalBias) * view.texelSize;
    CascadeHit hit;
    hit.texel = GetShadowTexel(view, relative + normal * bias);
    float2 inRect = hit.texel.xy - float2(view.rect.xy);
    float2 toEdge = min(inRect, float2(view.rect.zw) - inRect);
    hit.inside = min(toEdge.x, toEdge.y);
    return hit;
}

// How much of the sun reaches the point: 1 past the last cascade.
SunShadow GetSunShadow(SunShadowConstants constants, float3 relative, float3 normal, float3 toSun, float noise)
{
    SunShadow result = { 1.0, 0.0, 0.0, 0.0 };
    float glancing = 1.0 - saturate(dot(normal, toSun));
    for (uint i = 0u; i < constants.viewCount; i++)
    {
        GpuShadowView view = LoadShadowView(constants.views, i);
        CascadeHit hit = GetCascadeHit(view, relative, normal, glancing, constants.normalBias);
        if (hit.inside < 1.0)
            continue;

        // In the band at the edge, the next cascade takes a share of the pixels that grows to the edge.
        uint chosen = i;
        float band = constants.cascadeBlend * float(view.rect.z);
        float blend = saturate(1.0 - (hit.inside - 1.0) / max(band, 1.0));
        if (i + 1u < constants.viewCount && noise < blend)
        {
            GpuShadowView next = LoadShadowView(constants.views, i + 1u);
            CascadeHit nextHit = GetCascadeHit(next, relative, normal, glancing, constants.normalBias);
            if (nextHit.inside >= 1.0)
            {
                view = next;
                hit = nextHit;
                chosen = i + 1u;
            }
        }

        result = FilterSunShadow(constants, view, hit.texel, noise);
        result.cascade = float(chosen + 1u);
        result.blend = i + 1u < constants.viewCount ? blend : 0.0;
        return result;
    }

    return result;
}

// The contact-hardening filter of the sun at a texel of a cascade: a search for the casters in front of it as far as
// the widest penumbra reaches, then a filter as wide as the sun's disc seen from the point over its distance to them.
SunShadow FilterSunShadow(SunShadowConstants constants, GpuShadowView view, float3 texel, float noise)
{
    ShadowMaps maps = { constants.atlas, constants.sampler, constants.minMips, constants.maxMips, constants.mipLevels };
    float rotation = noise * TwoPi;
    float widest = max(constants.maxPenumbra, 1.0);
    SunShadow result = { 1.0, 0.0, 0.0, 0.0 };
    Blockers blockers = FindBlockers(maps, texel.xy, texel.z, float2(0.0), widest, view.rect, rotation, constants.blockerTaps);
    if (blockers.count == 0.0)
        return result;

    // The metres from the point to its casters along the light, and the half width of the penumbra there: the sun's
    // angular radius across that distance, in texels of the cascade.
    float reach = (blockers.depth - texel.z) * view.depthRange;
    float radius = clamp(reach * tan(SunAngularRadius) / view.texelSize, 1.0, widest);
    result.lit = FilterShadow(maps, texel.xy, texel.z, float2(0.0), radius, view.rect, rotation, constants.filterTaps);
    result.penumbra = radius;
    return result;
}

// Whether the depth buffer hides the sun from the point within a short march towards it: 0 where a step lands behind
// a surface by less than the thickness, else 1, fading to 1 at the screen's edges and far from the camera.
float GetContactShadow(FrameConstants frame, SunShadowConstants constants, uint2 pixel, float3 relative,
    float3 normal, float3 toSun, float noise)
{
    float2 uv = GetTexelCenterUv(pixel, uint2(frame.width, frame.height));
    float2 toEdge = min(uv, 1.0 - uv);
    float fade = max(
        saturate(1.0 - min(toEdge.x, toEdge.y) / ContactEdgeFade),
        saturate((length(relative) - ContactFarthest * 0.9) / (ContactFarthest * 0.1))
    );
    if (fade >= 1.0)
        return 1.0;

    TextureRef depthTexture = { frame.depth };
    uint2 size = uint2(frame.width, frame.height);
    float3 start = relative + normal * ContactStartOffset;
    float stepLength = constants.contactLength / float(constants.contactSteps);
    for (uint i = 0u; i < constants.contactSteps; i++)
    {
        float3 position = start + toSun * ((float(i) + noise) * stepLength);
        float4 clip = mul(float4(position, 1.0), frame.viewProjection);
        if (clip.w <= frame.near)
            break;

        float2 stepUv = ToUv(clip.xy / clip.w);
        if (!IsOnScreen(stepUv))
            break;

        float surface = ToViewDepth(depthTexture.Load(GetTexelAt(stepUv, size), 0u).x, frame.near);
        float behind = clip.w - surface;
        if (behind > 0.0 && behind < constants.contactThickness)
            return fade;
    }

    return 1.0;
}
