// Reflections, traced once a pixel of the view for a surface smooth enough to show them: a ray drawn from its GGX lobe
// (importance sampled as Karis 2013 does, so a rough surface's rays spread as its highlight does, a new way each frame)
// through global illumination's field, where the world draws it, and what it hits lit as the field lights it
// (Lighting/Gi.hlsli's GiHitRadiance): what it gives off, the sun and the lights near it through the field's own
// shadows, and the probes' light from everywhere; past everything, the sky, as blurred as the surface's roughness blurs
// it. Nothing is read from the view itself, so a reflection shows the same whatever is on screen, what is behind the
// camera or hidden behind something as much as what is in sight, only as coarsely as the field holds it. A surface
// rougher than `rough` draws two rays and takes their mean, as its lobe is wide. Written in nits with how much of it
// the lighting takes in place of the sky alone: all for a smooth surface, none past roughest, where the sky's blur is
// as good. Reflections/ReflectionResolve.comp.hlsl filters what the rays find in space and time.

#include "Include/Brdf.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Packing.hlsli"
#include "Lighting/Ibl.hlsli"
#if defined(HAS_GiLevels) && defined(HAS_GiField) && defined(HAS_GiSurface) && defined(HAS_GiEmission) && defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLights) && defined(HAS_WorldLights)
#define TRACE_FIELD 1
#include "Lighting/Gi.hlsli"
#endif

struct PassParams
{
    float roughest = 0.6;    // roughness past which nothing is traced
    float fade = 0.2;        // roughness below roughest over which the traced gives way to the sky's blur
    float rough = 0.25;      // roughness past which two rays are traced
};

// What one ray from the lobe reflects, in nits: ray tells the rays of a pixel apart, and sky is what the surface
// reflects of the sky past everything.
float3 TraceRay(float2 pixel, float3 position, float3 normal, float3 toEye, float roughness, uint ray, float3 sky)
{
    float2 apart = float2(23.0, 47.0) * (float)ray;

    // A ray from the lobe: the half vector drawn by GGX, reflected about; one that would go under the surface is the
    // mirror's instead.
    float2 random = float2(InterleavedGradientNoise(pixel + apart, FrameIndex), InterleavedGradientNoise(pixel + apart + float2(37.0, 11.0), FrameIndex));
    float3 halfway = TangentToWorld(ImportanceSampleGgx(random, RoughnessToAlpha(roughness)), normal);
    float3 direction = reflect(-toEye, halfway);
    direction = dot(direction, normal) > 0.0 ? direction : reflect(-toEye, normal);

#if defined(TRACE_FIELD)
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    // From the finest level that holds the surface, far enough off it that the field there, a little fatter than what
    // it holds and coarser the farther from the camera, does not stop the ray where it starts: a far surface's ray
    // started nearer would set out inside its own coarse copy and see that. A surface within another's fattened band,
    // the foot of a wall within the floor's, would start inside the field even so: its start is moved out along the
    // field's gradient first. And the ray sets out a voxel along itself, past the field's rounding of its own
    // surface's corners, which a ray leaving a corner at a grazing angle would otherwise meet. Where something is in
    // the way even so, the light the probes have arriving from there.
    GpuGi header = GiHeader();
    float3 surface = position + CameraPosition;
    uint first = 0u;
    [loop] while (first + 1u < header.levels && !GiContains(GiLevel(first), surface, 1.0))
    {
        first++;
    }

    GpuGiLevel level = GiLevel(first);
    float3 origin = surface + normal * (1.5 * level.voxel);
    [loop] for (uint push = 0u; push < 4u; push++)
    {
        float inside = GiFieldAt(level, first, origin);
        if (inside >= 0.3 * level.voxel)
        {
            break;
        }

        origin += GiNormalAt(level, first, origin) * (0.3 * level.voxel - inside);
    }

    GiHit hit = GiTrace(origin + direction * level.voxel, direction, first, header.levels, GiLevel(header.levels - 1u).voxel * (float)GiResolution * 0.5);
    [branch] if (hit.isHit && !hit.isInside)
    {
        return GiHitRadiance(hit, header.levels, sh);
    }

    [branch] if (hit.isInside)
    {
        float held;
        float3 arriving = GiIrradianceAt(origin, direction, direction, held);
        return lerp(sky, arriving, held);
    }
#endif

    return sky;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= (uint2)ViewSize))
    {
        return;
    }

    PassParams params = LoadPassParams();
    float2 pixel = float2(id.xy) + 0.5;
    float depth = Depth.SampleLevel(DepthSampler, pixel * ViewTexel, 0.0);
    float4 stored = Normal.SampleLevel(NormalSampler, pixel * ViewTexel, 0.0);
    float roughness = stored.z;
    float share = 1.0 - smoothstep(params.roughest - params.fade, params.roughest, roughness);
    [branch] if (depth <= 0.0 || share <= 0.0)
    {
        ReflectionTraced[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float3 position = ViewToWorld(ViewPosition(pixel, ViewDepth(depth)));
    float3 normal = OctDecode(stored.xy * 2.0 - 1.0);
    float3 toEye = -normalize(position);

    // The sky as the surface's roughness blurs it, along where its reflection mostly comes from.
    float width;
    float height;
    float levels;
    SkySpecular.GetDimensions(0u, width, height, levels);
    float3 dominant = SpecularDominantDirection(normal, reflect(-toEye, normal), roughness);
    float3 sky = SkySpecular.SampleLevel(SkySpecularSampler, dominant, PrefilteredLevel(roughness, levels)).rgb;

    float3 reflected = TraceRay(pixel, position, normal, toEye, roughness, 0u, sky);
    [branch] if (roughness > params.rough)
    {
        reflected = 0.5 * (reflected + TraceRay(pixel, position, normal, toEye, roughness, 1u, sky));
    }

    ReflectionTraced[id.xy] = float4(reflected, share);
}
