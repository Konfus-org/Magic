// Reflections, traced once a pixel of the view for a surface smooth enough to show them: a ray drawn from its GGX lobe
// (importance sampled as Karis 2013 does, so a rough surface's rays spread as its highlight does, a new way each frame),
// marched through the view's depth for what the view shows (screen-space reflections, McGuire & Mara 2014, "Efficient
// GPU Screen-Space Ray Tracing"), lit as the last frame lit it there, found by what was hit's velocity; where that finds
// nothing, through global illumination's field, where the world draws it, so what is off the view or behind something
// still shows; and past everything, the sky, as blurred as the surface's roughness blurs it. What the field hits is lit
// as the view lit it when the view shows it, so the two agree, and only what the view does not show is lit as the
// field lights it (Lighting/Gi.hlsli's GiHitRadiance), coarser, by its probes, the sun and the lights near it. Each hit is
// trusted by degrees, by how near the view's edges it is and how squarely the ray meets it, never all or nothing, so a
// pixel slides from one to the other as the view moves rather than jumping. Written in nits with how much of it the
// lighting takes in place of the sky alone: all for a smooth surface, none past roughest, where the sky's blur is as
// good. The march is jittered by pixel and frame, and Reflections/ReflectionResolve.comp.hlsl filters what it finds in
// space and time.

#include "Include/Brdf.hlsli"
#include "Include/Noise.hlsli"
#include "Include/Packing.hlsli"
#include "Lighting/Ibl.hlsli"
#if defined(HAS_GiLevels) && defined(HAS_GiField) && defined(HAS_GiSurface) && defined(HAS_GiEmission) && defined(HAS_GiIrradiance) && defined(HAS_GiDistance) && defined(HAS_GiLights) && defined(HAS_SceneLights)
#define TRACE_FIELD 1
#include "Lighting/Gi.hlsli"
#endif

struct PassParams
{
    float roughest = 0.6;    // roughness past which nothing is traced
    float fade = 0.2;        // roughness below roughest over which the traced gives way to the sky's blur
    float length = 80.0;     // metres the march through the view's depth goes at most
    float steps = 32.0;      // of the march, denser near its start
    float thickness = 0.3;   // metres what the depth shows is taken to be thick, and a share of its depth more
};

// How far behind what the view drew at a point's pixel the point is, in metres along the view: below 0 in front of it,
// and very far behind where nothing was drawn.
float BehindDrawn(float3 seen, float2 at)
{
    float depth = Depth.SampleLevel(DepthSampler, at * ViewTexel, 0.0);
    return depth > 0.0 ? seen.z - ViewDepth(depth) : -1e6;
}

// How far inside the view a point of it is: 0 at an edge or past it, rising to 1 a tenth of the view in.
float InsideView(float2 at)
{
    float2 edge = min(at, ViewSize - at) / (0.1 * ViewSize);
    return saturate(min(edge.x, edge.y));
}

// What the view showed at a pixel last frame, lit, back in nits, and how far to trust it as what a ray going along ray
// sees there: less near the view's edges, this frame's or the last's, where it may soon leave or has only just come,
// and the more the surface there turns from the ray, none for its back.
float LitThen(float2 at, float3 ray, out float3 lit)
{
    float2 then = at * ViewTexel - Velocity.Load(int3(at, 0)).xy;
    float3 facing = OctDecode(Normal.Load(int3(at, 0)).xy * 2.0 - 1.0);
    lit = ReflectionSceneHistory.SampleLevel(ReflectionSceneHistorySampler, then, 0.0).rgb / max(LOAD(Exposure, float4, 0u).x, 1e-12);
    return InsideView(at) * InsideView(then * ViewSize) * saturate(-4.0 * dot(facing, ray));
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

    float viewDepth = ViewDepth(depth);
    float3 seen = ViewPosition(pixel, viewDepth);
    float3 position = ViewToWorld(seen);
    float3 normal = OctDecode(stored.xy * 2.0 - 1.0);
    float3 toEye = -normalize(position);

    // A ray from the lobe: the half vector drawn by GGX, reflected about; one that would go under the surface is the
    // mirror's instead.
    float2 random = float2(InterleavedGradientNoise(pixel, FrameIndex), InterleavedGradientNoise(pixel + float2(37.0, 11.0), FrameIndex));
    float3 halfway = TangentToWorld(ImportanceSampleGgx(random, RoughnessToAlpha(roughness)), normal);
    float3 ray = reflect(-toEye, halfway);
    ray = dot(ray, normal) > 0.0 ? ray : reflect(-toEye, normal);

    // Through the view's depth, from a little off the surface, no nearer the camera than it sees.
    float3 start = seen + WorldToView(normal) * (0.01 + 0.002 * viewDepth);
    float3 along = WorldToView(ray);
    float reach = along.z < 0.0 ? min(params.length, 0.95 * (start.z - Near) / -along.z) : params.length;
    uint steps = max((uint)params.steps, 1u);
    float jitter = InterleavedGradientNoise(pixel + float2(71.0, 5.0), FrameIndex);
    float before = 0.0;
    float found = -1.0;
    float squarely = 0.0;
    [loop] for (uint step = 0u; step < steps; step++)
    {
        float t = reach * Pow2(((float)step + jitter) / (float)steps);
        float3 marched = start + along * t;
        float2 at = ViewPixel(marched);
        [branch] if (any(at < 0.0) || any(at >= ViewSize))
        {
            break;
        }

        // A step behind what was drawn by up to its thickness met it, and one up to twice as far behind trusted less the
        // further, as it may have passed behind it instead: no step is taken all or nothing.
        float behind = BehindDrawn(marched, at);
        float thick = max(params.thickness * (1.0 + 0.02 * marched.z), abs(along.z) * (t - before) * 1.5);
        [branch] if (behind > 0.0 && behind < 2.0 * thick)
        {
            squarely = saturate(2.0 - behind / thick);
            // Halve the last step until it is about where it met what was drawn.
            float inFront = before;
            float past = t;
            [unroll] for (uint halving = 0u; halving < 5u; halving++)
            {
                float middle = 0.5 * (inFront + past);
                float3 between = start + along * middle;
                bool isBehind = BehindDrawn(between, ViewPixel(between)) > 0.0;
                inFront = isBehind ? inFront : middle;
                past = isBehind ? middle : past;
            }

            found = past;
            break;
        }

        before = t;
    }

    // What it hit as the last frame lit it, trusted less toward the march's end too.
    float trust = 0.0;
    float3 lit = float3(0.0, 0.0, 0.0);
    [branch] if (found >= 0.0)
    {
        trust = LitThen(ViewPixel(start + along * found), ray, lit) * squarely * saturate(4.0 * (1.0 - found / reach));
    }

    // Past what the view shows: the sky as the surface's roughness blurs it, along where its reflection mostly comes
    // from, and what the field holds in the way.
    float width;
    float height;
    float levels;
    SkySpecular.GetDimensions(0u, width, height, levels);
    float3 dominant = SpecularDominantDirection(normal, reflect(-toEye, normal), roughness);
    float3 beyond = SkySpecular.SampleLevel(SkySpecularSampler, dominant, PrefilteredLevel(roughness, levels)).rgb;
#if defined(TRACE_FIELD)
    [branch] if (trust < 1.0)
    {
        float3 sh[9];
        [unroll] for (uint i = 0u; i < 9u; i++)
        {
            sh[i] = LOAD(SkySh, float4, i).rgb;
        }

        // From the finest level that holds the surface, far enough off it that the field there, a little fatter than
        // what it holds and coarser the farther from the camera, does not stop the ray where it starts: a far surface's
        // ray started nearer would set out inside its own coarse copy and see that. Where something is in the way even
        // so, the light the probes have arriving from there.
        GpuGi header = GiHeader();
        float3 surface = position + CameraPosition;
        uint first = 0u;
        [loop] while (first + 1u < header.levels && !GiContains(GiLevel(first), surface, 1.0))
        {
            first++;
        }

        float3 origin = surface + normal * (1.5 * GiLevel(first).voxel);
        GiHit hit = GiTrace(origin, ray, first, header.levels, GiLevel(header.levels - 1u).voxel * (float)GiResolution * 0.5);
        [branch] if (hit.isHit && !hit.isInside)
        {
            // What the field hit, lit as the view lit it where the view shows it: where what was drawn there is as near
            // as the field's surface, which stands out from the true one by a voxel or so; lit as the field's probes
            // light it where the view does not.
            float3 hitSeen = WorldToView(hit.position - CameraPosition);
            float2 hitAt = ViewPixel(hitSeen);
            float shown = 0.0;
            float3 shownLit = float3(0.0, 0.0, 0.0);
            [branch] if (hitSeen.z > Near && all(hitAt >= 0.0) && all(hitAt < ViewSize))
            {
                float apart = abs(BehindDrawn(hitSeen, hitAt));
                shown = LitThen(hitAt, ray, shownLit) * saturate(2.0 - apart / (2.0 * GiLevel(hit.level).voxel));
            }

            beyond = shownLit;
            [branch] if (shown < 1.0)
            {
                beyond = lerp(GiHitRadiance(hit, header.levels, sh), shownLit, shown);
            }
        }
        else if (hit.isInside)
        {
            float held;
            float3 arriving = GiIrradianceAt(origin, ray, ray, held);
            beyond = lerp(beyond, arriving, held);
        }
    }
#endif

    ReflectionTraced[id.xy] = float4(lerp(beyond, lit, trust), share);
}
