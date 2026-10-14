// Empties the view's light tiles' masks for the lights to set their bits in, four words a thread.

#include "Include/LightCulling.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    uint word = thread.x * 4u;
    if (word < CountOf(LightTiles, 4u))
    {
        LightTiles.Store4(word * 4u, uint4(0u, 0u, 0u, 0u));
    }
}
