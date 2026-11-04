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
    public uint2 origin;
    public uint2 size;
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

// The direction a card's side was seen from: towards the viewer, along the model's axis.
public float3 GetCardDirection(uint axis)
{
    float sign = (axis & 1u) != 0u ? -1.0 : 1.0;
    uint which = axis >> 1u;
    return float3(which == 0u ? sign : 0.0, which == 1u ? sign : 0.0, which == 2u ? sign : 0.0);
}

// The axes of a card's view, as the renderer sets them up: towards the viewer along direction, the view looking
// back along it.
public void GetCardBasis(float3 direction, out float3 right, out float3 up, out float3 forward)
{
    float3 worldUp = abs(direction.y) > 0.9 ? float3(0.0, 0.0, 1.0) : float3(0.0, 1.0, 0.0);
    forward = -direction;
    right = normalize(cross(worldUp, forward));
    up = cross(forward, right);
}

// The box's half extents in the card's frame: across, up and along the direction it was seen from, each at least a
// centimetre so a flat model still has a depth to its card.
public float3 GetCardExtents(GpuCard card, float3 right, float3 up, float3 direction)
{
    float3 extents = float3(abs(dot(card.extents, right)), abs(dot(card.extents, up)), abs(dot(card.extents, direction)));
    return max(extents, 0.01);
}

// Where a point of the model lands in a card: its texture coordinates from the card's top left, 0 to 1 across the
// card, and the depth the capture holds there, 1 at the side seen from and 0 at the far side.
public float2 ProjectIntoCard(GpuCard card, float3 modelPoint, out float depth)
{
    float3 direction = GetCardDirection(card.axis);
    float3 right;
    float3 up;
    float3 forward;
    GetCardBasis(direction, right, up, forward);
    float3 extents = GetCardExtents(card, right, up, direction);
    float3 offset = modelPoint - card.center;
    depth = 0.5 + dot(offset, direction) / (2.0 * extents.z);
    return float2(dot(offset, right) / (2.0 * extents.x) + 0.5, 0.5 - dot(offset, up) / (2.0 * extents.y));
}

// The point of the model a card's texture coordinates and depth name: the inverse of ProjectIntoCard.
public float3 UnprojectFromCard(GpuCard card, float2 uv, float depth)
{
    float3 direction = GetCardDirection(card.axis);
    float3 right;
    float3 up;
    float3 forward;
    GetCardBasis(direction, right, up, forward);
    float3 extents = GetCardExtents(card, right, up, direction);
    return card.center + right * ((uv.x * 2.0 - 1.0) * extents.x) + up * ((1.0 - uv.y * 2.0) * extents.y) + direction * ((depth - 0.5) * 2.0 * extents.z);
}

// Metres a card's texel covers, across the card.
public float GetCardTexelWorld(GpuCard card)
{
    float3 direction = GetCardDirection(card.axis);
    float3 right;
    float3 up;
    float3 forward;
    GetCardBasis(direction, right, up, forward);
    float3 extents = GetCardExtents(card, right, up, direction);
    return 2.0 * max(extents.x / float(card.size.x), extents.y / float(card.size.y));
}

// Metres a card's depth runs across, from its far side to the side it was seen from.
public float GetCardDepthRange(GpuCard card)
{
    float3 direction = GetCardDirection(card.axis);
    float3 right;
    float3 up;
    float3 forward;
    GetCardBasis(direction, right, up, forward);
    return 2.0 * GetCardExtents(card, right, up, direction).z;
}

// The atlas texture coordinates of a point of a card, kept half a texel inside it, so a bilinear read never
// crosses into the card beside it.
public float2 GetCardAtlasUv(GpuCard card, float2 uv)
{
    float2 texel = clamp(uv * float2(card.size), float2(0.5), float2(card.size) - 0.5);
    return (float2(card.origin) + texel) / float(CardAtlasTexels);
}
