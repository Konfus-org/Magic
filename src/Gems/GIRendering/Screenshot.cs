using Magic.Debugging;
using Magic.Files;
using Magic.Gpu;
using Magic.Images;
using Magic.Rendering;
using Magic.Utils;

namespace GIRendering;

/// <summary>
/// The output read back and saved: what a screenshot asked for is.
/// </summary>
internal static class Screenshot
{
    /// <summary>
    /// The frame's output copied where the CPU can read it.
    /// </summary>
    public static void RecordReadback(RenderGraph graph, FrameTargets targets)
    {
        graph.BeginTransferPass("Read the output back");
        graph.Use(targets.Output, GpuAccess.CopySource);
        graph.Use(targets.Readback, GpuAccess.CopyDestination);
        graph.Recorder.CopyTextureToBuffer(targets.Output, 0, 0, targets.Readback, 0);
        graph.Use(targets.Readback, GpuAccess.HostRead);
        graph.EndPass();
    }

    /// <summary>
    /// Writes what was read back to <paramref name="path"/> once the GPU is done with it.
    /// </summary>
    public static async Task SaveAsync(
        IGraphicsApi gpu,
        IFileSystem files,
        GpuBuffer readback,
        string path,
        int width,
        int height
    )
    {
        Result<ReadOnlyMemory<byte>> pixels = await gpu.ReadBackAsync(
                readback,
                0,
                width * height * 4,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (!pixels.Ok)
        {
            Log.Error($"The screenshot could not be read back: {pixels.Error}");

            return;
        }

        byte[] png = Png.Encode(width, height, pixels.Value.Span);
        Result made = files.CreateDirectory(files.GetParent(path));
        if (!made.Ok)
        {
            Log.Error($"The screenshot's folder could not be made: {made.Error}");

            return;
        }

        Result written = await files.WriteAsync(path, png, CancellationToken.None).ConfigureAwait(false);
        if (written.Ok)
            Log.Info($"Screenshot: {path}");
        else
            Log.Error($"The screenshot could not be written: {written.Error}");
    }
}
