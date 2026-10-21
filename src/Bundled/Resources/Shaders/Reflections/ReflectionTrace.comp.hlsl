// Reflections, traced once a pixel of the view for a surface smooth enough to show them: a ray drawn from its GGX lobe
// (importance sampled as Karis 2013 does, so a rough surface's rays spread as its highlight does, a new way each frame),
// first across the view, where what it meets was drawn: shown as lit there, exact and as small as it is, from the view
// the frame before kept for it (Reflections/ReflectionScene.frag.hlsl), as this frame's is not lit yet. Where the view
// does not show it, off its edge, behind what was drawn, or a surface's far side, the ray goes through global
// illumination's field instead, where the world draws it, and what it hits is lit as the field lights it
// (Lighting/Gi.hlsli's GiHitRadiance): what it gives off, the sun and the lights near it through the field's own
// shadows, and the probes' light from everywhere, only as coarsely as the field holds it; past everything, the sky, as
// blurred as the surface's roughness blurs it. The two are blended toward the view's edges, so a reflection does not
// jump as what it shows leaves the view. A surface rougher than `rough` draws two rays and takes their mean, as its lobe
// is wide. Written in nits with how much of it the lighting takes in place of the sky alone: all for a smooth surface,
// none past roughest, where the sky's blur is as good; and, apart (ReflectionDistance), how far the first ray went to
// what it reflects, which tells where the reflection's image stands behind the surface.
// Reflections/ReflectionResolve.comp.hlsl filters what the rays find in space and time.

#include "Include/Brdf.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Packing.hlsli"
#include "Lighting/Ibl.hlsli"
#if defined(HAS_GiLevels) && defined(HAS_GiField) && defined(HAS_GiSurface) && defined(HAS_GiEmission) && defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLights) && defined(HAS_WorldLights)
#define TRACE_FIELD 1
#include "Lighting/Gi.hlsli"
#endif
#if defined(HAS_ReflectionSceneHistory) && defined(HAS_Velocity)
#define TRACE_SCREEN 1
#endif

struct PassParams
{
    float roughest = 0.6;    // roughness past which nothing is traced
    float fade = 0.2;        // roughness below roughest over which the traced gives way to the sky's blur
    float rough = 0.25;      // roughness past which two rays are traced
    float stride = 4.0;      // pixels a ray steps across the view, so what is a few pixels wide is not stepped over
    float steps = 96.0;      // the most steps a ray takes across the view, a longer way stepping further
    float thickness = 0.25;  // metres a surface the view drew is taken to be thick, a hundredth of its distance more
};

// Metres a reflection of the sky is taken to be from the surface: far enough that its image moves as the sky does.
static const float SkyDistance = 10000.0;

// The share of the view, in from each edge, over which what a ray found in the view gives way to the field's.
static const float ScreenEdge = 0.1;

#if defined(TRACE_SCREEN)
// How far a ray's point at s of its way across the view (from, span, in pixels; nearFrom and nearTo the reciprocals of
// its ends' view depths) is behind what the view drew there, in metres; below zero in front of it, or where nothing was.
float BehindDrawn(float2 from, float2 span, float nearFrom, float nearTo, float s)
{
    float drawn = Depth.SampleLevel(DepthSampler, (from + span * s) * ViewTexel, 0.0);
    return drawn > 0.0 ? 1.0 / lerp(nearFrom, nearTo, s) - ViewDepth(drawn) : -1.0;
}

// What one ray reflects of what the view drew, in nits, how sure that is, from none where the view does not show it to
// all, and how far it went: marched from seen (the surface, in view space) across the view, evenly in its pixels as far
// as its edge, jittered per pixel and frame, against this frame's depth (McGuire & Mara 2014's screen-space march,
// linear rather than hierarchical, as the depth pyramid keeps only the farthest of its depths). Its colour is the view
// the frame before, where the hit was then by its velocity, so what moves is read where it was drawn, blurrier the
// rougher the surface. And clear, the metres of the ray the view showed empty, in front of all it drew, which the
// field goes on from where the view cannot say: what the ray meets is then where the view would have it.
float3 TraceScreen(float2 pixel, float3 seen, float3 normal, float3 direction, float roughness, uint ray, PassParams params, out float sure, out float distance, out float clear)
{
    sure = 0.0;
    distance = 0.0;
    clear = 0.0;

    // From just off the surface, so its own depth does not stop the ray, as far as the view reaches: short of the
    // camera's near plane for a ray turned toward it.
    float3 along = WorldToView(direction);
    float3 start = seen + WorldToView(normal) * max(0.01, 0.002 * seen.z);
    float reach = along.z < 0.0 ? (start.z - 1.5 * Near) / -along.z : SkyDistance;
    float3 end = start + along * max(reach, 0.0);
    float2 from = ViewPixel(start);
    float2 span = ViewPixel(end) - from;
    float2 bound = select(span > 0.0, ViewSize, float2(0.0, 0.0));
    float2 leaves = select(abs(span) > 1e-4, (bound - from) / span, float2(1e9, 1e9));
    float last = saturate(min(leaves.x, leaves.y));
    float pixels = length(span) * last;
    [branch] if (pixels < 1.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    // A view-space depth along the ray is not even in pixels, its reciprocal is. The first step behind what was drawn
    // ends the march, its crossing halved in on: just behind a surface there, by less than its thickness, is a hit,
    // however narrow the surface on the view; far behind is the ray gone behind something, whose far side the view
    // never drew, and the field, which holds it, takes the ray on from the last step in front.
    float nearFrom = 1.0 / start.z;
    float nearTo = 1.0 / end.z;
    uint steps = clamp((uint)ceil(pixels / max(params.stride, 1.0)), 1u, max((uint)params.steps, 1u));
    float jitter = InterleavedGradientNoise(pixel + float2(71.0, 13.0) * (float)ray, FrameIndex);
    float before = 0.0;
    float at = -1.0;
    bool isBehind = false;
    [loop] for (uint i = 0u; i < steps && !isBehind; i++)
    {
        float s = last * ((float)i + jitter) / (float)steps;
        isBehind = BehindDrawn(from, span, nearFrom, nearTo, s) > 0.0;
        [branch] if (isBehind)
        {
            float front = before;
            float back = s;
            [unroll] for (uint halving = 0u; halving < 4u; halving++)
            {
                float middle = 0.5 * (front + back);
                bool isPast = BehindDrawn(from, span, nearFrom, nearTo, middle) > 0.0;
                front = isPast ? front : middle;
                back = isPast ? middle : back;
            }

            float z = 1.0 / lerp(nearFrom, nearTo, back);
            at = BehindDrawn(from, span, nearFrom, nearTo, back) < params.thickness + 0.01 * z ? back : at;
            before = front;
        }
        else
        {
            before = s;
        }
    }

    // The way up to the last step in front was clear, a stride at a time, so what the view drew narrower than a stride
    // may be stepped over, by this ray and the field's after it.
    float beforeDepth = 1.0 / lerp(nearFrom, nearTo, before);
    clear = abs(along.z) > 1e-4 ? (beforeDepth - start.z) / along.z : max(reach, 0.0) * before;
    [branch] if (at < 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    // Sure where the hit is in from the view's edges, on the side of its surface the ray comes from, and was in the
    // view the frame before.
    float2 hit = from + span * at;
    int3 texel = int3(min(uint2(hit), (uint2)ViewSize - 1u), 0);
    float hitDepth = Depth.Load(texel);
    float3 facing = OctDecode(Normal.Load(texel).xy * 2.0 - 1.0);
    float2 uv = hit * ViewTexel;
    float2 then = uv - Velocity.Load(texel).xy;
    float2 edges = min(uv, 1.0 - uv);
    sure = saturate(min(edges.x, edges.y) / ScreenEdge) * saturate(-dot(facing, direction) * 8.0);
    sure *= all(then >= 0.0) && all(then <= 1.0) && hitDepth > 0.0 ? 1.0 : 0.0;
    float4 shown = ReflectionSceneHistory.SampleLevel(ReflectionSceneHistorySampler, then, roughness * 4.0);
    sure *= shown.a;
    distance = length(ViewPosition(hit, ViewDepth(max(hitDepth, 1e-12))) - seen);
    return shown.rgb;
}
#endif

// What one ray reflects through global illumination's field, in nits, and how far from the surface what it reflects
// is; sky past everything.
float3 TraceField(float3 position, float3 normal, float3 direction, float3 sky, float clear, out float distance)
{
    distance = SkyDistance;
#if defined(TRACE_FIELD)
    float3 sh[9];
    [unroll] for (uint i = 0u; i < 9u; i++)
    {
        sh[i] = LOAD(SkySh, float4, i).rgb;
    }

    GpuGi header = GiHeader();
    float3 surface = position + CameraPosition;
    float maxDistance = GiLevel(header.levels - 1u).voxel * (float)GiResolution * 0.5;
    uint first = 0u;
    [loop] while (first + 1u < header.levels && !GiContains(GiLevel(first), surface, 1.0))
    {
        first++;
    }

    // Where the view showed the ray's way empty (clear metres of it) up to something it went behind, the ray goes on
    // from there, so what it meets is where the view would have it. Setting out within a copy, it is leaving it where
    // it heads out of it, the copy of its own surface near which it set out; heading in, the copy is what it meets.
    GpuGiLevel level = GiLevel(first);
    [branch] if (clear > 0.0)
    {
        float3 onward = surface + direction * clear;
        uint index = 0u;
        [loop] while (index + 1u < header.levels && !GiContains(GiLevel(index), onward, 1.0))
        {
            index++;
        }

        bool isLeaving = dot(GiNormalAt(GiLevel(index), index, onward), direction) > 0.0;
        GiHit onwardHit = GiTrace(onward, direction, index, header.levels, maxDistance - clear, 0.0, isLeaving);
        [branch] if (onwardHit.isHit)
        {
            distance = length(onwardHit.position - surface);
            return GiHitRadiance(onwardHit, header.levels, sh);
        }

        return sky;
    }

    // Else from the finest level that holds the surface, far enough off it that the field there, a little fatter than
    // what it holds and coarser the farther from the camera, does not stop the ray where it starts: a far surface's ray
    // started nearer would set out inside its own coarse copy and see that. A surface within another's fattened band,
    // the foot of a wall within the floor's, would start inside the field even so: its start is moved out along the
    // field's gradient first. And the ray sets out a voxel along itself, past the field's rounding of its own
    // surface's corners, which a ray leaving a corner at a grazing angle would otherwise meet. Where something is in
    // the way even so, the light the probes have arriving from there.
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

    GiHit hit = GiTrace(origin + direction * level.voxel, direction, first, header.levels, maxDistance, 0.0, false);
    [branch] if (hit.isHit && !hit.isInside)
    {
        distance = length(hit.position - surface);
        return GiHitRadiance(hit, header.levels, sh);
    }

    [branch] if (hit.isInside)
    {
        distance = 1.5 * level.voxel;
        float held;
        float3 arriving = GiIrradianceAt(origin, direction, direction, held);
        return lerp(sky, arriving, held);
    }
#endif

    return sky;
}

// What one ray from the lobe reflects, in nits, and how far from the surface what it reflects is: ray tells the rays of
// a pixel apart, and sky is what the surface reflects of the sky past everything. The field is traced only where the
// view does not wholly show what the ray meets.
float3 TraceReflection(float2 pixel, float3 position, float3 normal, float3 toEye, float roughness, uint ray, float3 sky, PassParams params, out float distance)
{
    float2 apart = float2(23.0, 47.0) * (float)ray;

    // A ray from the lobe: the half vector drawn by GGX, reflected about; one that would go under the surface is the
    // mirror's instead.
    float2 random = float2(InterleavedGradientNoise(pixel + apart, FrameIndex), InterleavedGradientNoise(pixel + apart + float2(37.0, 11.0), FrameIndex));
    float3 halfway = TangentToWorld(ImportanceSampleGgx(random, RoughnessToAlpha(roughness)), normal);
    float3 direction = reflect(-toEye, halfway);
    direction = dot(direction, normal) > 0.0 ? direction : reflect(-toEye, normal);

    float sure = 0.0;
    float3 shown = float3(0.0, 0.0, 0.0);
    float shownDistance = 0.0;
    float clear = 0.0;
#if defined(TRACE_SCREEN)
    shown = TraceScreen(pixel, WorldToView(position), normal, direction, roughness, ray, params, sure, shownDistance, clear);
    [branch] if (sure >= 1.0)
    {
        distance = shownDistance;
        return shown;
    }
#endif

    float3 field = TraceField(position, normal, direction, sky, clear, distance);
    distance = sure > 0.5 ? shownDistance : distance;
    return lerp(field, shown, sure);
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
        ReflectionDistance[id.xy] = 0.0;
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

    float distance;
    float3 reflected = TraceReflection(pixel, position, normal, toEye, roughness, 0u, sky, params, distance);
    [branch] if (roughness > params.rough)
    {
        float second;
        reflected = 0.5 * (reflected + TraceReflection(pixel, position, normal, toEye, roughness, 1u, sky, params, second));
    }

    ReflectionTraced[id.xy] = float4(reflected, share);
    ReflectionDistance[id.xy] = distance;
}
