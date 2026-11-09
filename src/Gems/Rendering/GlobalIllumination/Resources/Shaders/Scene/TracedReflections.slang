// The traced reflections: the light a surface's specular reflects along the directions it reflects the view into,
// traced at half the picture's size for surfaces no rougher than TracedRoughness, resolved over the neighbours and
// blended over frames, and how the picture reads it back at its own size. C#'s TracedReflectionConstants is in the
// GI buffer after the traced diffuse light's.
module "Scene/TracedReflections";

import Core.Heap;
import Core.Pass;
import Scene.TracedDiffuse;

public struct TracedReflectionConstants
{
    // The rays' light at each traced pixel, with how far each went in alpha, and its direction with the density it was
    // drawn at.
    public uint traced;
    public uint rays;
    // The rays' light resolved over the neighbours, with its brightness's variance in alpha, and the farthest of their
    // rays that count.
    public uint resolved;
    public uint distance;
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
    public uint padding0;
    public uint padding1;
}


public TracedReflectionConstants GetTracedReflections()
{
    BufferRef gi = { GetPassHeader().y };
    return gi.Load<TracedReflectionConstants>(TracedDiffuseBytes);
}
