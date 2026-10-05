// Starts every refreshed shadow view's draw args for the frame: one slice of the args buffer per slice of the
// refreshed list (the header says how many there are), each group's command copied from the bucket template
// with no instances yet, its first instance moved into the slice's run of the visible-id buffer
// (visibleHighWater entries each), so the cull of each refreshed view appends into its own run and the draw of
// each slice reads its own. A slice past this frame's refreshed rows keeps its zero instance counts and draws
// nothing. One thread per bucket group, looping over the slices.

#include "Include/Frame.hlsli"
#include "Include/ShadowViews.hlsli"

StructuredBuffer<GpuDrawArgs> DrawTemplate : READ(0);
StructuredBuffer<GpuCounts> Counts : READ(1);
StructuredBuffer<GpuShadowHeader> ShadowHeader : READ(2);
RWStructuredBuffer<GpuDrawArgs> ShadowDrawArgs : WRITE(0);

[numthreads(64, 1, 1)]
void main(uint3 threadId : SV_DispatchThreadID)
{
    GpuCounts counts = Counts[0];
    uint group = threadId.x;
    if (group >= counts.groupCount)
        return;

    GpuDrawArgs source = DrawTemplate[group];
    uint slices = min(ShadowHeader[0].slots.z, ShadowMaxRows);
    [loop] for (uint slice = 0u; slice < slices; slice++)
    {
        GpuDrawArgs args = source;
        args.instanceCount = 0u;
        args.firstInstance = source.firstInstance + slice * counts.visibleHighWater;
        ShadowDrawArgs[slice * counts.groupCount + group] = args;
    }
}
