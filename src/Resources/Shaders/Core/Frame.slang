// What every pass of a frame reads: the view, the sun, the output's size and the heap ids of the frame's tables and
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

    // Towards the sun, unit, and the sun's and the sky's radiance at the output's exposure.
    public float4 sunDirection;
    public float4 sunRadiance;
    public float4 skyRadiance;

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
    // Pixels of projected error a drawn cluster may have.
    public float errorThreshold;
    public float padding0;

    public uint instanceCount;
    public uint maxVisibleClusters;
    public uint frameIndex;
    public uint defaultSampler;

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
    public uint padding1;
    public uint padding2;
    public uint padding3;
}

public FrameConstants GetFrame() { return LoadConstants<FrameConstants>(); }
