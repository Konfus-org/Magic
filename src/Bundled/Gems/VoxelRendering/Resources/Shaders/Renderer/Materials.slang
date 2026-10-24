// The world's surfaces drawn: the visibility buffer, each pixel the name of the triangle in front, then the G-buffer,
// each pixel's surface resolved over the tiles that show it. Every surface's pipeline is these entry points specialized
// with its struct, and their stages share MaterialConstants, the renderer's (WorldPasses.cs), member for member.
//
// The visibility buffer is a draw of pieces a raster bin. Its vertex stage has no vertex buffer: each instance of the
// draw is a piece of the view's (its instance's slot, which cluster and piece or card, and the piece's slot among the
// view's), from which each index of the draw is worked out: its triangle, index / 3, and corner, index % 3, of the
// piece's 32, the corner's vertex read from the geometry's words and moved by its instance, relative to the camera. A
// triangle past the cluster's last is put where no triangle is drawn. A mirrored instance's triangles wind the other
// way, so its second and third corners swap and the pipelines cull back faces alike. A card's six indices are its
// quad's, turned to face the camera. The bins that do not clip hand on only where the triangle is and its name
// (DrawPiecePlaces, WriteVisibility), and read nothing of its surface. A surface's bin clips (WriteClippedVisibility):
// it keeps a pixel where a card's mesh covers it, its dissolve keeps it (a fade, or a card and its clusters taking over
// from each other), and, for a masked material, its alpha reaches its cutoff. A card's pixel is put at the depth of the
// mesh it shows, never nearer than its quad, so the depth test still culls early.
//
// Then ClassifyTiles sorts the view's 8 x 8 tiles by the surfaces their pixels are shaded with, its last group writes
// each surface's draw over its tiles, and the resolve draws a quad over each (DrawTiles) and shades each pixel of the
// tile its surface owns once (ResolveSurfaces). The pixel's triangle is the one the visibility buffer names: its piece,
// its instance, its cluster's three corners read from the geometry and moved as the vertex stage moved them (or a
// card's quad), and where the pixel falls on it, by the triangle's 2D homogeneous coordinates (Olano & Greer, "Triangle
// Scan Conversion using 2D Homogeneous Coordinates", 1997), which are perspective-correct and hold for a corner behind
// the camera. Its uv's derivatives, its position's and its normal's are the same taken a pixel across and a pixel down.
// Then the surface is evaluated as the rasteriser's fragments would have, its roughness widened by how far the normal
// turns across the pixel (Lighting/Brdf's GetFilteredRoughness), and written to the G-buffer (Core/GBuffer), its
// emission to a target of its own, with the pixel's velocity: where it was on screen last frame, its instance's last
// place through the last frame's view, less where it is now without this frame's jitter, in uv.

module "Renderer/Materials";

import Core.Compute;
import Core.Failure;
import Core.GBuffer;
import Lighting.Brdf;
import Renderer.Culling;
import Renderer.Pieces;

struct MaterialConstants
{
    ArrayRef<uint3> pieces;      // the view's pieces (Renderer/Pieces), an instance of the draw each
    ArrayRef<uint> tiles;        // the resolve: the tile list, an instance of the draw each
    UintTextureRef visibility;   // the resolve: the visibility buffer
}

struct ClassifyConstants
{
    UintTextureRef visibility;   // the visibility buffer
    ArrayRef<uint3> pieces;
    ArrayRef<uint> tileCounts;   // the tiles each resolve bin holds, then the groups done (TileGroupsDone)
    ArrayRef<uint> tileList;     // each resolve bin's tiles, a run of as many as the view has
    ArrayRef<IndexedDrawArgs> resolveArgs; // each resolve bin's draw
}

// What the resolve's vertex stage hands its fragment stage: the resolve bin the tile was given, and where the pixel is.
struct TileVaryings
{
    nointerpolation uint bin : TEXCOORD0;
    float4 clip : SV_Position;
}

static const uint TileSize = 8u;

// Resolve bins, and where in tileCounts the classifying groups count themselves (Core/Compute's IsLastGroup).
static const uint ResolveBins = 256u;
static const uint TileGroupsDone = ResolveBins;

static const float2 QuadCorners[6] = { float2(0.0, 0.0), float2(1.0, 0.0), float2(1.0, 1.0), float2(0.0, 0.0), float2(1.0, 1.0), float2(0.0, 1.0) };

groupshared uint BinMask[8];

// The tiles across and down the view, all of them.
uint GetTileCount()
{
    return ((uint)View.viewSize.x + TileSize - 1u) / TileSize * (((uint)View.viewSize.y + TileSize - 1u) / TileSize);
}

// Where a piece's corner is, relative to the camera, and its triangle's name; the rest of what DrawPieces hands on is
// left out of a place.
PieceVaryings GetPieceCorner(uint vertex, uint instance, MaterialConstants c, bool isPlaceOnly)
{
    // The draw's first instance is its bin's first piece, which the instance's number counts from.
    uint3 piece = c.pieces[instance];
    GpuInstance placed = World.instances[piece.x];
    uint face = vertex / 3u;
    uint corner = vertex % 3u;
    corner = placed.isMirrored && corner != 0u ? 3u - corner : corner;
    bool isCard = (piece.y & CardBit) != 0u;
    float3 position;
    float3 normal = float3(0.0, 1.0, 0.0);
    float4 tangent = float4(0.0, 0.0, 0.0, 0.0);
    float2 uv = float2(0.0, 0.0);
    float4 view = float4(0.0, 0.0, 0.0, 0.0);
    float3 front = float3(0.0, 0.0, 0.0);
    uint2 atlases = uint2(TextureNone.id, TextureNone.id);
    uint slot;
    bool isPast = false;
    [branch] if (isCard)
    {
        GpuCard card = World.cards[piece.y & ~CardBit];
        CardCorner quad = GetCardCorner(placed, card, CardCorners[min(face * 3u + corner, 5u)]);
        position = quad.position;
        normal = quad.normal;
        uv = quad.uv;
        view = quad.view;
        front = quad.front;
        atlases = uint2(card.surface.id, card.normals.id);
        slot = card.slot;
        isPast = face >= 2u;
    }
    else
    {
        GpuCluster cluster = World.clusters[piece.y >> 2];
        uint index = (piece.y & 3u) * PieceTriangles + face;
        isPast = index >= cluster.triangleCount;
        PieceVertex corners = cluster.LoadVertex(cluster.LoadTriangle(min(index, cluster.triangleCount - 1u))[corner]);
        position = placed.ToCameraRelative(corners.position);
        normal = placed.ToWorldNormal(corners.normal);
        tangent = float4(NormalizeOrZero(placed.ToWorldDirection(corners.tangent.xyz)), corners.tangent.w);
        uv = corners.uv;
        slot = cluster.slot;
    }

    PieceVaryings output;
    output.clip = isPast ? float4(asfloat(0x7FC00000u).xxx, 1.0) : View.ToClip(position);
    output.id = ((piece.z << 5) | face) + 1u;
    [branch] if (isPlaceOnly)
    {
        return output;
    }

    GpuModel model = World.models[placed.model];
    float takeover = GetCardTakeover(model, placed, length(placed.ToCameraRelative(model.bounds.xyz)));
    output.position = position;
    output.normal = normal;
    output.tangent = tangent;
    output.uv = uv;
    output.material = placed.GetMaterial(slot);
    output.flags = placed.flags;
    output.card = view;
    output.dissolve = GetDissolve(placed, takeover, isCard);
    output.front = front;
    output.atlases = atlases;
    return output;
}

[shader("vertex")]
PieceVaryings DrawPieces(
    uint vertex : SV_VulkanVertexID,
    uint instance : SV_VulkanInstanceID,
    uniform PassHeader pass,
    uniform MaterialConstants c)
{
    BeginPass(pass);
    return GetPieceCorner(vertex, instance, c, false);
}

[shader("vertex")]
PlaceVaryings DrawPiecePlaces(
    uint vertex : SV_VulkanVertexID,
    uint instance : SV_VulkanInstanceID,
    uniform PassHeader pass,
    uniform MaterialConstants c)
{
    BeginPass(pass);
    PieceVaryings corner = GetPieceCorner(vertex, instance, c, true);
    PlaceVaryings output;
    output.id = corner.id;
    output.clip = corner.clip;
    return output;
}

[shader("fragment")]
uint WriteVisibility(PlaceVaryings input) : SV_Target0
{
    return input.id;
}

[shader("fragment")]
uint WriteClippedVisibility<S : ISurface>(
    PieceVaryings input,
    bool isFrontFace : SV_IsFrontFace,
    out float depth : SV_DepthLessEqual,
    uniform PassHeader pass,
    uniform MaterialConstants c) : SV_Target0
{
    BeginPass(pass);
    uint bins = World.materialBins[input.material];
    PieceSurfaceInputs piece = CreateSurfaceInputs(
        input,
        ddx(input.uv),
        ddy(input.uv),
        ddx(input.position),
        ddy(input.position),
        isFrontFace,
        (bins & BinDoubleSided) != 0u);
    Surface surface = World.LoadMaterial<S>(input.material).Evaluate(piece.inputs);
    float keep = min(piece.cover - 0.5, GetDissolveClip(input.dissolve, input.clip.xy));
    keep = (bins & BinMasked) != 0u ? min(keep, surface.alpha - surface.alphaCutoff) : keep;
    clip(keep);
    depth = piece.depth;
    return input.id;
}

// Writes each resolve bin's draw, four bins a lane of the classifying pass's last group: a quad, six indices, over each
// tile it gave the bin, from the bin's run of the tile list; and empties the bin's count for the next frame.
void WriteResolveArgs(ClassifyConstants c, uint lane)
{
    uint capacity = c.tileList.count;
    uint tiles = GetTileCount();
    [unroll] for (uint i = 0u; i < 4u; i++)
    {
        uint bin = lane * 4u + i;
        uint first = min(bin * tiles, capacity);
        IndexedDrawArgs draw;
        draw.indices = 6u;
        draw.instances = min(c.tileCounts.LoadAtomic(bin), capacity - first);
        draw.firstIndex = 0u;
        draw.vertexOffset = 0;
        draw.firstInstance = first;
        c.resolveArgs[bin] = draw;
        c.tileCounts[bin] = 0u;
    }
}

// Sorts the view's tiles by resolve bin, a group a tile: each pixel's triangle (the visibility buffer) names its piece,
// its instance and its cluster's or card's material slot, whose row names its resolve bin. Each bin the tile holds is
// noted once, and the tile added to that bin's run of the tile list. A tile's entry is its place, 12 bits each way, and
// its bin in the top 8. The last group writes the resolve's draws.
[shader("compute")]
[numthreads(8, 8, 1)]
void ClassifyTiles(
    uint3 tile : SV_GroupID,
    uint3 id : SV_DispatchThreadID,
    uint thread : SV_GroupIndex,
    uniform PassHeader pass,
    uniform ClassifyConstants c)
{
    BeginPass(pass);
    if (thread < 8u)
    {
        BinMask[thread] = 0u;
    }

    GroupMemoryBarrierWithGroupSync();
    uint visible = all(id.xy < (uint2)View.viewSize) ? c.visibility.Load(int2(id.xy), 0) : 0u;
    if (visible != 0u)
    {
        uint3 piece = c.pieces[(visible - 1u) >> 5];
        GpuInstance instance = World.instances[piece.x];
        uint slot = (piece.y & CardBit) != 0u ? World.cards[piece.y & ~CardBit].slot : World.clusters[piece.y >> 2].slot;
        uint material = instance.GetMaterial(slot);

        // A row past the materials reads nothing meant: raised, and binned with the first.
        bool isInRange = material < World.materialBins.count;
        [branch] if (!isInRange)
        {
            RaiseFailure(World.failures, FailureCode.MaterialRange);
        }

        uint bin = isInRange ? (World.materialBins[material] >> 16) & 0xFFu : 0u;
        InterlockedOr(BinMask[bin >> 5], 1u << (bin & 31u));
    }

    GroupMemoryBarrierWithGroupSync();
    uint tiles = GetTileCount();
    [unroll] for (uint i = 0u; i < 4u; i++)
    {
        uint bin = thread * 4u + i;
        if ((BinMask[bin >> 5] & (1u << (bin & 31u))) == 0u)
        {
            continue;
        }

        uint at = c.tileCounts.InterlockedAdd(bin, 1u);
        if (bin * tiles + at < c.tileList.count)
        {
            c.tileList[bin * tiles + at] = tile.x | (tile.y << 12) | (bin << 24);
        }
    }

    if (IsLastGroup(c.tileCounts, TileGroupsDone, thread))
    {
        WriteResolveArgs(c, thread);
    }
}

// Where a point of the view (ndc) falls on the triangle whose corners are a, b and c as clip (x, y, w): the weights of
// its corners, each the corner's share of the 2D homogeneous coordinates' inverse, normalised.
float3 GetBarycentrics(float3 a, float3 b, float3 c, float2 ndc)
{
    float3 at = float3(ndc, 1.0);
    float3 weights = float3(dot(cross(b, c), at), dot(cross(c, a), at), dot(cross(a, b), at));
    return weights / (weights.x + weights.y + weights.z);
}

[shader("vertex")]
TileVaryings DrawTiles(
    uint vertex : SV_VulkanVertexID,
    uint instance : SV_VulkanInstanceID,
    uniform PassHeader pass,
    uniform MaterialConstants c)
{
    BeginPass(pass);

    // The draw's first instance is its bin's first tile, which the instance's number counts from.
    uint tile = c.tiles[instance];
    float2 place = float2(tile & 0xFFFu, (tile >> 12) & 0xFFFu);
    float2 pixel = min((place + QuadCorners[vertex % 6u]) * 8.0, View.viewSize);
    TileVaryings output;
    output.bin = tile >> 24;
    output.clip = float4(pixel.x * View.viewTexel.x * 2.0 - 1.0, 1.0 - pixel.y * View.viewTexel.y * 2.0, 0.0, 1.0);
    return output;
}

[shader("fragment")]
GBufferTargets ResolveSurfaces<S : ISurface>(TileVaryings input, uniform PassHeader pass, uniform MaterialConstants c)
{
    BeginPass(pass);
    uint2 pixel = (uint2)input.clip.xy;
    uint id = c.visibility.Load(int2(pixel), 0);
    if (id == 0u)
    {
        discard;
    }

    uint3 piece = c.pieces[(id - 1u) >> 5];
    uint face = (id - 1u) & 31u;
    GpuInstance instance = World.instances[piece.x];
    bool isCard = (piece.y & CardBit) != 0u;
    GpuCard card = World.cards[isCard ? piece.y & ~CardBit : 0u];
    GpuCluster cluster = World.clusters[isCard ? 0u : piece.y >> 2];
    uint slot = isCard ? card.slot : cluster.slot;
    uint material = instance.GetMaterial(slot);
    uint bins = World.materialBins[material];
    if (((bins >> 16) & 0xFFu) != input.bin)
    {
        discard;
    }

    // The triangle's corners in the order the vertex stage drew them, a mirrored instance's second and third swapped.
    float3 positions[3];
    float3 normals[3];
    float4 tangents[3];
    float2 uvs[3];
    float3 places[3];
    float4 view = float4(0.0, 0.0, 0.0, 0.0);
    float3 front = float3(0.0, 0.0, 0.0);
    uint3 corners = isCard ? uint3(0u, 0u, 0u) : cluster.LoadTriangle((piece.y & 3u) * PieceTriangles + face);
    [unroll] for (uint k = 0u; k < 3u; k++)
    {
        uint corner = instance.isMirrored && k != 0u ? 3u - k : k;
        [branch] if (isCard)
        {
            CardCorner quad = GetCardCorner(instance, card, CardCorners[face * 3u + corner]);
            positions[k] = quad.position;
            normals[k] = quad.normal;
            uvs[k] = quad.uv;
            view = quad.view;
            front = quad.front;
            tangents[k] = float4(0.0, 0.0, 0.0, 0.0);
            places[k] = card.centre;
        }
        else
        {
            PieceVertex vertex = cluster.LoadVertex(corners[corner]);
            positions[k] = instance.ToCameraRelative(vertex.position);
            normals[k] = instance.ToWorldNormal(vertex.normal);
            tangents[k] = float4(NormalizeOrZero(instance.ToWorldDirection(vertex.tangent.xyz)), vertex.tangent.w);
            uvs[k] = vertex.uv;
            places[k] = vertex.position;
        }
    }

    float4 clip0 = View.ToClip(positions[0]);
    float4 clip1 = View.ToClip(positions[1]);
    float4 clip2 = View.ToClip(positions[2]);
    float2 ndc = float2((pixel.x + 0.5) * View.viewTexel.x * 2.0 - 1.0, 1.0 - (pixel.y + 0.5) * View.viewTexel.y * 2.0);
    float3 here = GetBarycentrics(clip0.xyw, clip1.xyw, clip2.xyw, ndc);
    float3 across = GetBarycentrics(clip0.xyw, clip1.xyw, clip2.xyw, ndc + float2(2.0 * View.viewTexel.x, 0.0));
    float3 down = GetBarycentrics(clip0.xyw, clip1.xyw, clip2.xyw, ndc - float2(0.0, 2.0 * View.viewTexel.y));
    float2 uv = here.x * uvs[0] + here.y * uvs[1] + here.z * uvs[2];

    // Clockwise on screen is front, and the screen's y runs down where clip space's runs up: a front triangle's area in
    // clip space, counter-clockwise positive, is positive.
    float2 screen0 = clip0.xy / clip0.w;
    float2 screen1 = clip1.xy / clip1.w;
    float2 screen2 = clip2.xy / clip2.w;
    float2 side1 = screen1 - screen0;
    float2 side2 = screen2 - screen0;
    bool isFrontFace = (side1.x * side2.y - side1.y * side2.x) * clip0.w * clip1.w * clip2.w > 0.0;

    PieceVaryings varyings;
    varyings.position = here.x * positions[0] + here.y * positions[1] + here.z * positions[2];
    varyings.normal = here.x * normals[0] + here.y * normals[1] + here.z * normals[2];
    varyings.tangent = float4(here.x * tangents[0].xyz + here.y * tangents[1].xyz + here.z * tangents[2].xyz, tangents[0].w);
    varyings.uv = uv;
    varyings.material = material;
    varyings.flags = instance.flags;
    varyings.card = view;
    varyings.dissolve = float2(1.0, 0.0);
    varyings.front = front;
    varyings.atlases = uint2(card.surface.id, card.normals.id);
    varyings.id = id;
    varyings.clip = float4(pixel + 0.5, 0.0, 1.0);
    float2 uvDdx = (across.x * uvs[0] + across.y * uvs[1] + across.z * uvs[2]) - uv;
    float2 uvDdy = (down.x * uvs[0] + down.y * uvs[1] + down.z * uvs[2]) - uv;
    float3 positionDdx = (across.x * positions[0] + across.y * positions[1] + across.z * positions[2]) - varyings.position;
    float3 positionDdy = (down.x * positions[0] + down.y * positions[1] + down.z * positions[2]) - varyings.position;

    // The normal's turn across the pixel, over the footprint its textures are read at.
    float3 normal = NormalizeOrZero(varyings.normal);
    float3 normalAcross = NormalizeOrZero(across.x * normals[0] + across.y * normals[1] + across.z * normals[2]);
    float3 normalDown = NormalizeOrZero(down.x * normals[0] + down.y * normals[1] + down.z * normals[2]);
    float3 normalDdx = (normalAcross - normal) * View.textureMipScale;
    float3 normalDdy = (normalDown - normal) * View.textureMipScale;
    PieceSurfaceInputs inputs = CreateSurfaceInputs(
        varyings,
        uvDdx,
        uvDdy,
        positionDdx,
        positionDdy,
        isFrontFace,
        (bins & BinDoubleSided) != 0u);
    Surface surface = World.LoadMaterial<S>(material).Evaluate(inputs.inputs);

    GBufferSurface stored;
    stored.baseColor = surface.baseColor;
    stored.occlusion = surface.occlusion;
    stored.normal = NormalizeOrZero(surface.normal);
    stored.roughness = GetFilteredRoughness(surface.roughness, normalDdx, normalDdy);
    stored.model = surface.shadingModel;
    stored.metallic = surface.metallic;
    stored.specular = surface.specular;
    stored.modelData = surface.modelData;

    // A card moves as its centre does; a cluster's point as its instance moved it.
    float3 place = here.x * places[0] + here.y * places[1] + here.z * places[2];
    float3 last = isCard
        ? varyings.position + instance.ToLastCameraRelative(card.centre) - instance.ToCameraRelative(card.centre)
        : instance.ToLastCameraRelative(place);
    float4 lastClip = View.ToLastClip(last);
    float2 lastNdc = lastClip.xy / max(lastClip.w, 1e-6);

    return EncodeGBuffer(stored, surface.emissive, (ndc - View.jitter - lastNdc) * float2(0.5, -0.5));
}
