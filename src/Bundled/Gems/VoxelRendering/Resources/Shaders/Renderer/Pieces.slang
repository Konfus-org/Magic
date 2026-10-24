// The world's geometry read back: a cluster's vertices and triangles from the geometry's words, a card's quad, how an
// instance dissolves, and what a surface is handed at a piece's pixel.
//
// A piece is up to 32 triangles of a cluster, or a card: the unit the visibility buffer is drawn in, an instance of one
// draw of 96 indices (six for a card), whose vertex stage finds its triangle and corner from the index alone. A piece's
// three words are its instance's slot, what it draws (its cluster's row times four plus which piece, or a card's row
// with CardBit), and its slot among the view's pieces, which the visibility buffer names a pixel's triangle by:
// ((slot << 5) | triangle) + 1, so 0 is nothing drawn. What the pieces' vertex stage hands a fragment stage is
// PieceVaryings, declared once so the two cannot disagree. Every varying is the triangle's corner's, so a value all
// three corners share, as the triangle's id, needs no provoking vertex.
//
// A card's pixel (Core's Card) is the mesh as its atlases hold it, seen from one of the two views either side of the
// camera, dithered between them by how far round from one to the next the camera is, so a card turns smoothly as the
// camera goes round it rather than stepping from view to view: its material's uv, its normal, how much of the texel the
// mesh covers, and how far behind the card's quad the mesh is there, where the pixel is put, so it is lit and depth
// tested where the mesh would be. Its material's uv's derivatives are the atlas's read a pixel across and a pixel down.
// A view's normals are as its side sees them, so they are turned on by as far round as the camera is from that side,
// then as the instance turns the mesh. Its position's derivatives are where the rays of the pixels beside it meet the
// plane through where it is put, facing its normal.

module "Renderer/Pieces";

import Core.Noise;
__exported import Core.Surface;
__exported import Renderer.World;

public static const uint PieceTriangles = 32u;
public static const uint CardBit = 0x80000000u;

// How long a fade in takes.
public static const float FadeSec = 0.4;

// The share either side of the distance a model's cards take over from its clusters over which both are drawn, each
// dissolving into the other.
public static const float CardBand = 0.1;

// Core's Card: the views each atlas holds, those of a row, and a view's cell as a share of the atlas.
public static const float CardViews = 8.0;
public static const float CardColumns = 3.0;
public static const float CardCell = 85.0 / 256.0;

// The steepest an atlas is read at, as a share of it a pixel: its fourth mip, past which a view's cell would blur into
// its neighbours'.
public static const float CardMaxGradient = 8.0 / 256.0;

// A card's six indices: its four corners as two triangles, as Core's card meshes wind them.
public static const uint CardCorners[6] = { 0u, 3u, 2u, 0u, 2u, 1u };
public static const float2 CardCornerUv[4] = { float2(-1.0, -1.0), float2(1.0, -1.0), float2(1.0, 1.0), float2(-1.0, 1.0) };

public struct PieceVertex
{
    float3 position;
    float3 normal;
    float4 tangent;
    float2 uv;
}

// One corner of a card's quad: where it is relative to the camera, its normal, its corner in -1..1, the view it shows
// (its atlases' cell, its turn, whether it shows the mesh mirrored, and 1) and which way its front is (along the
// ground, xy) and how far (its half width).
public struct CardCorner
{
    float3 position;
    float3 normal;
    float2 uv;
    float4 view;
    float3 front;
}

public struct PieceVaryings
{
    float3 position : TEXCOORD0;               // relative to the camera
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;                // w = bitangent sign
    float2 uv : TEXCOORD3;
    nointerpolation uint material : TEXCOORD4; // the material's row
    nointerpolation uint flags : TEXCOORD5;    // the instance's
    nointerpolation float4 card : TEXCOORD6;   // a card's view (CardCorner.view); all 0 for a cluster
    nointerpolation float2 dissolve : TEXCOORD7; // what of it is kept as it dissolves (GetDissolve)
    nointerpolation float3 front : TEXCOORD8;  // a card's CardCorner.front
    nointerpolation uint2 atlases : TEXCOORD9; // a card's two atlases' texture ids
    nointerpolation uint id : TEXCOORD10;      // the visibility buffer's name for its triangle
    float4 clip : SV_Position;
}

// What the bins that do not clip need of a triangle: where it is and its name.
public struct PlaceVaryings
{
    nointerpolation uint id : TEXCOORD0;
    float4 clip : SV_Position;
}

// What a surface is handed at a piece's pixel, how much of the pixel a card covers (1 for a cluster's), and the depth
// the pixel is drawn at.
public struct PieceSurfaceInputs
{
    SurfaceInputs inputs;
    float cover;
    float depth;
}

int ToSigned20(uint value)
{
    return ((int)(value << 12)) >> 12;
}

// A vertex from its five words (GpuConverter.PackVertex): its position in steps of its model's grid, its normal and
// tangent on the octahedron, its uv as two halves.
PieceVertex LoadPackedVertex(uint word, float step)
{
    uint4 packed = World.geometry.Load<uint4>(word * 4u);
    uint uv = World.geometry.Load<uint>((word + 4u) * 4u);
    PieceVertex vertex;
    int x = ToSigned20(packed.x & 0xFFFFFu);
    int y = ToSigned20((packed.x >> 20) | ((packed.y & 0xFFu) << 12));
    int z = ToSigned20((packed.y >> 8) & 0xFFFFFu);
    vertex.position = float3(x, y, z) * step;
    vertex.normal = DecodeOctahedral(float2(packed.z & 0xFFFFu, packed.z >> 16) * (2.0 / 65535.0) - 1.0);
    float2 folded = float2(
        (packed.w & 0xFFFFu) * (2.0 / 65535.0) - 1.0,
        ((packed.w >> 16) & 0x7FFFu) * (2.0 / 32767.0) - 1.0);
    vertex.tangent = float4(DecodeOctahedral(folded), (packed.w >> 31) != 0u ? -1.0 : 1.0);
    vertex.uv = unpackHalf2x16ToFloat(uv);
    return vertex;
}

public extension GpuCluster
{
    public property uint triangleCount
    {
        get { return counts & 0xFFu; }
    }

    // Its material slot.
    public property uint slot
    {
        get { return (counts >> 17) & 7u; }
    }

    public property uint pieceCount
    {
        get { return (triangleCount + PieceTriangles - 1u) / PieceTriangles; }
    }

    // A triangle's three corners, each its place among the cluster's vertices.
    public uint3 LoadTriangle(uint face)
    {
        uint word = World.geometry.Load<uint>((triangles + face) * 4u);
        return uint3(word & 0xFFu, (word >> 8) & 0xFFu, (word >> 16) & 0xFFu);
    }

    // A corner's vertex, by its place among the cluster's vertices.
    public PieceVertex LoadVertex(uint corner)
    {
        return LoadPackedVertex(vertices + corner * 5u, step);
    }

    // A corner's position alone, in its model's space: the vertex's first two words.
    public float3 LoadPosition(uint corner)
    {
        uint2 packed = World.geometry.Load<uint2>((vertices + corner * 5u) * 4u);
        int x = ToSigned20(packed.x & 0xFFFFFu);
        int y = ToSigned20((packed.x >> 20) | ((packed.y & 0xFFu) << 12));
        int z = ToSigned20((packed.y >> 8) & 0xFFFFFu);
        return float3(x, y, z) * step;
    }

    // Its level among its model's, 0 the finest.
    public property uint level
    {
        get { return (counts >> 20) & 31u; }
    }
}

public extension GpuInstance
{
    // How far through its fade it is, 0 to 1.
    public float GetFadeProgress()
    {
        return saturate((World.clocks.realSec - fadeStartSec) / FadeSec);
    }
}

// How far a model's cards have taken over from its clusters at a distance, 0 to 1 across the band about the distance
// they start at (the model's own cardDistance at unit scale and a 60 degree view, scaled by the instance and the view's
// zoom); 0 for a model with no cards.
public float GetCardTakeover(GpuModel model, GpuInstance instance, float distance)
{
    if (model.cardCount == 0u || model.cardDistance <= 0.0)
    {
        return 0.0;
    }

    // A view of half height tanY sees as far as a 60 degree one sees distance * tan(30) / tanY.
    float tanY = View.viewSize.y * 0.5 / View.pixelScale;
    float seen = distance * tanY / 0.57735027;
    float start = model.cardDistance * instance.maxScale;
    return saturate((seen / start - (1.0 - CardBand)) / (2.0 * CardBand));
}

// What of a piece is kept as it dissolves, as two thresholds of a pixel's dither: it is kept where the dither is under
// x and not under y. A fade in raises x; a card taking over raises x for the card and y for the clusters, so the two
// between them draw each pixel once.
public float2 GetDissolve(GpuInstance instance, float takeover, bool isCard)
{
    float2 dissolve = float2(1.0, 0.0);
    float progress = instance.GetFadeProgress();
    dissolve.x = instance.isFadingIn ? progress : dissolve.x;
    dissolve.x = isCard ? min(dissolve.x, takeover) : dissolve.x;
    dissolve.y = isCard ? dissolve.y : max(dissolve.y, takeover);
    return dissolve;
}

// What clip() is handed for a pixel of a dissolving piece: 0 or more keeps it, less clips it. The dither moves each
// frame.
public float GetDissolveClip(float2 dissolve, float2 pixel)
{
    float dither = GetInterleavedGradientNoise(pixel, World.clocks.index);
    return min(dissolve.x - dither - 1e-4, dither - dissolve.y);
}

// Whether a piece dissolves at all, so it is drawn by its surface's raster bin, which clips.
public bool IsDissolving(float2 dissolve)
{
    return dissolve.x < 1.0 || dissolve.y > 0.0;
}

// One corner of a card's quad, which turns about +Y to face the camera at the front of its mesh, as wide and tall as
// the instance scales it.
public CardCorner GetCardCorner(GpuInstance instance, GpuCard card, uint corner)
{
    float3 axisX = float3(instance.row0.x, instance.row1.x, instance.row2.x);
    float3 axisY = float3(instance.row0.y, instance.row1.y, instance.row2.y);
    float3 axisZ = float3(instance.row0.z, instance.row1.z, instance.row2.z);
    float3 centre = instance.ToCameraRelative(card.centre);
    bool showsMirrored = instance.isMirrored != (card.halfWidth < 0.0);
    float turn = card.turn + atan2(axisZ.x, axisZ.z);

    // The camera is at the origin: the way to it along the ground is the centre's, back.
    float2 toCamera = -centre.xz;
    float2 side = dot(toCamera, toCamera) > 1e-8 ? toCamera * rsqrt(dot(toCamera, toCamera)) : float2(0.0, 1.0);
    float2 cornerUv = CardCornerUv[corner];
    float across = instance.isMirrored ? -cornerUv.x : cornerUv.x;
    float width = abs(card.halfWidth) * max(length(axisX), length(axisZ));
    float height = card.halfHeight * length(axisY);

    // The camera's side turned back into the mesh's space, then mirrored back, is the view to show.
    float turnCos = cos(turn);
    float turnSin = sin(turn);
    float2 seen = float2(turnCos * side.x - turnSin * side.y, turnSin * side.x + turnCos * side.y);
    seen.x = showsMirrored ? -seen.x : seen.x;

    CardCorner quad;
    quad.position = centre
        + float3(side.x, 0.0, side.y) * width
        + float3(-side.y, 0.0, side.x) * (across * width)
        + float3(0.0, cornerUv.y * height, 0.0);
    quad.normal = float3(side.x, 0.0, side.y);
    quad.uv = float2(showsMirrored ? -across : across, cornerUv.y);
    quad.view = float4(frac(atan2(seen.x, seen.y) / TwoPi) * CardViews, turn, showsMirrored ? 1.0 : 0.0, 1.0);
    quad.front = float3(side, width);
    return quad;
}

// Whether a sphere about a camera-relative centre is at least partly inside the view: in front of the near plane, past
// none of the four sides, and nearer than the far distance.
public bool IsSphereInView(float3 centre, float radius)
{
    float3 seen = View.ToView(centre);
    float2 slopes = sqrt(View.projScale * View.projScale + 1.0);
    return seen.z + radius > View.near
        && abs(seen.x) * View.projScale.x - seen.z <= radius * slopes.x
        && abs(seen.y) * View.projScale.y - seen.z <= radius * slopes.y
        && seen.z - radius < View.far;
}

// The atlas's change a pixel across or down, from the quad's corner uv's, held to the steepest it is read at.
float2 GetCardGradient(float2 cornerGradient)
{
    float2 gradient = float2(cornerGradient.x * 0.5, -cornerGradient.y * 0.5) * CardCell;
    float steepness = length(gradient);
    return steepness > CardMaxGradient ? gradient * (CardMaxGradient / steepness) : gradient;
}

// A uv change across a pixel, read from the atlas, where the uv wraps round as the atlas holds it fractionally.
float2 GetWrappedDifference(float2 to, float2 from)
{
    float2 difference = to - from;
    return difference - round(difference);
}

// Where the ray from the camera through a point meets the plane through another facing normal, both camera-relative. A
// ray that grazes the plane is taken to meet it as at a tenth of face on, so a derivative made of it never runs off.
float3 IntersectPlane(float3 through, float3 at, float3 normal)
{
    float3 ray = NormalizeOrZero(through);
    float facing = dot(normal, ray);
    facing = facing < 0.0 ? min(facing, -0.1) : max(facing, 0.1);
    return ray * (dot(normal, at) / facing);
}

// The surface's inputs at a piece's pixel, given its uv's and its position's derivatives across a pixel and down one,
// which the caller works out, as the resolve has no neighbouring pixels of its triangle to take them from: its normal
// turned to the face seen on a double-sided surface's back, the derivatives scaled by textureMipScale, the frame's
// clocks, the world's weather and the sampler materials' textures are read through.
public PieceSurfaceInputs CreateSurfaceInputs(
    PieceVaryings input,
    float2 uvDdx,
    float2 uvDdy,
    float3 positionDdx,
    float3 positionDdy,
    bool isFrontFace,
    bool isDoubleSided)
{
    PieceSurfaceInputs piece;
    SurfaceInputs inputs;
    inputs.worldPosition = input.position + View.cameraPosition;
    inputs.normal = NormalizeOrZero(input.normal);
    inputs.tangent = input.tangent;
    inputs.uv = input.uv;
    inputs.uvDdx = uvDdx;
    inputs.uvDdy = uvDdy;
    inputs.positionDdx = positionDdx;
    inputs.positionDdy = positionDdy;
    inputs.sampler = World.materialSampler;
    piece.cover = 1.0;
    piece.depth = input.clip.z;
    bool isCard = input.card.w != 0.0;
    [branch] if (isCard)
    {
        float3 placed = input.position;
        float pick = GetInterleavedGradientNoise(input.clip.xy + float2(41.0, 23.0), World.clocks.index);
        float shown = floor(input.card.x) + (frac(input.card.x) > pick ? 1.0 : 0.0);
        float view = fmod(shown, CardViews);
        float2 local = float2(input.uv.x * 0.5 + 0.5, 0.5 - input.uv.y * 0.5);
        float2 atlas = (float2(fmod(view, CardColumns), floor(view / CardColumns)) + local) * CardCell;
        float2 atlasDdx = GetCardGradient(uvDdx);
        float2 atlasDdy = GetCardGradient(uvDdy);
        TextureRef surfaceAtlas = { input.atlases.x };
        TextureRef normalAtlas = { input.atlases.y };
        if (surfaceAtlas.isValid)
        {
            float4 surface = inputs.SampleGrad(surfaceAtlas, atlas, atlasDdx, atlasDdy);
            inputs.uv = surface.xy;
            piece.cover = surface.w;
            float2 across = inputs.SampleGrad(surfaceAtlas, atlas + atlasDdx, atlasDdx, atlasDdy).xy;
            float2 down = inputs.SampleGrad(surfaceAtlas, atlas + atlasDdy, atlasDdx, atlasDdy).xy;
            inputs.uvDdx = GetWrappedDifference(across, surface.xy);
            inputs.uvDdy = GetWrappedDifference(down, surface.xy);

            // Behind the quad, which stands at the front of the mesh, along the ray from the camera: blue is how far
            // behind the mesh's middle, so twice it is how far behind the front, in half widths.
            float3 ray = NormalizeOrZero(input.position);
            float behind = input.front.z * (surface.z * 2.0);
            float along = max(dot(ray, -float3(input.front.x, 0.0, input.front.y)), 0.1);
            placed = input.position + ray * (behind / along);
            inputs.worldPosition = placed + View.cameraPosition;
            float4 clip = View.ToClip(placed);
            piece.depth = min(clip.z / max(clip.w, 1e-6), input.clip.z);
        }

        if (normalAtlas.isValid)
        {
            float3 normal = inputs.SampleGrad(normalAtlas, atlas, atlasDdx, atlasDdy).xyz * 2.0 - 1.0;
            bool isMirrored = input.card.z != 0.0;
            float roundBy = (input.card.x - shown) * (TwoPi / CardViews);
            float turn = input.card.y + (isMirrored ? -roundBy : roundBy);
            normal.x = isMirrored ? -normal.x : normal.x;
            float turnCos = cos(turn);
            float turnSin = sin(turn);
            float3 turned = float3(normal.x * turnCos + normal.z * turnSin, normal.y, normal.z * turnCos - normal.x * turnSin);
            inputs.normal = NormalizeOrZero(turned);
        }

        inputs.positionDdx = IntersectPlane(input.position + positionDdx, placed, inputs.normal) - placed;
        inputs.positionDdy = IntersectPlane(input.position + positionDdy, placed, inputs.normal) - placed;
    }

    inputs.uvDdx *= View.textureMipScale;
    inputs.uvDdy *= View.textureMipScale;
    inputs.positionDdx *= View.textureMipScale;
    inputs.positionDdy *= View.textureMipScale;
    inputs.screenUv = input.clip.xy * View.viewTexel;
    inputs.frame = World.clocks;
    inputs.cameraPosition = View.cameraPosition;
    inputs.viewDirection = NormalizeOrZero(-(inputs.worldPosition - View.cameraPosition));
    inputs.weather = World.weather;

    // A mirrored instance's corners were swapped, so what the rasteriser calls its front is its front.
    inputs.isFrontFace = isFrontFace;
    inputs.normal = isDoubleSided && !isFrontFace ? -inputs.normal : inputs.normal;
    piece.inputs = inputs;
    return piece;
}
