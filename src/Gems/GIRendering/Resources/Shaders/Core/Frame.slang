// What every pass of a frame reads: the view, the sun, the picture's size and the heap ids of the frame's tables and
// textures, C#'s FrameConstants, written once a frame into the buffer whose heap id is every pass header's x.
module "Core/Frame";

import Core.Heap;
import Core.Pass;

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

    // Towards the sun, unit, and the sun's and the sky's radiance, before the exposure.
    public float4 sunDirection;
    public float4 sunRadiance;
    public float4 skyRadiance;

    // Where the camera is in the world: what every world position has taken off it before it is used, so positions
    // keep their precision near the camera wherever it is.
    public float4 cameraPosition;

    // Last frame's camera-relative world to clip and where the camera was, for what reprojects into last frame.
    public float4x4 previousViewProjection;
    public float4 previousCameraPosition;

    public uint width;
    public uint height;
    public uint hzbLevels;
    // Whether the depth pyramid holds last frame, so a cluster may be tested against it.
    public uint hasHzb;

    // Metres to the near plane.
    public float near;
    // Pixels a metre covers at a metre's distance: the output's height over twice the tangent of half the field of
    // view.
    public float pixelsPerRadian;
    // Pixels of projected error a drawn cluster may have, as the cluster budget raises it.
    public float errorThreshold;
    // The pixel radius on screen under which an instance hands off to its impostor, as the cluster budget raises it;
    // 0 hands off by distance alone.
    public float impostorPixels;

    public uint instanceCount;
    public uint maxVisibleClusters;
    public uint frameIndex;

    // The heap ids of the tables and textures.
    public uint instances;
    public uint clusters;
    public uint vertices;
    public uint indices;
    public uint materials;
    public uint visibleClusters;
    public uint drawArguments;
    public uint counts;
    public uint retestList;
    public uint retestArguments;
    public uint hzb;
    public uint visibility;
    public uint depth;
    public uint gbufferNormal;
    public uint gbufferBase;
    public uint gbufferSurface;
    public uint output;

    // Pixels the projection is shifted by this frame, for the resolve to take off.
    public float2 jitter;

    // On a GPU that traces rays: the top-level acceleration structure over the instances, 0 when none, and the
    // table of leaf clusters its instances' geometries name.
    public uint acceleration;
    public uint leafClusters;

    // Where each pixel's surface was on last frame's screen: the move to there from its place now, in texture
    // coordinates, in xy, and its view depth then in z.
    public uint velocity;
    // The distance in metres to an instance's sphere past which it hands off to its impostor; 0 hands off by size
    // alone.
    public float impostorDistance;
    // The heap id of the lit picture, in linear light before the exposure.
    public uint hdr;
    // The heap id of the exposure meter, which MeterExposure writes and GetExposure reads, and the seconds the run has
    // taken.
    public uint exposureMeter;
    public float timeSec;
    // On a GPU that traces rays: each material row as a ray hit reads it, Trace/HitSurface's GpuHitMaterial.
    public uint hitMaterials;
    public uint padding3;
}

public FrameConstants GetFrame() { return LoadConstants<FrameConstants>(); }

// What the picture is multiplied by to land near a middle grey: the meter's first float, as it last metered.
public float GetExposure(FrameConstants frame)
{
    BufferRef meter = { frame.exposureMeter };
    return meter.Load<float>(0u);
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
