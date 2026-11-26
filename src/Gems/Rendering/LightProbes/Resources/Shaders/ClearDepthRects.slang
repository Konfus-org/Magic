// Clears rectangles of a depth atlas to no depth, a draw a rectangle: a triangle over the whole target that the draw's
// scissor keeps to the rectangle, whose depth is written over what the target holds.
module "ClearDepthRects";

struct Varyings
{
    float4 position : SV_Position;
};

[shader("vertex")]
Varyings ClearDepthRectVertex(uint vertex : SV_VertexID)
{
    float2 corner = float2(float((vertex << 1u) & 2u), float(vertex & 2u));
    Varyings output;
    output.position = float4(corner * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

[shader("pixel")]
float ClearDepthRectPixel(Varyings input) : SV_Depth
{
    return 0.0;
}
