// What the post passes share, C#'s PostConstants: the picture after the panes, the resolved pictures of this frame
// and the last, the histogram and the exposure it meters, the bloom and the camera's settings. The constants' heap
// id is the lighting constants' post.
module "Scene/Post";

import Core.Heap;
import Core.Pass;
import Scene.Lighting;

public static const uint PostHistogramBins = 256u;
// The log2 of the luminance the first bin starts at and the last ends at.
public static const float PostHistogramLowEv = -12.0;
public static const float PostHistogramHighEv = 12.0;

public struct PostConstants
{
    // Heap ids.
    public uint picture;
    public uint history;
    public uint historyPrevious;
    public uint histogram;
    public uint exposureState;
    public uint bloom;
    public uint sampler;
    public uint bloomLevels;
    // What the camera asks: stops added to the metered exposure, how much of the bloom is added, and the least and
    // most the meter may scale by.
    public float exposureCompensation;
    public float bloomStrength;
    public float exposureMin;
    public float exposureMax;
    // Seconds the run has taken.
    public float time;
    public float padding0;
    public float padding1;
    public float padding2;
}

public PostConstants GetPost() { return LoadConstants<PostConstants>(GetLighting().post); }

// The histogram's bin for a luminance.
public uint GetHistogramBin(float luminance)
{
    float ev = log2(max(luminance, 1e-6));
    float t = saturate((ev - PostHistogramLowEv) / (PostHistogramHighEv - PostHistogramLowEv));
    return min(uint(t * float(PostHistogramBins)), PostHistogramBins - 1u);
}

// The log2 of the luminance at the middle of a bin.
public float GetHistogramLuminance(uint bin)
{
    return PostHistogramLowEv + (float(bin) + 0.5) / float(PostHistogramBins) * (PostHistogramHighEv - PostHistogramLowEv);
}
