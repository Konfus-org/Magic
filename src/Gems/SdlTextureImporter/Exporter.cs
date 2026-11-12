using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.SdlBindings;
using Magic.Utils;
using static Magic.SdlBindings.SDL_IOWhence;
using static Magic.SdlBindings.SDL_PixelFormat;
using static Magic.SdlBindings.SDL3;

namespace Magic.SdlTextureImporter;

/// <summary>
/// Writes a plain texture as a PNG, through SDL.
/// </summary>
public sealed unsafe class Exporter : IAssetExporter<Texture>
{
    public string Extension => ".png";

    public Task<Result<ReadOnlyMemory<byte>>> ExportAsync(
        Texture asset,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => Task.FromResult(Export(asset));

    /// <summary>
    /// The top level of <paramref name="texture"/> as a PNG file's bytes; why not for a block-compressed one, whose texels
    /// are not there to write.
    /// </summary>
    private static Result<ReadOnlyMemory<byte>> Export(Texture texture)
    {
        if (texture.IsCompressed)
            return Result.Failure("A block-compressed texture is not written as a PNG.");

        // Written to a stream in memory, then read back out of it.
        SDL_IOStream* io = SDL_IOFromDynamicMem();
        if (io == null)
            return Result.Failure($"No memory stream was made: {Sdl.Error}");

        TextureLevel top = texture.Levels[0];
        bool saved;
        fixed (byte* pixels = texture.Pixels.Span)
        {
            SDL_Surface* surface = SDL_CreateSurfaceFrom(
                top.Width,
                top.Height,
                SDL_PIXELFORMAT_RGBA32,
                pixels + top.Offset,
                top.Width * Importer.TexelBytes
            );
            saved = surface != null && SDL_SavePNG_IO(surface, io, closeio: false);
            SDL_DestroySurface(surface);
        }

        if (!saved)
        {
            string error = Sdl.Error;
            SDL_CloseIO(io);

            return Result.Failure($"The PNG was not written: {error}");
        }

        byte[] file = GC.AllocateUninitializedArray<byte>((int)SDL_GetIOSize(io));
        SDL_SeekIO(io, 0, SDL_IO_SEEK_SET);
        nuint read;
        fixed (byte* to = file)
        {
            read = SDL_ReadIO(io, to, (nuint)file.Length);
        }

        SDL_CloseIO(io);

        return (int)read == file.Length
            ? Result.Success<ReadOnlyMemory<byte>>(file)
            : Result.Failure($"{read} of the PNG's {file.Length} bytes were read back.");
    }
}
