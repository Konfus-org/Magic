// Which probes light anything this frame, a thread a probe of every level (Lighting/Gi.hlsli's GiIsUsed): one that does
// not is not traced, and counts for nothing. One not used, or not yet traced since its slot last held another probe or
// none, has its irradiance blanked, counting for nothing until it is, rather than lighting with what it, or the probe
// before it, saw.

#include "Lighting/Gi.hlsli"

[numthreads(64, 1, 1)]
void main(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= GiProbes * GiProbes * GiProbes * GiHeader().levels)
    {
        return;
    }

    uint index;
    GpuGiLevel level;
    int3 probe = GiProbeOf(thread.x, index, level);
    int4 state = LOAD(GiProbeState, int4, thread.x);
    bool isUsed = GiIsUsed(level, index, probe);
    bool isHeld = isUsed && (state.w & GiProbeHeld) != 0 && all(state.xyz == probe);
    STORE(GiProbeState, int4, thread.x, int4(probe, (isUsed ? GiProbeUsed : 0) | (isHeld ? GiProbeHeld : 0)));
    if (isHeld)
    {
        return;
    }

    uint2 corner = GiTile(probe, index) * GiIrradianceSide;
    [loop] for (uint texel = 0u; texel < GiIrradianceSide * GiIrradianceSide; texel++)
    {
        GiIrradiance[corner + uint2(texel % GiIrradianceSide, texel / GiIrradianceSide)] = float4(0.0, 0.0, 0.0, 0.0);
    }
}
