using Magic.Assets;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.Deferred;

/// <summary>
/// The output read back and saved, opaque as the window shows it: what a screenshot asked for is.
/// </summary>
internal static class Screenshot
{
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

        // The window shows no alpha, which a picture may use for its own data.
        byte[] opaque = pixels.Value.ToArray();
        for (int alpha = 3; alpha < opaque.Length; alpha += 4)
        {
            opaque[alpha] = byte.MaxValue;
        }

        Texture shot = new()
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba8Srgb,
            Pixels = opaque,
            Levels = [new TextureLevel(width, height, 0, opaque.Length)],
            HasMipmaps = false,
            IsCompressed = false,
        };
        Result written = await assets.ExportAsync(shot, path, null, CancellationToken.None).ConfigureAwait(false);
        if (written.Ok)
            Terminal.Write(path);
        else
            Log.Error($"The screenshot could not be written: {written.Error}");
    }
}
