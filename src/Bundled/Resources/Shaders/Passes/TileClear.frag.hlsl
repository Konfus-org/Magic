// The fragment stage of a tiled draw's clear: nothing, as the rasteriser writes the depth 0 Passes/Fullscreen.vert.hlsl
// puts everywhere, the farthest. It takes the fullscreen triangle's varyings, so the two stages link on every API.

#include "Include/Fullscreen.hlsli"

void main(PassVaryings input)
{
}
