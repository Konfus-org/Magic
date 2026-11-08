// The traced reflections: the light a surface's specular reflects along the directions it reflects the view into,
// traced at half the picture's size for surfaces no rougher than TracedRoughness, resolved over the neighbours and
// blended over frames, and how the picture reads it back at its own size. C#'s TracedReflectionConstants is in the
// buffer whose heap id is the pass header's w.
module "Scene/TracedReflections";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Traced;

// The roughest surface whose reflections are traced; a rougher one reflects its traced diffuse light, or the sky giving
// way to its diffuse light where none is traced.
public static const float TracedRoughness = 0.6;

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
    return LoadConstants<TracedReflectionConstants>(GetPassHeader().w);
}

// The light reflected towards the view at a pixel of the picture whose surface is at a camera-relative point facing a
// unit normal with a roughness no more than TracedRoughness, from the blended light, in rgb, and how much of it was
// traced in alpha: blurred over up to 2 pixels as the surface roughens towards an eighth.
public float4 SampleTracedReflections(
    FrameConstants frame, uint light, uint2 pixel, float3 relative, float3 normal, float roughness)
{
    TextureRef texture = { light };
    float radius = 2.0 * saturate(roughness * 8.0);
    return SampleTraced(frame, texture, pixel, relative, normal, radius, TracedRoughness);
}
