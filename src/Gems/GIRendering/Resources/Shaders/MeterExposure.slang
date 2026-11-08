// The exposure, metered from a histogram of the picture's log luminance at half its size and adapted to over time, as
// an eye adapts (Tardif, "Histogram-Based Luminance", 2019). Each group counts its pixels into bins, then the last
// group to finish takes the histogram's weighted average, moves the luminance it adapted to towards it and writes the
// exposure that brings that luminance to the key, with the stops of compensation added. Black pixels are left out of
// the average. Adapted from Wicked Engine's luminance passes, over a range wide enough for light in physical units.
// The pass header's y is the seconds since the last frame and z the stops of compensation, both as floats' bits.
module "MeterExposure";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Core.Color;

// The meter's layout in bytes: the exposure, which GetExposure reads, the luminance adapted to, 0 for none yet, the
// groups that have counted this frame, and the histogram's bins, which the last group empties again.
static const uint ExposureOffset = 0u;
static const uint LuminanceOffset = 4u;
static const uint GroupsOffset = 8u;
static const uint HistogramOffset = 12u;

static const uint GroupSide = 32u;
static const uint BinCount = GroupSide * GroupSide;

// The log2 luminance the bins span: bin 0 is black, and the rest spread evenly from the least to the most.
static const float LeastLog = -10.0;
static const float MostLog = 20.0;

// What the average luminance is brought to, and how fast the adapted luminance follows it, per second.
static const float Key = 0.115;
static const float AdaptionRate = 1.0;

groupshared uint groupBins[BinCount];
groupshared float weightedBins[BinCount];
groupshared bool isLastGroup;

// The bin a pixel of the half-size picture falls in: the average of the four it covers, 0 when black.
uint GetBin(FrameConstants frame, uint2 pixel)
{
    TextureRef picture = { frame.hdr };
    float3 color = picture.Load(pixel * 2u, 0u).rgb;
    color += picture.Load(pixel * 2u + uint2(1u, 0u), 0u).rgb;
    color += picture.Load(pixel * 2u + uint2(0u, 1u), 0u).rgb;
    color += picture.Load(pixel * 2u + 1u, 0u).rgb;
    float luminance = GetLuminance(color * 0.25);
    if (luminance <= 0.001)
        return 0u;

    float logLuminance = saturate((log2(luminance) - LeastLog) / (MostLog - LeastLog));
    return uint(logLuminance * float(BinCount - 2u) + 1.0);
}

[shader("compute")]
[numthreads(GroupSide, GroupSide, 1)]
void MeterExposure(uint3 thread : SV_DispatchThreadID, uint groupIndex : SV_GroupIndex)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    BufferRef meter = { frame.exposureMeter };
    uint2 size = uint2(frame.width, frame.height) / 2u;
    uint binOffset = HistogramOffset + groupIndex * 4u;

    // The group's pixels counted in shared memory, where the atomics contend less, then added to the histogram.
    groupBins[groupIndex] = 0u;
    GroupMemoryBarrierWithGroupSync();
    if (all(thread.xy < size))
        InterlockedAdd(groupBins[GetBin(frame, thread.xy)], 1u);

    GroupMemoryBarrierWithGroupSync();
    meter.InterlockedAdd(binOffset, groupBins[groupIndex]);

    // The last group to count, once every other group's counts are in, reads the histogram.
    DeviceMemoryBarrierWithGroupSync();
    if (groupIndex == 0u)
    {
        uint2 groups = (size + GroupSide - 1u) / GroupSide;
        isLastGroup = meter.InterlockedAdd(GroupsOffset, 1u) + 1u == groups.x * groups.y;
    }

    GroupMemoryBarrierWithGroupSync();
    if (!isLastGroup)
        return;

    // Each bin weighted by its index and summed, the histogram emptied for the next frame as it is read.
    uint count = meter.InterlockedAdd(binOffset, 0u);
    meter.Store<uint>(binOffset, 0u);
    weightedBins[groupIndex] = float(count) * float(groupIndex);
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = BinCount / 2u; stride > 0u; stride /= 2u)
    {
        if (groupIndex < stride)
            weightedBins[groupIndex] += weightedBins[groupIndex + stride];

        GroupMemoryBarrierWithGroupSync();
    }

    if (groupIndex != 0u)
        return;

    // The average bin over the pixels that aren't black, which are bin 0's count, back to a luminance; the adapted
    // luminance moved towards it, or set to it on the first reading.
    meter.Store<uint>(GroupsOffset, 0u);
    float averageBin = weightedBins[0] / max(float(size.x * size.y) - float(count), 1.0) - 1.0;
    float average = exp2(averageBin / float(BinCount - 2u) * (MostLog - LeastLog) + LeastLog);
    float adapted = meter.Load<float>(LuminanceOffset);
    adapted = adapted > 0.0 ? lerp(adapted, average, 1.0 - exp(-asfloat(header.y) * AdaptionRate)) : average;
    meter.Store<float>(LuminanceOffset, adapted);
    meter.Store<float>(ExposureOffset, Key / adapted * exp2(asfloat(header.z)));
}
