// Clears one region of the shadow atlas: a triangle over the whole viewport at depth 0 (reverse-Z: nothing drawn),
// with the scissor set to the region and the pipeline's depth test Always. There is no partial clear of a
// texture, and the atlas is loaded, not cleared, on a frame where some of its tiles keep last frame's depth.

float4 main(uint vertexId : SV_VertexID) : SV_Position
{
    float2 corner = float2((vertexId << 1) & 2, vertexId & 2);
    return float4(corner * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}
