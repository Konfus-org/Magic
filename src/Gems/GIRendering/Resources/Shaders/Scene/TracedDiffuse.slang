// The traced diffuse light: what a white diffuse surface reflects of the light that reaches it indirectly, its
// irradiance over pi, traced at half the picture's size, filtered and blended over frames, and how the picture reads it
// back at its own size. C#'s TracedDiffuseConstants is in the frame's buffer after the probe grid's, in the GI
// constants.
module "Scene/TracedDiffuse";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Probes;
import Scene.Traced;

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

// Where the traced diffuse light's constants are in the frame's buffer, after the probe grid's, and their bytes.
public static const uint TracedDiffuseOffset = ProbeGridOffset + ProbeGridBytes;
public static const uint TracedDiffuseBytes = 32u;

public TracedDiffuseConstants GetTracedDiffuse()
{
    BufferRef frame = { GetPassHeader().x };
    return frame.Load<TracedDiffuseConstants>(TracedDiffuseOffset);
}

// The diffuse light at a pixel of the picture whose surface is at a camera-relative point facing a unit normal, read
// back from the blended light and blurred over up to 8 pixels where it is still noisy.
public float3 SampleTracedDiffuse(
    FrameConstants frame, TracedDiffuseConstants diffuse, uint2 pixel, float3 relative, float3 normal)
{
    TextureRef light = { diffuse.light };
    return SampleTraced(frame, light, pixel, relative, normal, 8.0, 1.0).rgb;
}
