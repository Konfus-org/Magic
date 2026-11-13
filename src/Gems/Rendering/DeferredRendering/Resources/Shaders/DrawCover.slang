// One colour over the whole window, as World.CoverOpacity hides it during a load. The pass header holds the colour,
// encoded for the screen, in x, y and z, and the opacity in w, each as a float's bits. The colour is premultiplied by
// the opacity, for the alpha blend.
module "DrawCover";

import Core.Pass;

[shader("vertex")]
float4 DrawCoverVertex(uint index : SV_VertexID) : SV_Position
{
    float2 corner = float2(float((index << 1u) & 2u), float(index & 2u));
    return float4(corner * 2.0 - 1.0, 0.0, 1.0);
}

[shader("pixel")]
float4 DrawCoverPixel() : SV_Target0
{
    PassHeader header = GetPassHeader();
    float opacity = asfloat(header.w);
    return float4(asfloat(uint3(header.x, header.y, header.z)) * opacity, opacity);
}
