// What the material templates (Rendering/Materials/VisibilityClip.frag.hlsl, Resolve.frag.hlsl) hand a surface: where
// the pixel is, its normal (turned to the face seen on a double-sided surface's back), its uv and position and their
// derivatives, which the caller works out, as the resolve has no neighbouring pixels of its triangle to take them from,
// scaled by textureMipScale, which side of it the camera sees, and the world's weather. Composed after the surface.
//
// A card's pixel (Core's Card) is the mesh as its atlases hold it, seen from one of the two views either side of the
// camera, dithered between them by how far round from one to the next the camera is, so a card turns smoothly as the
// camera goes round it rather than stepping from view to view: its material's uv, its normal, how much of the texel the
// mesh covers, and how far behind the card's quad the mesh is there, where the pixel is put, so it is lit and depth
// tested where the mesh would be. Its material's uv's derivatives are the atlas's read a pixel across and a pixel down.
// A view's normals are as its side sees them, so they are turned on by as far round as the camera is from that side,
// then as the instance turns the mesh. Its position's derivatives are where the rays of the pixels beside it meet the
// plane through where it is put, facing its normal.

#ifndef MAGIC_RENDERING_PIECE_FRAGMENT_HLSLI
#define MAGIC_RENDERING_PIECE_FRAGMENT_HLSLI

#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"
#include "Include/PieceVaryings.hlsli"
#include "Include/Surface.hlsli"
#include "Rendering/Include/View.hlsli"

// Core's Card: the views each atlas holds, those of a row, and a view's cell as a share of the atlas.
static const float CardAtlasViews = 8.0;
static const float CardColumns = 3.0;
static const float CardCell = 85.0 / 256.0;

// The steepest an atlas is read at, as a share of it a pixel: its fourth mip, past which a view's cell would blur into
// its neighbours'.
static const float CardMaxGradient = 8.0 / 256.0;

float2 CardGradientOf(float2 cornerGradient)
{
    float2 gradient = float2(cornerGradient.x * 0.5, -cornerGradient.y * 0.5) * CardCell;
    float steepness = length(gradient);
    return steepness > CardMaxGradient ? gradient * (CardMaxGradient / steepness) : gradient;
}

// A uv change across a pixel, read from the atlas, where the uv wraps round as the atlas holds it fractionally.
float2 WrappedDifference(float2 to, float2 from)
{
    float2 difference = to - from;
    return difference - round(difference);
}

// Where the ray from the camera through a point meets the plane through another facing normal, both camera-relative. A
// ray that grazes the plane is taken to meet it as at a tenth of face on, so a derivative made of it never runs off.
float3 OnPlane(float3 through, float3 at, float3 normal)
{
    float3 ray = NormalizeOrZero(through);
    float facing = dot(normal, ray);
    facing = facing < 0.0 ? min(facing, -0.1) : max(facing, 0.1);
    return ray * (dot(normal, at) / facing);
}

// The surface's inputs at a piece's pixel, given its uv's and its position's derivatives across a pixel and down one,
// how much of the pixel a card covers (1 for a cluster's), and the depth it is drawn at.
SurfaceInputs PieceInputsOf(
    PieceVaryings input,
    float2 uvDdx,
    float2 uvDdy,
    float3 positionDdx,
    float3 positionDdy,
    bool isFrontFace,
    out float cover,
    out float depth)
{
    SurfaceInputs inputs;
    inputs.worldPosition = input.position + View.cameraPosition;
    inputs.normal = NormalizeOrZero(input.normal);
    inputs.tangent = input.tangent;
    inputs.uv = input.uv;
    inputs.uvDdx = uvDdx;
    inputs.uvDdy = uvDdy;
    inputs.positionDdx = positionDdx;
    inputs.positionDdy = positionDdy;
    cover = 1.0;
    depth = input.clip.z;
    bool isCard = input.card.w != 0.0;
    [branch] if (isCard)
    {
        float3 placed = input.position;
        float pick = InterleavedGradientNoise(input.clip.xy + float2(41.0, 23.0), View.frameIndex);
        float shown = floor(input.card.x) + (frac(input.card.x) > pick ? 1.0 : 0.0);
        float view = fmod(shown, CardAtlasViews);
        float2 local = float2(input.uv.x * 0.5 + 0.5, 0.5 - input.uv.y * 0.5);
        float2 atlas = (float2(fmod(view, CardColumns), floor(view / CardColumns)) + local) * CardCell;
        float2 atlasDdx = CardGradientOf(uvDdx);
        float2 atlasDdy = CardGradientOf(uvDdy);
        if (HasTexture(input.atlases.x))
        {
            float4 surface = SampleTextureGrad(input.atlases.x, atlas, atlasDdx, atlasDdy);
            inputs.uv = surface.xy;
            cover = surface.w;
            inputs.uvDdx = WrappedDifference(SampleTextureGrad(input.atlases.x, atlas + atlasDdx, atlasDdx, atlasDdy).xy, surface.xy);
            inputs.uvDdy = WrappedDifference(SampleTextureGrad(input.atlases.x, atlas + atlasDdy, atlasDdx, atlasDdy).xy, surface.xy);

            // Behind the quad, which stands at the front of the mesh, along the ray from the camera: blue is how far
            // behind the mesh's middle, so twice it is how far behind the front, in half widths.
            float3 ray = NormalizeOrZero(input.position);
            float behind = input.front.z * (surface.z * 2.0);
            float along = max(dot(ray, -float3(input.front.x, 0.0, input.front.y)), 0.1);
            placed = input.position + ray * (behind / along);
            inputs.worldPosition = placed + View.cameraPosition;
            float4 clip = ToClip(placed);
            depth = min(clip.z / max(clip.w, 1e-6), input.clip.z);
        }

        if (HasTexture(input.atlases.y))
        {
            float3 normal = SampleTextureGrad(input.atlases.y, atlas, atlasDdx, atlasDdy).xyz * 2.0 - 1.0;
            bool isMirrored = input.card.z != 0.0;
            float roundBy = (input.card.x - shown) * (TwoPi / CardAtlasViews);
            float turn = input.card.y + (isMirrored ? -roundBy : roundBy);
            normal.x = isMirrored ? -normal.x : normal.x;
            float turnCos = cos(turn);
            float turnSin = sin(turn);
            float3 turned = float3(normal.x * turnCos + normal.z * turnSin, normal.y, normal.z * turnCos - normal.x * turnSin);
            inputs.normal = NormalizeOrZero(turned);
        }

        inputs.positionDdx = OnPlane(input.position + positionDdx, placed, inputs.normal) - placed;
        inputs.positionDdy = OnPlane(input.position + positionDdy, placed, inputs.normal) - placed;
    }

    inputs.uvDdx *= View.textureMipScale;
    inputs.uvDdy *= View.textureMipScale;
    inputs.positionDdx *= View.textureMipScale;
    inputs.positionDdy *= View.textureMipScale;
    inputs.screenUv = input.clip.xy * View.viewTexel;
    inputs.time = View.time;
    inputs.cameraPosition = View.cameraPosition;
    inputs.viewDirection = NormalizeOrZero(-(inputs.worldPosition - View.cameraPosition));
    inputs.wind = View.weather.wind;
    inputs.coverage = View.weather.coverage;

    // A mirrored instance's corners were swapped, so what the rasteriser calls its front is its front.
    inputs.isFrontFace = isFrontFace;
#if SURFACE_DOUBLE_SIDED
    inputs.normal = inputs.isFrontFace ? inputs.normal : -inputs.normal;
#endif
    return inputs;
}

#endif
