// The sun's light that one bounce off the lit surfaces brings to each pixel, from the view's reflective shadow map
// (Dachsbacher and Stamminger, "Reflective Shadow Maps", 2005). Each texel of the map is a small surface that the sun
// lights, and so a light of its own: its diffuse colour times the sun's light on it. A pixel gathers the texels about
// its own place in the map, on a spiral that is denser near the centre, and adds the light of each as a small area
// light: its colour times the cosines at both ends over the distance squared. Nothing tests whether a wall lies
// between them, but the cosines leave out a texel behind the pixel's surface or one that faces away from it. The
// spiral turns at each pixel and each frame, and a blur and the temporal anti-aliasing average it. The pass header's x
// is the frame's buffer and its y the buffer of BounceConstants.
module "GatherBounce";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Math;
import Core.Random;
import Scene.Lights;
import Scene.ShadowViews;
import Sky.ISky;

// What the passes read beyond the frame, C#'s BounceConstants, 112 bytes.
struct BounceConstants
{
    // The map's view, its depth, colour and normal, the gathered light at half size and its blur.
    uint view;
    uint depth;
    uint albedo;
    uint normal;
    uint gathered;
    uint blurred;
    uint halfWidth;
    uint halfHeight;
    // The texels gathered for each pixel, and the metres across the map that they reach from it.
    uint samples;
    float reach;
    float padding0;
    float padding1;
    // From the map's clip space to the camera-relative world: the inverse of its view-projection.
    float4x4 clipToRelative;
};

// The least squared distance, in square metres, that a texel's light is spread over: a texel next to the pixel does
// not light it without end.
static const float NearestSquared = 0.05;

// The golden angle in radians, which spreads a spiral's samples evenly.
static const float GoldenAngle = 2.3999632;

BounceConstants LoadBounceConstants() { return LoadConstants<BounceConstants>(GetPassHeader().y); }

// The light that one bounce brings to each pixel at half size, as the irradiance over pi that a white diffuse surface
// reflects.
[shader("compute")]
[numthreads(8, 8, 1)]
void GatherBounce<S : ISky>(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    BounceConstants constants = LoadBounceConstants();
    if (any(thread.xy >= uint2(constants.halfWidth, constants.halfHeight)))
        return;

    StorageRef gathered = { constants.gathered, 0u };
    uint2 pixel = min(thread.xy * 2u, uint2(frame.width, frame.height) - 1u);
    TextureRef depthTexture = { frame.depth };
    float depth = depthTexture.Load(pixel, 0u).x;
    if (depth == 0.0)
    {
        gathered.Store(thread.xy, float4(0.0));
        return;
    }

    float3 point = ReconstructPixel(frame, pixel, depth);
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    GpuShadowView view = LoadShadowView(constants.view, 0u);
    float4 clip = mul(float4(point, 1.0), view.viewProjection);
    float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    if (any(uv <= 0.0) || any(uv >= 1.0))
    {
        gathered.Store(thread.xy, float4(0.0));
        return;
    }

    Lights lights = GetLights();
    float3 sun = GetSunIlluminance<S>(lights);
    float3 towardsSun = lights.sunDirection.xyz;
    TextureRef mapDepth = { constants.depth };
    TextureRef mapAlbedo = { constants.albedo };
    TextureRef mapNormal = { constants.normal };
    float2 size = float2(mapDepth.GetSize(0u));
    float2 texel = uv * size;
    float radius = constants.reach / view.texelSize;
    float rotation = GetInterleavedGradientNoise(pixel, frame.frameIndex) * TwoPi;
    uint samples = max(constants.samples, 1u);
    // Each sample stands for the area of the map that the spiral gives it: its radius is the reach times an even
    // share, so its density over the map falls as one over the radius.
    float areaScale = TwoPi * constants.reach * constants.reach / float(samples);
    float3 irradiance = float3(0.0);
    for (uint i = 0u; i < samples; i++)
    {
        float share = (float(i) + 0.5) / float(samples);
        float angle = float(i) * GoldenAngle + rotation;
        float2 at = texel + float2(cos(angle), sin(angle)) * share * radius;
        if (any(at < 0.0) || any(at >= size))
            continue;

        uint2 read = uint2(at);
        float mapZ = mapDepth.Load(read, 0u).x;
        if (mapZ == 0.0)
            continue;

        float2 ndc = (float2(read) + 0.5) / size * float2(2.0, -2.0) + float2(-1.0, 1.0);
        float4 lit = mul(float4(ndc, mapZ, 1.0), constants.clipToRelative);
        float3 source = lit.xyz / lit.w;
        float3 sourceNormal = NormalizeSafe(mapNormal.Load(read, 0u).xyz * 2.0 - 1.0, float3(0.0, 1.0, 0.0));
        float3 radiosity = mapAlbedo.Load(read, 0u).rgb * sun * saturate(dot(sourceNormal, towardsSun));
        float3 toPoint = point - source;
        float squared = max(dot(toPoint, toPoint), NearestSquared);
        float transfer = max(dot(sourceNormal, toPoint), 0.0) * max(-dot(normal, toPoint), 0.0) / (squared * squared);
        irradiance += radiosity * transfer * share * areaScale * InvPi;
    }

    gathered.Store(thread.xy, float4(irradiance * InvPi, 0.0));
}

// A 5 by 5 blur of the gathered light that keeps to surfaces: each neighbour weighed by how near its depth and its
// normal are to the pixel's.
[shader("compute")]
[numthreads(8, 8, 1)]
void BlurBounce(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    BounceConstants constants = LoadBounceConstants();
    int2 size = int2(constants.halfWidth, constants.halfHeight);
    if (any(int2(thread.xy) >= size))
        return;

    TextureRef gathered = { constants.gathered };
    TextureRef depthTexture = { frame.depth };
    int2 full = int2(frame.width, frame.height) - 1;
    uint2 pixel = uint2(min(int2(thread.xy) * 2, full));
    float depth = depthTexture.Load(pixel, 0u).x;
    StorageRef blurred = { constants.blurred, 0u };
    if (depth == 0.0)
    {
        blurred.Store(thread.xy, float4(0.0));
        return;
    }

    float viewDepth = frame.near / depth;
    float3 normal = LoadNormal(frame.gbufferNormal, pixel);
    float3 sum = float3(0.0);
    float weight = 0.0;
    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            int2 at = clamp(int2(thread.xy) + int2(x, y), int2(0), size - 1);
            uint2 atPixel = uint2(min(at * 2, full));
            float atDepth = depthTexture.Load(atPixel, 0u).x;
            if (atDepth == 0.0)
                continue;

            float depthWeight = exp(-abs(frame.near / atDepth - viewDepth) / max(0.05 * viewDepth, 1e-3));
            float normalWeight = pow(saturate(dot(LoadNormal(frame.gbufferNormal, atPixel), normal)), 8.0);
            float w = depthWeight * normalWeight + 1e-4;
            sum += gathered.Load(uint2(at), 0u).rgb * w;
            weight += w;
        }
    }

    blurred.Store(thread.xy, float4(sum / max(weight, Epsilon), 0.0));
}
