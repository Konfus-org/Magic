using Magic.Assets;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The output read back and saved: what a screenshot asked for is.
/// </summary>
internal static class Screenshot
{
    /// <summary>
    /// The frame's output copied into <paramref name="readback"/>, where the CPU can read it.
    /// </summary>
    public static void RecordReadback(RenderGraph graph, GpuTexture output, GpuBuffer readback)
    {
        graph.BeginTransferPass("Read the output back");
        graph.Use(output, GpuAccess.CopySource);
        graph.Use(readback, GpuAccess.CopyDestination);
        graph.CopyTextureToBuffer(output, 0, 0, readback, 0);
        graph.EndPass();
        graph.BeginTransferPass("Output to the host");
        graph.Use(readback, GpuAccess.HostRead);
        graph.EndPass();
    }

    /// <summary>
    /// Writes what was read back to <paramref name="path"/> once the GPU is done with it, as a texture through the gem
    /// that writes them.
    /// </summary>
    public static async Task SaveAsync(
        IGraphicsApi gpu,
        AssetManager assets,
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

        Texture shot = new()
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba8Srgb,
            Pixels = pixels.Value,
            Levels = [new TextureLevel(width, height, 0, pixels.Value.Length)],
            HasMipmaps = false,
            IsCompressed = false,
        };
        Result written = await assets.ExportAsync(shot, path, null, CancellationToken.None).ConfigureAwait(false);
        if (written.Ok)
            Log.Info($"Screenshot: {path}");
        else
            Log.Error($"The screenshot could not be written: {written.Error}");
    }
}
