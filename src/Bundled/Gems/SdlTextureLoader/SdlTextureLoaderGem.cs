using System.Numerics;
using System.Text.Json;
using Magic.SdlBindings;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.SdlTextureLoader;

/// <summary>
/// Reads images into <see cref="Texture"/>s with SDL_image: whatever it decodes, converted to RGBA8, with the mip chain
/// made by halving each level with SDL's linear scaling, as the <c>.meta</c> asks (<see cref="TextureMeta"/>). SDL_image
/// needs no SDL started, so files load on any worker, several at once.
/// </summary>
internal sealed unsafe class SdlTextureLoaderGem : IGem, IAssetLoader<Texture>
{
    public ValueTask<Result<Texture>> LoadAsync(string path, ReadOnlyMemory<byte> bytes, JsonElement settings, CancellationToken cancel)
    {
        return new(Load(path, bytes.Span, settings));
    }

    private static Result<Texture> Load(string path, ReadOnlySpan<byte> file, JsonElement settings)
    {
        Result<TextureMeta> meta = Json.Read<TextureMeta>(settings);
        if (!meta.Ok) return Result.Failure<Texture>($"its .meta is not a texture's: {meta.Error}");

        SDL.Surface* decoded = Decode(path, file, meta.Value.Size);
        if (decoded == null)
        {
            return Result.Failure<Texture>($"SDL_image could not read it: {CString.Read(SDL.GetError())}");
        }

        // R, G, B, A bytes in memory whatever the machine's byte order: what the GPU formats below are.
        SDL.Surface* level0 = SDL.ConvertSurface(decoded, SDL.PixelFormat.Rgba32);
        SDL.DestroySurface(decoded);
        if (level0 == null)
        {
            return Result.Failure<Texture>($"SDL could not convert it to RGBA8: {CString.Read(SDL.GetError())}");
        }

        try
        {
            return MakeMips(level0, meta.Value);
        }
        finally
        {
            SDL.DestroySurface(level0);
        }
    }

    /// <summary>
    /// The image in <paramref name="file"/>, an <c>.svg</c> drawn <paramref name="width"/> pixels wide (0: its own
    /// size); null when SDL_image cannot read it. SDL itself is called first, so SDL_image binds to the SDL every gem
    /// shares.
    /// </summary>
    private static SDL.Surface* Decode(string path, ReadOnlySpan<byte> file, int width)
    {
        fixed (byte* bytes = file)
        {
            SDL.IOStream* io = SDL.IOFromConstMem(bytes, (ulong)file.Length);
            if (io == null) return null;

            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return IMG.LoadIo(io, closeio: true);

            SDL.Surface* drawn = IMG.LoadSizedSVGIo(io, width, 0);
            SDL.CloseIO(io);
            return drawn;
        }
    }

    /// <summary>
    /// <paramref name="level0"/> as a texture, with the levels below it when <paramref name="meta"/> asks for mips, each
    /// half the one above, all in one array.
    /// </summary>
    private static Result<Texture> MakeMips(SDL.Surface* level0, TextureMeta meta)
    {
        int count = meta.Mipmaps ? 1 + BitOperations.Log2((uint)Math.Max(level0->W, level0->H)) : 1;
        TextureLevel[] levels = new TextureLevel[count];
        int size = 0;
        for (int i = 0; i < count; i++)
        {
            int width = Math.Max(1, level0->W >> i);
            int height = Math.Max(1, level0->H >> i);
            levels[i] = new TextureLevel(width, height, size, width * height * 4);
            size += levels[i].Size;
        }

        byte[] pixels = GC.AllocateUninitializedArray<byte>(size);
        Copy(level0, pixels.AsSpan(levels[0].Offset, levels[0].Size));
        SDL.Surface* above = level0;
        for (int i = 1; i < count; i++)
        {
            SDL.Surface* level = SDL.ScaleSurface(above, levels[i].Width, levels[i].Height, SDL.ScaleMode.Linear);
            if (above != level0) SDL.DestroySurface(above);

            if (level == null)
            {
                return Result.Failure<Texture>($"SDL could not scale it to mip {i}: {CString.Read(SDL.GetError())}");
            }

            Copy(level, pixels.AsSpan(levels[i].Offset, levels[i].Size));
            above = level;
        }

        if (above != level0) SDL.DestroySurface(above);

        return Result.Success(new Texture
        {
            Width = level0->W,
            Height = level0->H,
            Format = meta.Usage is TextureUsage.Normal or TextureUsage.Mask ? GpuFormat.Rgba8Unorm : GpuFormat.Rgba8Srgb,
            Pixels = pixels,
            Levels = levels,
            Filter = meta.Filter,
            Wrap = meta.Wrap,
        });
    }

    /// <summary>
    /// <paramref name="surface"/>'s RGBA8 pixels into <paramref name="level"/> as tightly packed rows, where SDL pads
    /// each to its pitch. A surface SDL made by converting or scaling needs no lock to be read.
    /// </summary>
    private static void Copy(SDL.Surface* surface, Span<byte> level)
    {
        int row = surface->W * 4;
        byte* pixels = (byte*)surface->Pixels;
        for (int y = 0; y < surface->H; y++)
        {
            new ReadOnlySpan<byte>(pixels + (y * surface->Pitch), row).CopyTo(level.Slice(y * row, row));
        }
    }
}
