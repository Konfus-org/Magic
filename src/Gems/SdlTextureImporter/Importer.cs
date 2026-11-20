using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.Assets.Pipeline;
using Magic.Files;
using Magic.SdlBindings;
using Magic.Utils;
using static Magic.SdlBindings.SDL_PixelFormat;
using static Magic.SdlBindings.SDL_ScaleMode;
using static Magic.SdlBindings.SDL3;
using static Magic.SdlBindings.SDL3_image;
using static Magic.SdlTextureImporter.bc7enc;

namespace Magic.SdlTextureImporter;

/// <summary>
/// Reads image files through SDL_image into textures that are ready to upload, with mip chains and BC7 blocks.
/// </summary>
/// <remarks>
/// Each mip level is half the one above, made with SDL's linear scaling. Every level is packed into BC7 blocks unless
/// its <c>.meta</c> says otherwise, in sRGB for colour and linear for normals and masks. As an
/// <see cref="ICache{T}"/>, it makes an image once for each change. SDL_image needs no SDL started, so files load on
/// any worker, several at once.
/// </remarks>
public sealed unsafe class Importer : IAssetImporter<Texture>, ICache<Texture>
{
    /// <summary>
    /// The bytes of one RGBA8 texel.
    /// </summary>
    internal const int TexelBytes = 4;

    private const int HeaderBytes = 16;

    private const int BlockTexels = 4;
    private const int BlockBytes = 16;

    /// <summary>
    /// What the BC7 encoder is told for every block: the error of each channel weighs the same, as most textures are
    /// data.
    /// </summary>
    /// <remarks>
    /// It is made once, with the tables that the encoder needs before its first block.
    /// </remarks>
    private static readonly bc7enc_compress_block_params _blockOptions = CreateBlockOptions();

    internal Importer() { }

    public ImmutableArray<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".svg", ".ico"];

    /// <summary>
    /// Raised when the bytes written change shape, or the levels are encoded otherwise.
    /// </summary>
    /// <remarks>
    /// It is the version of both the import and the cache, so a raise also makes dirty what preprocessors made of the
    /// textures.
    /// </remarks>
    public int Version => 3;

    public Task<Result<Texture>> ImportAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => Task.FromResult(Import(file));

    public void Write(Texture value, IBufferWriter<byte> bytes)
    {
        Span<byte> header = bytes.GetSpan(HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header, (int)value.Format);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Levels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], value.Size);
        header[12] = (byte)value.Usage;
        header[13] = (byte)value.Filter;
        header[14] = (byte)value.Wrap;
        header[15] = (byte)((value.HasMipmaps ? 1 : 0) | (value.IsCompressed ? 2 : 0));
        bytes.Advance(HeaderBytes);
        bytes.Write(MemoryMarshal.AsBytes(value.Levels.AsSpan()));
        bytes.Write(value.Pixels.Span);
    }

    public Result<Texture> Read(ReadOnlySpan<byte> bytes)
    {
        // Bytes cut short throw from a slice, which the caches take as a dirty entry.
        ReadOnlySpan<byte> header = bytes;
        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int tableBytes = levelCount * sizeof(TextureLevel);
        bytes = bytes[HeaderBytes..];

        // The level table, then the pixels, which end where the table's last level does.
        ImmutableArray<TextureLevel> levels = [.. MemoryMarshal.Cast<byte, TextureLevel>(bytes[..tableBytes])];
        bytes = bytes[tableBytes..];
        TextureLevel last = levels[^1];
        int pixelBytes = last.Offset + last.Size;

        return Result.Success(
            new Texture
            {
                Width = levels[0].Width,
                Height = levels[0].Height,
                Format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(header),
                Pixels = bytes[..pixelBytes].ToArray(),
                Levels = levels,
                Size = BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
                Usage = (TextureUsage)header[12],
                Filter = (TextureFilter)header[13],
                Wrap = (TextureWrap)header[14],
                HasMipmaps = (header[15] & 1) != 0,
                IsCompressed = (header[15] & 2) != 0,
            }
        );
    }

    /// <summary>
    /// Imports the image in <paramref name="file"/> as a texture, with its mip levels in one array when its
    /// <c>.meta</c> asks.
    /// </summary>
    /// <remarks>
    /// Each level is half the one above. It calls SDL itself first, so SDL_image binds to the SDL that every gem
    /// shares.
    /// </remarks>
    private static Result<Texture> Import(in AssetFile file)
    {
        int size = file.Metadata.Get<int>(nameof(Texture.Size));
        bool hasMipmaps = file.Metadata.Get<bool>(nameof(Texture.HasMipmaps));
        bool isCompressed = file.Metadata.Get<bool>(nameof(Texture.IsCompressed));
        TextureUsage usage = file.Metadata.Get<TextureUsage>(nameof(Texture.Usage));

        // Decoded from the file's bytes, an .svg drawn `size` pixels wide (0: its own size).
        SDL_Surface* decoded;
        fixed (byte* bytes = file.Bytes.Span)
        {
            SDL_IOStream* io = SDL_IOFromConstMem(bytes, (nuint)file.Bytes.Length);
            if (io == null)
                return Result.Failure($"{file.Path} did not decode: {Sdl.Error}");

            if (file.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // A height of 0 keeps it in proportion. Unlike the other loaders, this one leaves the stream open.
                decoded = IMG_LoadSizedSVG_IO(io, size, 0);
                SDL_CloseIO(io);
            }
            else
            {
                // Named by its extension for the one format, .tga, that has no signature; the rest are known by theirs.
                fixed (byte* type = CString.ToBytes(Path.GetExtension(file.Path)[1..]))
                {
                    decoded = IMG_LoadTyped_IO(io, closeio: true, type);
                }
            }
        }

        if (decoded == null)
            return Result.Failure($"{file.Path} did not decode: {Sdl.Error}");

        // R, G, B, A bytes in memory whatever the machine's byte order: what the formats below are.
        SDL_Surface* surface = SDL_ConvertSurface(decoded, SDL_PIXELFORMAT_RGBA32);
        SDL_DestroySurface(decoded);
        if (surface == null)
            return Result.Failure($"{file.Path} did not convert to RGBA8: {Sdl.Error}");

        // Where each level goes: half the one above, down to one texel.
        int count = hasMipmaps ? 1 + BitOperations.Log2((uint)Math.Max(surface->w, surface->h)) : 1;
        ImmutableArray<TextureLevel>.Builder table = ImmutableArray.CreateBuilder<TextureLevel>(count);
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            int width = Math.Max(1, surface->w >> i);
            int height = Math.Max(1, surface->h >> i);
            int levelBytes = isCompressed
                ? GetBlocks(width) * GetBlocks(height) * BlockBytes
                : width * height * TexelBytes;
            table.Add(new TextureLevel(width, height, total, levelBytes));
            total += levelBytes;
        }

        // Each level is scaled from the one above. An uncompressed level is copied at once. The surfaces of a
        // compressed chain stay until all its blocks are encoded, so that one loop can encode them all. A surface SDL
        // made by converting or scaling needs no lock to be read.
        ImmutableArray<TextureLevel> levels = table.MoveToImmutable();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(total);
        SDL_Surface*[] surfaces = new SDL_Surface*[count];
        surfaces[0] = surface;
        for (int i = 0; i < count; i++)
        {
            TextureLevel level = levels[i];
            if (i > 0)
            {
                surfaces[i] = SDL_ScaleSurface(surfaces[i - 1], level.Width, level.Height, SDL_SCALEMODE_LINEAR);
                if (surfaces[i] == null)
                {
                    DestroyAll(surfaces);
                    return Result.Failure($"{file.Path} did not scale to mip {i}: {Sdl.Error}");
                }
            }

            if (isCompressed)
                continue;

            // SDL pads each row to its pitch; here they are packed tight. The level above is not needed again.
            byte* source = (byte*)surfaces[i]->pixels;
            int row = level.Width * TexelBytes;
            for (int y = 0; y < level.Height; y++)
            {
                new ReadOnlySpan<byte>(source + (y * surfaces[i]->pitch), row).CopyTo(
                    pixels.AsSpan(level.Offset + (y * row), row)
                );
            }

            if (i > 0)
            {
                SDL_DestroySurface(surfaces[i - 1]);
                surfaces[i - 1] = null;
            }
        }

        if (isCompressed)
            EncodeBlocks(surfaces, levels, pixels);
        DestroyAll(surfaces);

        return Result.Success(
            new Texture
            {
                Width = levels[0].Width,
                Height = levels[0].Height,
                Format = (usage, isCompressed) switch
                {
                    (TextureUsage.Color, true) => TextureFormat.Bc7Srgb,
                    (TextureUsage.Color, false) => TextureFormat.Rgba8Srgb,
                    (_, true) => TextureFormat.Bc7Unorm,
                    _ => TextureFormat.Rgba8Unorm,
                },
                Pixels = pixels,
                Levels = levels,
                Usage = usage,
                HasMipmaps = hasMipmaps,
                IsCompressed = isCompressed,
                Filter = file.Metadata.Get<TextureFilter>(nameof(Texture.Filter)),
                Wrap = file.Metadata.Get<TextureWrap>(nameof(Texture.Wrap)),
                Size = size,
            }
        );
    }

    /// <summary>
    /// Packs the rows of blocks of every level in one loop, not one loop for each level, so that small levels cost no loop of their own.
    /// A block past a level's edge repeats its last texels.
    /// </summary>
    private static void EncodeBlocks(SDL_Surface*[] surfaces, ImmutableArray<TextureLevel> levels, byte[] pixels)
    {
        // The rows of block of each level, counted from the first level's.
        int[] firstRows = new int[levels.Length + 1];
        for (int i = 0; i < levels.Length; i++)
            firstRows[i + 1] = firstRows[i] + GetBlocks(levels[i].Height);

        fixed (byte* output = pixels)
        {
            byte* start = output;
            Parallel.For(
                0,
                firstRows[^1],
                index =>
                {
                    int i = 0;
                    while (index >= firstRows[i + 1])
                        i++;

                    TextureLevel level = levels[i];
                    byte* source = (byte*)surfaces[i]->pixels;
                    int pitch = surfaces[i]->pitch;
                    int row = index - firstRows[i];
                    int columns = GetBlocks(level.Width);
                    bc7enc_compress_block_params options = _blockOptions;
                    uint* block = stackalloc uint[BlockTexels * BlockTexels];
                    for (int column = 0; column < columns; column++)
                    {
                        for (int y = 0; y < BlockTexels; y++)
                        {
                            uint* from = (uint*)(
                                source + (Math.Min((row * BlockTexels) + y, level.Height - 1) * pitch)
                            );
                            for (int x = 0; x < BlockTexels; x++)
                            {
                                block[(y * BlockTexels) + x] = from[
                                    Math.Min((column * BlockTexels) + x, level.Width - 1)
                                ];
                            }
                        }

                        bc7enc_compress_block(
                            start + level.Offset + (((row * columns) + column) * BlockBytes),
                            (byte*)block,
                            &options
                        );
                    }
                }
            );
        }
    }

    private static void DestroyAll(SDL_Surface*[] surfaces)
    {
        foreach (SDL_Surface* surface in surfaces)
        {
            if (surface != null)
                SDL_DestroySurface(surface);
        }
    }

    /// <summary>
    /// How many blocks cover <paramref name="texels"/> texels along one side.
    /// </summary>
    private static int GetBlocks(int texels) => (texels + BlockTexels - 1) / BlockTexels;

    private static bc7enc_compress_block_params CreateBlockOptions()
    {
        bc7enc_compress_block_init();
        bc7enc_compress_block_params options = new()
        {
            m_max_partitions_mode = BC7ENC_MAX_PARTITIONS1,
            m_try_least_squares = 1,
            m_mode_partition_estimation_filterbank = 1,
            m_use_mode5_for_alpha = 1,
            m_use_mode7_for_alpha = 1,
        };
        ((Span<uint>)options.m_weights).Fill(1);

        return options;
    }
}
