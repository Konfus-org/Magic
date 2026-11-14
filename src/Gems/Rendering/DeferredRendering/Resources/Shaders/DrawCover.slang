// One colour over the whole window, as World.CoverOpacity hides it during a load. The pass header holds the colour as
// the screen shows it in x, y and z, and the opacity in w, each as a float's bits. The window is sRGB, so it blends
// linear colour: the pixel shader decodes the colour, and gives the blend the opacity that makes the fade even to the
// eye. The colour is premultiplied by that opacity, for the alpha blend.
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
    float3 shown = asfloat(uint3(header.x, header.y, header.z));
    float opacity = asfloat(header.w);

    // A blend of what the screen shows is a linear blend at a power of the opacity: over a black picture
    // opacity^2.2, and for a black cover 1 - (1 - opacity)^2.2 over any picture. The cover is mostly over the
    // opposite of its colour, so the bright cover takes the first and the black cover the second.
    float bright = max(shown.r, max(shown.g, shown.b));
    float blend = lerp(1.0 - pow(1.0 - opacity, 2.2), pow(opacity, 2.2), bright);
    return float4(pow(shown, 2.2) * blend, blend);
}
