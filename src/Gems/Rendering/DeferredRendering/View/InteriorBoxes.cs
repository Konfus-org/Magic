using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.View;

/// <summary>
/// One <see cref="Interior"/> box as the GPU reads it, the twin of <c>Light/Ambient.slang</c>'s GpuInterior, 80 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct GpuInterior
{
    public const int Bytes = 80;

    /// <summary>
    /// From the camera-relative world of the view to the box's unit cube, grown by
    /// <see cref="InteriorBoxes.Margin"/> on each side.
    /// </summary>
    public Matrix4x4 ToBox;

    /// <summary>
    /// The share of the sky's light in the box.
    /// </summary>
    public float SkyLight;

    public Vector3 Padding;
}

/// <summary>
/// Puts the world's <see cref="Interior"/> boxes into a buffer for each view, about the view's camera.
/// </summary>
internal sealed class InteriorBoxes(IGraphicsApi gpu)
{
    /// <summary>
    /// The most interior boxes that a view reads.
    /// </summary>
    public const int MostBoxes = 64;

    /// <summary>
    /// The metres that each box grows by on each side, so that a surface on its face, and a point that the depth
    /// puts a little behind that face, is inside.
    /// </summary>
    public const float Margin = 0.1f;

    private readonly GpuInterior[] _rows = new GpuInterior[MostBoxes];

    /// <summary>
    /// Whether the error for more boxes than a view reads is shown, so it shows one time and not each frame.
    /// </summary>
    private bool _isTooManyLogged;

    /// <summary>
    /// Stages the boxes of <paramref name="interiors"/> for the view, about <paramref name="eye"/>.
    /// </summary>
    /// <returns>The buffer of the boxes and how many it holds.</returns>
    public Result<(GpuBuffer Boxes, int Count)> Prepare(
        RenderGraph graph,
        int view,
        EcsSnapshot<Transform, Interior> interiors,
        Vector3 eye
    )
    {
        Result<GpuBuffer> boxes = gpu.GetBuffer(new("Interior boxes", MostBoxes * GpuInterior.Bytes), view);
        if (!boxes.Ok)
            return Result.Failure(boxes.Error);

        if (interiors.Count > MostBoxes && !_isTooManyLogged)
        {
            Log.Error($"Some interior boxes are not read: a view reads {MostBoxes}.");
            _isTooManyLogged = true;
        }

        int count = Math.Min(interiors.Count, MostBoxes);
        for (int i = 0; i < count; i++)
        {
            // About the eye, so the matrix holds small numbers and keeps its precision far from the origin.
            Transform box = interiors.First[i] with
            {
                Position = interiors.First[i].Position - eye,
                Scale = interiors.First[i].Scale + new Vector3(2 * Margin),
            };
            _rows[i] = new GpuInterior
            {
                ToBox = Matrix4x4.Invert(box.Matrix, out Matrix4x4 toBox) ? toBox : default,
                SkyLight = Math.Clamp(interiors.Second[i].SkyLight, 0, 1),
            };
        }

        if (count > 0 && !graph.Upload(boxes.Value, _rows.AsSpan(0, count)))
            return Result.Failure("the interior boxes were not staged.");

        return Result.Success((boxes.Value, count));
    }
}
