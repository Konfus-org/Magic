// The shadow pass's fragment stage: nothing. The pipeline has no colour target, the rasteriser writes the depth,
// and a stage SDL insists on is this empty one. No constants either: the CPU pushes the frame block to the vertex
// stage only.

void main(float4 position : SV_Position)
{
}
