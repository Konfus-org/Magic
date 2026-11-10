// What every pass of a frame reads: the view, the picture's size and the heap ids of the view's textures, C#'s
// FrameConstants, written once a frame at the start of the buffer whose heap id is every pass header's x. The models,
// the lights and the shadows follow it in that buffer, then a renderer's own constants.
module "Core/Frame";

import Core.Heap;
import Core.Pass;
import Core.Math;

// The frame's bytes, C#'s FrameConstants.Bytes: where the rows after it in its buffer start.
public static const uint FrameBytes = 528u;

public struct FrameConstants
{
    // The camera-relative world to view, view to clip (reversed depth, no far plane), their product and its inverse,
    // row-major, for row vectors: clip = mul(float4(p, 1), viewProjection).
    public float4x4 view;
    public float4x4 projection;
    public float4x4 viewProjection;
    public float4x4 inverseViewProjection;

    // The frustum's sides and near plane in the camera-relative world, xyz a unit normal pointing in and w the offset,
    // so a point is inside where dot(xyz, p) + w >= 0 for every one; the sixth is the far plane, which never rejects.
    public float4 frustumPlanes[6];

    // Where the camera is in the world: what every world position has taken off it before it is used, so positions
    // keep their precision near the camera wherever it is.
    public float4 cameraPosition;

    // Last frame's camera-relative world to clip and where the camera was, for what reprojects into last frame.
    public float4x4 previousViewProjection;
    public float4 previousCameraPosition;

    public uint width;
    public uint height;

    // Metres to the near plane.
    public float near;
    // Pixels a metre covers at a metre's distance: the output's height over twice the tangent of half the field of
    // view.
    public float pixelsPerRadian;

    public uint frameIndex;

    // The heap ids of the view's textures.
    public uint depth;
    public uint gbufferNormal;
    public uint gbufferBase;
    public uint gbufferSurface;
    public uint output;

    // Pixels the projection is shifted by this frame, for the resolve to take off.
    public float2 jitter;

    // Where each pixel's surface was on last frame's screen: the move to there from its place now, in texture
    // coordinates, in xy, and its view depth then in z.
    public uint velocity;
    // The heap id of the picture: the lit picture in linear light, until the post effects finish it for the screen.
    public uint hdr;
    // The seconds the run has taken, and since the frame before.
    public float timeSec;
    public float deltaSec;
    // The nearest glass's reversed depth in front of the opaque surfaces, 0 where there is none, and its surface as
    // its material decided it: its base colour and alpha, its normal, and its roughness and what it gives off.
    public uint glassDepth;
    public uint glassBase;
    public uint glassNormal;
    public uint glassSurface;
}

public FrameConstants GetFrame() { return LoadConstants<FrameConstants>(); }

// Whether a sphere in the camera-relative world is at least partly inside the frustum.
public bool IsSphereInFrustum(FrameConstants frame, float3 center, float radius)
{
    [unroll]
    for (uint i = 0u; i < 6u; i++)
    {
        float4 plane = frame.frustumPlanes[i];
        if (dot(plane.xyz, center) + plane.w < -radius)
            return false;
    }

    return true;
}


// The most a pixel's point rises off its flat triangle, in widths of the pixel at its distance, as the GBuffer
// surface's w holds it: square-rooted, so a small rise keeps its precision in 8 bits.
static const float MostLiftPixels = 64.0;

// How far a pixel's point at a camera-relative position rises off its flat triangle, as the GBuffer surface's w holds
// it, and back.
public float EncodeLift(FrameConstants frame, float3 relative, float lift)
{
    float footprint = max(length(relative) / frame.pixelsPerRadian, Epsilon);
    return sqrt(saturate(lift / (MostLiftPixels * footprint)));
}

public float DecodeLift(FrameConstants frame, float3 relative, float encoded)
{
    return encoded * encoded * MostLiftPixels * length(relative) / frame.pixelsPerRadian;
}

// A world position relative to the camera.
public float3 Relative(FrameConstants frame, float3 worldPosition) { return worldPosition - frame.cameraPosition.xyz; }

// The camera-relative position a pixel's reversed depth puts it at, from its texture coordinates.
public float3 ReconstructRelative(FrameConstants frame, float2 uv, float depth)
{
    float4 clip = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0);
    float4 relative = mul(clip, frame.inverseViewProjection);
    return relative.xyz / relative.w;
}
