using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// Models drawn into the card atlases as they become resident, a few a frame: each along its six sides, its finest
/// clusters at the origin, through an orthographic view of its box, each card at its own size.
/// </summary>
internal sealed class CardCapture(
    IGraphicsApi gpu,
    RenderGraph graph,
    GpuPipelines pipelines,
    CardTable cards,
    Visibility visibility,
    FrameView frameView
) : IDisposable
{
    private const int CapturesPerFrame = 4;

    private readonly GpuPass _writePass = pipelines.Compute(
        "Capture card",
        "CaptureCard",
        "CaptureCard",
        isTiled: true
    );
    private readonly FrameTargets _targets = new(gpu, graph);
    private readonly GpuBuffer[] _constants = new GpuBuffer[GpuCard.PerCapture];
    private GpuBuffer _instance;

    public void Dispose()
    {
        _targets.Dispose();
        if (!_instance.IsNone)
            gpu.Destroy(_instance);

        foreach (GpuBuffer buffer in _constants)
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }
    }

    public Result Create()
    {
        Result<GpuBuffer> instance = gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = GpuInstance.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Capture instance",
            }
        );
        if (!instance.Ok)
            return Result.Failure($"the Capture instance buffer was not made: {instance.Error}");

        _instance = instance.Value;
        for (int side = 0; side < GpuCard.PerCapture; side++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = FrameConstants.Bytes,
                    Usage = BufferUsages.Storage,
                    Name = $"Card constants {side}",
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"a card's constants buffer was not made: {buffer.Error}");

            _constants[side] = buffer.Value;
        }

        return _targets.Resize(GpuCard.MaxTexels, GpuCard.MaxTexels, GpuCard.MaxTexels, GpuCard.MaxTexels);
    }

    /// <summary>
    /// The captures newly asked for, drawn before any instance shows them.
    /// </summary>
    public void Record()
    {
        int captured = 0;
        while (captured < CapturesPerFrame && cards.TryTakeJob(out CaptureJob job))
        {
            RecordJob(job);
            captured++;
        }
    }

    /// <summary>
    /// An orthographic projection of a card's box, for row vectors: the box's <paramref name="extents"/> across, up
    /// and along the view, drawn <paramref name="width"/> by <paramref name="height"/> texels into the top left of a
    /// target <see cref="GpuCard.MaxTexels"/> square, with reversed depth: 1 at the side seen from, 0 at the far
    /// side, the viewer 2 extents ahead of the box's centre.
    /// </summary>
    private static Matrix4x4 CreateProjection(uint width, uint height, Vector3 extents)
    {
        float target = GpuCard.MaxTexels;
        Matrix4x4 projection = default;
        projection.M11 = width / (target * extents.X);
        projection.M22 = height / (target * extents.Y);
        projection.M41 = (width / target) - 1;
        projection.M42 = 1 - (height / target);
        projection.M33 = -1 / (2 * extents.Z);
        projection.M43 = 1.5f;
        projection.M44 = 1;

        return projection;
    }

    private void RecordJob(in CaptureJob job)
    {
        ResidentModel resident = job.Resident;
        graph.BeginTransferPass("Capture instance");
        GpuUpload upload = gpu.BeginUpload(GpuInstance.Bytes);
        GpuInstance instance = new()
        {
            World = Matrix4x4.Identity,
            InverseWorld = Matrix4x4.Identity,
            PreviousWorld = Matrix4x4.Identity,
            BoundsCenter = resident.BoundsCenter,
            BoundsRadius = resident.BoundsRadius,
            ClusterOffset = resident.ClusterOffset,
            ClusterCount = resident.ClusterCount,
            Material0 = job.Materials[0],
            Material1 = job.Materials[1],
            Material2 = job.Materials[2],
            Material3 = job.Materials[3],
            Material4 = job.Materials[4],
            Material5 = job.Materials[5],
            Material6 = job.Materials[6],
            Material7 = job.Materials[7],
        };
        MemoryMarshal.Write(upload.Bytes, in instance);
        graph.Use(_instance, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, _instance, 0, GpuInstance.Bytes);
        graph.EndPass();

        for (int side = 0; side < GpuCard.PerCapture; side++)
        {
            GpuCard card = cards.GetCard(job.Row, side);
            CardTable.GetBasis((uint)side, out Vector3 direction, out Vector3 right, out Vector3 up);
            Vector3 extents = CardTable.GetExtents(card, direction, right, up);
            Vector3 cameraPosition = card.Center + (direction * 2 * extents.Z);
            Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, -direction, up);
            FrameConstants frame = frameView.GetView(
                view,
                CreateProjection(card.Width, card.Height, extents),
                cameraPosition,
                1e-3f,
                _targets,
                _instance,
                1
            );
            frame.ErrorThreshold = 0;
            frame.PixelsPerRadian = 1;
            frame.Flags = FrameConstants.FlagNoConeCull;

            GpuBuffer constants = _constants[side];
            graph.BeginTransferPass($"Card {side} constants");
            frameView.Upload(constants, frame);
            visibility.ResetCounts();
            graph.EndPass();

            visibility.RecordOnce(new RenderView(constants, _instance, 1, _targets), $"Card {side}");

            uint cardIndex = (job.Row * GpuCard.PerCapture) + (uint)side;
            _writePass
                .Run(new GpuConstants(constants.Id, cardIndex), name: $"Card {side} write")
                .Reads(
                    constants,
                    cards.Buffer,
                    _targets.Visibility,
                    _targets.Depth,
                    _targets.GBufferNormal,
                    _targets.GBufferBase
                )
                .Writes(cards.Albedo, cards.Normal, cards.Depth)
                .Dispatch(pipelines.GetTiles((int)card.Width), pipelines.GetTiles((int)card.Height));
        }
    }
}
