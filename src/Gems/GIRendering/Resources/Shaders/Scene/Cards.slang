// The card table: six rows per model and set of materials captured, C#'s GpuCard, one a side of the model's box,
// each the model drawn orthographically along that axis into the card atlases: albedo with coverage in its alpha,
// the normal in the model's frame, and the depth across the box. A capture row r owns cards 6r to 6r + 5, so an
// instance's capture names its cards.
module "Scene/Cards";

import Core.Heap;
import Core.Pack;

public static const uint CardsPerCapture = 6u;
public static const uint CardAtlasTexels = 2048u;
public static const uint CardMaxTexels = 128u;

public struct GpuCard
{
    // The card's texels in the atlases, from its top left.
    public uint originX;
    public uint originY;
    public uint width;
    public uint height;
    // The centre of the model's box, in the model's frame.
    public float3 center;
    // Which side: 0 to 5 for +X, -X, +Y, -Y, +Z, -Z.
    public uint axis;
    // The box's half extents along the model's axes.
    public float3 extents;
    public uint padding;
}

public GpuCard LoadCard(uint table, uint index)
{
    ArrayRef<GpuCard> cards = { table };
    return cards[index];
}

public uint2 GetCardOrigin(GpuCard card) { return uint2(card.originX, card.originY); }

public uint2 GetCardSize(GpuCard card) { return uint2(card.width, card.height); }

// The direction a card's side was seen from: towards the viewer, along the model's axis.
public float3 GetCardDirection(uint axis)
{
    float sign = (axis & 1u) != 0u ? -1.0 : 1.0;
    uint which = axis >> 1u;
    return float3(which == 0u ? sign : 0.0, which == 1u ? sign : 0.0, which == 2u ? sign : 0.0);
}

// A card's frame: the direction it was seen from, the axes across and up its texels as the renderer set its view
// up, and the box's half extents along each, at least a centimetre so a flat model still has a depth to its card.
public struct CardFrame
{
    public float3 direction;
    public float3 right;
    public float3 up;
    public float3 extents;
}

public CardFrame GetCardFrame(GpuCard card)
{
    CardFrame frame;
    frame.direction = GetCardDirection(card.axis);
    float3 worldUp = abs(frame.direction.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    frame.right = normalize(cross(worldUp, -frame.direction));
    frame.up = cross(-frame.direction, frame.right);
    frame.extents = max(
        float3(abs(dot(card.extents, frame.right)), abs(dot(card.extents, frame.up)), abs(dot(card.extents, frame.direction))),
        0.01
    );
    return frame;
}

// Where a point of the model lands in a card: its texture coordinates from the card's top left, 0 to 1 across the
// card, and the depth the capture holds there, 1 at the side seen from and 0 at the far side.
public float2 ProjectIntoCard(GpuCard card, float3 modelPoint, out float depth)
{
    CardFrame frame = GetCardFrame(card);
    float3 offset = modelPoint - card.center;
    depth = 0.5 + dot(offset, frame.direction) / (2.0 * frame.extents.z);
    return float2(dot(offset, frame.right) / (2.0 * frame.extents.x) + 0.5, 0.5 - dot(offset, frame.up) / (2.0 * frame.extents.y));
}

// The point of the model a card's texture coordinates and depth name: the inverse of ProjectIntoCard.
public float3 UnprojectFromCard(GpuCard card, float2 uv, float depth)
{
    CardFrame frame = GetCardFrame(card);
    return card.center + frame.right * ((uv.x * 2.0 - 1.0) * frame.extents.x) + frame.up * ((1.0 - uv.y * 2.0) * frame.extents.y)
        + frame.direction * ((depth - 0.5) * 2.0 * frame.extents.z);
}

// Metres a card's texel covers, across the card.
public float GetCardTexelWorld(GpuCard card)
{
    float3 extents = GetCardFrame(card).extents;
    return 2.0 * max(extents.x / float(card.width), extents.y / float(card.height));
}

// Metres a card's depth runs across, from its far side to the side it was seen from.
public float GetCardDepthRange(GpuCard card) { return 2.0 * GetCardFrame(card).extents.z; }

// The atlas texture coordinates of a point of a card, kept half a texel inside it, so a bilinear read never
// crosses into the card beside it.
public float2 GetCardAtlasUv(GpuCard card, float2 uv)
{
    float2 size = float2(GetCardSize(card));
    float2 texel = clamp(uv * size, float2(0.5), size - 0.5);
    return (float2(GetCardOrigin(card)) + texel) / float(CardAtlasTexels);
}
