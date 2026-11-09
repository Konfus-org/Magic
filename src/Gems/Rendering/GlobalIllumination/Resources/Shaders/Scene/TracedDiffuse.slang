// The traced diffuse light: what a white diffuse surface reflects of the light that reaches it indirectly, its
// irradiance over pi, traced at half the picture's size, filtered and blended over frames, and how the picture reads it
// back at its own size. C#'s TracedDiffuseConstants is the first row of the GI buffer, the pass header's y.
module "Scene/TracedDiffuse";

import Core.Heap;
import Core.Pass;

public struct TracedDiffuseConstants
{
    // The rays' light at each traced pixel.
    public uint traced;
    // The rays' light filtered over the neighbours, with its brightness's variance in alpha.
    public uint filtered;
    // This frame's light blended with the frames before, with its variance in alpha, and the view depth of each of its
    // pixels; and the last frame's of both, which this frame's blends with.
    public uint light;
    public uint depth;
    public uint lightHistory;
    public uint depthHistory;
    // Linear, clamped to the edges.
    public uint sampler;
    // 0 while the history holds nothing of the view: this frame starts it afresh.
    public uint hasHistory;
}

// The traced diffuse light's constants' bytes, at the start of the GI buffer.
public static const uint TracedDiffuseBytes = 32u;

public TracedDiffuseConstants GetTracedDiffuse()
{
    BufferRef gi = { GetPassHeader().y };
    return gi.Load<TracedDiffuseConstants>(0u);
}
