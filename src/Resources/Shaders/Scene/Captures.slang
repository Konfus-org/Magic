// The capture table: a row per model and set of materials drawn as an imposter, C#'s GpuCapture, the heap ids of its
// atlases. An atlas is CaptureCells by CaptureCells cells of CaptureCellSize texels, one a direction the model was
// seen from, the directions laid out as the octahedral map lays out the sphere.
module "Scene/Captures";

import Core.Heap;
import Core.Pack;

public static const uint CaptureCells = 8u;
public static const uint CaptureCellSize = 64u;

public struct GpuCapture
{
    // Albedo with coverage in its alpha; the normal, octahedral, in the model's frame; the reversed depth of the
    // capture.
    public uint albedo;
    public uint normal;
    public uint depth;
    public uint padding;
    // The sphere the model was captured around, in the model's frame.
    public float3 boundsCenter;
    public float boundsRadius;
}

public GpuCapture LoadCapture(uint table, uint row)
{
    ArrayRef<GpuCapture> captures = { table };
    return captures[row];
}

// The direction, in the model's frame, a cell's capture was seen from: towards the viewer.
public float3 GetCaptureDirection(uint2 cell)
{
    float2 flat = ((float2(cell) + 0.5) / float(CaptureCells)) * 2.0 - 1.0;
    return DecodeOctahedral(flat);
}

// Where in an atlas a point of the quad, quadUv in [0, 1]² from its top left, is read for a model seen from
// modelDirection, the cell nearest that direction.
public float2 GetCaptureUv(float3 modelDirection, float2 quadUv)
{
    float2 flat = EncodeOctahedral(modelDirection) * 0.5 + 0.5;
    uint2 cell = min(uint2(flat * float(CaptureCells)), CaptureCells - 1u);
    return (float2(cell) + saturate(quadUv)) / float(CaptureCells);
}
