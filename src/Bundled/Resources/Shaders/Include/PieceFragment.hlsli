// What the fragment templates of pieces (Templates/VisibilityClip.frag.hlsl, Templates/Resolve.frag.hlsl,
// Templates/ForwardPieces.frag.hlsl) hand a surface: where the pixel is, its normal (turned to the face seen on a
// double-sided surface's back), its uv and their derivatives, which the caller works out, as the resolve has no
// neighbouring pixels of its triangle to take them from, scaled by TextureLodScale, and which side of it the camera
// sees. Composed after the surface.
//
// A card's pixel (Core's Card) is the mesh as its atlases hold it, seen from the view nearest the camera, or near
// halfway round to the next, dithered between the two, so a card shows one view whole most of the time and does not
// speckle where two views of a boxy mesh disagree: its material's uv, its normal, how much of the texel the mesh
// covers, and how far behind the card's quad the mesh is there, where the pixel is put, so it is lit, shadowed and
// depth tested where the mesh would be. Its material's uv's derivatives are the atlas's read a pixel across and a pixel
// down, as the uv comes from the atlas. A view's normals are as its side sees them, so they are turned on by as far
// round as the camera is from that side, then as the instance turns the mesh: the two views then agree where the mesh
// looks alike from both, as a sphere does.

#ifndef MAGIC_PIECE_FRAGMENT_HLSLI
#define MAGIC_PIECE_FRAGMENT_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Math.hlsli"
#include "Include/Noise.hlsli"
#include "Include/PieceVaryings.hlsli"
#include "Include/Surface.hlsli"

// Core's Card: the views each atlas holds, those of a row, and a view's cell as a share of the atlas, its Size /
// Columns texels of Size.
static const float CardAtlasViews = 8.0;
static const float CardColumns = 3.0;
static const float CardCell = 85.0 / 256.0;

// The share of the way round from one view to the next, either side of halfway, over which a card dithers between them.
static const float CardBlend = 0.15;

// The steepest an atlas is read at, as a share of it a pixel: its fourth mip, past which a view's cell would blur
// into its neighbours'.
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

// The surface's inputs at a piece's pixel, given its uv's derivatives across a pixel and down one, how much of the
// pixel a card covers (1 for a cluster's), and the depth it is drawn at.
SurfaceInputs PieceInputsOf(PieceVaryings input, float2 uvDdx, float2 uvDdy, bool isFrontFace, out float cover, out float depth)
{
    SurfaceInputs inputs;
    inputs.worldPosition = input.position + CameraPosition;
    inputs.normal = NormalizeOrZero(input.normal);
    inputs.tangent = input.tangent;
    inputs.uv = input.uv;
    inputs.uvDdx = uvDdx;
    inputs.uvDdy = uvDdy;
    cover = 1.0;
    depth = input.clip.z;
    bool isCard = input.card.w != 0.0;
    [branch] if (isCard)
    {
        float pick = InterleavedGradientNoise(input.clip.xy + float2(41.0, 23.0), 0u);
        float toNext = saturate((frac(input.card.x) - 0.5 + CardBlend) / (2.0 * CardBlend));
        float nearest = floor(input.card.x) + (toNext > pick ? 1.0 : 0.0);
        float view = fmod(nearest, CardAtlasViews);
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
            float3 placed = input.position + ray * (behind / along);
            inputs.worldPosition = placed + CameraPosition;
            float4 clip = mul(ViewProj, float4(placed, 1.0));
            depth = min(clip.z / max(clip.w, 1e-6), input.clip.z);
        }

        if (HasTexture(input.atlases.y))
        {
            float3 normal = SampleTextureGrad(input.atlases.y, atlas, atlasDdx, atlasDdy).xyz * 2.0 - 1.0;
            bool isMirrored = input.card.z != 0.0;
            float roundBy = (input.card.x - nearest) * (TwoPi / CardAtlasViews);
            float turn = input.card.y + (isMirrored ? -roundBy : roundBy);
            normal.x = isMirrored ? -normal.x : normal.x;
            float turnCos = cos(turn);
            float turnSin = sin(turn);
            float3 turned = float3(normal.x * turnCos + normal.z * turnSin, normal.y, normal.z * turnCos - normal.x * turnSin);
            inputs.normal = NormalizeOrZero(turned);
        }
    }

    inputs.uvDdx *= TextureLodScale;
    inputs.uvDdy *= TextureLodScale;
    inputs.screenUv = input.clip.xy * ViewTexel;
    inputs.time = Time;
    inputs.cameraPosition = CameraPosition;
    inputs.viewDirection = NormalizeOrZero(-(inputs.worldPosition - CameraPosition));

    // A mirrored instance's corners were swapped, so what the rasteriser calls its front is its front.
    inputs.isFrontFace = isFrontFace;
#if SURFACE_DOUBLE_SIDED
    inputs.normal = inputs.isFrontFace ? inputs.normal : -inputs.normal;
#endif
    return inputs;
}

#endif
