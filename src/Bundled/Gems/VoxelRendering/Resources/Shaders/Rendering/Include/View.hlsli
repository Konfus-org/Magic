// A view's constants for a frame: the renderer's ViewConstants (GpuStructs.cs), member for member: change both
// together. Its camera, its sizes, the world's lights and weather, and the heap ids of the world's tables. A pass names
// the view's buffer among its constants and loads it first (LoadView), into View, which everything after reads.
// Camera-relative: a position is relative to the camera before viewProj takes it. Reverse-Z with no far plane: a depth
// is near over the view depth, 0 where nothing was drawn. A matrix is its four rows, as System.Numerics keeps them,
// which take a row vector (Transform).

#ifndef MAGIC_RENDERING_VIEW_HLSLI
#define MAGIC_RENDERING_VIEW_HLSLI

#include "Include/Curve.hlsli"
#include "Include/Heap.hlsli"

// The world's weather, still, clear and with no fog without one.
struct WeatherConstants
{
    float3 wind;             // metres per second: its direction and strength
    float coverage;          // how much of the sky clouds cover, 0 clear to 1 overcast
    float3 cloudColor;       // the clouds where the sun lights them, linear
    float density;           // how thick they are, 0 wisps to 1 thick
    float3 cloudShadow;      // the clouds in their own shade and underneath, linear
    float fog;               // the share of the light the fog puts out a metre where its falloff is 1; 0, none
    float3 fogColor;         // the fog's tint, times the light that reaches it, linear
    float fogStart;          // metres from the camera before the fog begins
    Curve fogFalloff;        // the fog's share by metres above the world's origin
};

// The sky's sun and moon, which the sky draws whether or not either lights the world now. Each points straight down
// with no light without one.
struct SkyLightConstants
{
    float3 sunDirection;     // the way its light travels
    float sunAngularRadius;  // its disc's half-angle, radians
    float3 sunIlluminance;   // lux times its colour, above the air
    float moonAngularRadius;
    float3 moonDirection;
    float padding0;
    float3 moonIlluminance;  // a full moon's lux times its colour, above the air
    float padding1;
};

// A directional light that lights the world alone, without shadows.
struct DirectionalLightConstants
{
    float3 direction;        // the way its light travels
    float padding0;
    float3 illuminance;      // lux times its colour
    float padding1;
};

// The directional lights a view holds besides the key light; the renderer's DirectionalLights.Capacity.
static const uint DirectionalLightCapacity = 4u;

struct ViewConstants
{
    float4 viewProj[4];
    float4 rotation[4];      // world to view, a rotation alone
    float4 prevViewProj[4];  // the last frame's viewProj

    float3 cameraPosition;
    float time;

    float2 viewSize;         // pixels as the view is drawn, and one over them
    float2 viewTexel;

    float2 projScale;        // the projection's x and y scales
    float near;
    float far;

    // The key light: the directional light that casts the shadows, the sun while it is up, else the moon while it is,
    // else the brightest other.
    float3 keyLightDirection;     // the way its light travels
    float keyLightAngularRadius;  // its disc's half-angle, radians

    float3 keyLightIlluminance;   // lux times its colour; zero with none
    uint frameIndex;

    float2 outputSize;       // pixels as the view is shown
    float2 jitter;           // this frame's sub-pixel offset of viewProj, in clip space

    float3 cameraMotion;     // the camera's position less the last frame's
    float pixelScale;        // pixels a metre covers one metre in front of the camera, along the view's height

    float3 priorityPosition; // the player's camera relative to this one, which detail is kept nearest to
    float textureMipScale;   // what uv gradients are scaled by

    float deltaSeconds;
    float clusterError;      // pixels a cluster's error may cover before a finer one is drawn
    uint keyLightThroughSky; // 1 when the key light is the sky's sun or moon
    uint directionalCount;   // how many of directional light the world

    uint targetEncoding;     // 0 linear as it is, 1 sRGB, 2 PQ (HDR10), 3 scRGB: Encode.hlsli
    uint skyMaterial;        // the sky's material's row
    uint materialSampler;    // the heap ids of the sampler materials' textures are read with,
    uint failures;           // and of the frame's raised failure codes (Include/Failure.hlsli)

    // The heap ids of the world's tables (Tables.hlsli).
    uint instances;
    uint models;
    uint levels;
    uint clusters;

    uint cards;
    uint materials;
    uint materialBins;
    uint geometry;

    uint exposure;           // the heap ids of the view's exposure, a float4: the exposure, its EV100, metered or not,
    uint skySh;              // and of the sky's harmonics, nine float4s
    uint padding0;
    uint padding1;

    WeatherConstants weather;
    SkyLightConstants skyLights;
    DirectionalLightConstants directional[DirectionalLightCapacity];
};

static ViewConstants View;

// Loads the view's constants from the buffer of heap id view, into View.
void LoadView(uint view)
{
    View = BUFFER(view).Load<ViewConstants>(0u);
}

// A row vector through a matrix kept as its rows.
float4 Transform(float4 rows[4], float4 value)
{
    return value.x * rows[0] + value.y * rows[1] + value.z * rows[2] + value.w * rows[3];
}

// A camera-relative position through the view-projection, and one relative to the last frame's camera through the last
// frame's.
float4 ToClip(float3 position)
{
    return Transform(View.viewProj, float4(position, 1.0));
}

float4 ToPrevClip(float3 position)
{
    return Transform(View.prevViewProj, float4(position, 1.0));
}

// The distance along the view's +Z a depth stands for; only for a depth above 0.
float ViewDepth(float depth)
{
    return View.near / max(depth, 1e-12);
}

// A view-space position from a point of the view (pixels from its top left, a pixel's centre at +0.5) and its depth,
// the frame's jitter taken back out.
float3 ViewPosition(float2 pixel, float viewDepth)
{
    float2 ndc = float2(pixel.x * View.viewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * View.viewTexel.y * 2.0) - View.jitter;
    return float3(ndc / View.projScale * viewDepth, viewDepth);
}

// Where a view-space position in front of the camera falls on the view, in pixels from its top left.
float2 ViewPixel(float3 seen)
{
    float2 ndc = seen.xy * View.projScale / seen.z + View.jitter;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * View.viewSize;
}

// The rotation's transpose undoes it.
float3 ViewToWorld(float3 direction)
{
    return float3(dot(direction, View.rotation[0].xyz), dot(direction, View.rotation[1].xyz), dot(direction, View.rotation[2].xyz));
}

float3 WorldToView(float3 direction)
{
    return Transform(View.rotation, float4(direction, 0.0)).xyz;
}

#endif
