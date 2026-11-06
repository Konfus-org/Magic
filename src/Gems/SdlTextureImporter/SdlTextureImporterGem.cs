using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Caching;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Projects;
using Magic.SdlBindings;
using Magic.Utils;
using static Magic.SdlBindings.SDL_IOWhence;
using static Magic.SdlBindings.SDL_PixelFormat;
using static Magic.SdlBindings.SDL_ScaleMode;
using static Magic.SdlBindings.SDL3;
using static Magic.SdlBindings.SDL3_image;
using static Magic.SdlTextureImporter.bc7enc;

namespace Magic.SdlTextureImporter;

/// <summary>
/// Reads image files into textures ready to upload, through SDL_image: decoded, their mip chain made by halving each level
/// with SDL's linear scaling, and every level packed into BC7 blocks unless its <c>.meta</c> says otherwise, in sRGB for
/// colour and linear for normals and masks. As an <see cref="ICache{T}"/> an image is made once per change. It also writes
/// a plain texture as a PNG, through SDL. SDL_image needs no SDL started, so files load on any worker, several at once. It
/// is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed unsafe class SdlTextureImporterGem
    : IGem,
        IAssetImporter<Texture>,
        IAssetExporter<Texture>,
        ICache<Texture>
{
    private const int HeaderBytes = 16;
    private const int TexelBytes = 4;
    private const int BlockTexels = 4;
    private const int BlockBytes = 16;

    /// <summary>
    /// What the BC7 encoder is told for every block: every channel's error weighed alike, as most textures are data rather
    /// than colour. Made, with the tables the encoder needs before its first block, once.
    /// </summary>
    private static readonly bc7enc_compress_block_params _blockOptions = CreateBlockOptions();

    public ImmutableArray<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga", ".bmp", ".svg", ".ico"];

    public string Extension => ".png";

    /// <summary>
    /// Raised when the bytes written change shape, or the levels are encoded otherwise.
    /// </summary>
    public int Version => 3;

    public Task<Result<Texture>> ImportAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => Task.FromResult(Import(file));

    public Task<Result<ReadOnlyMemory<byte>>> ExportAsync(
        Texture asset,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    ) => Task.FromResult(Export(asset));

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
        if (bytes.Length < HeaderBytes)
            return Result.Failure("the entry is cut short.");

        ReadOnlySpan<byte> header = bytes;
        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int tableBytes = levelCount * sizeof(TextureLevel);
        bytes = bytes[HeaderBytes..];
        if (levelCount < 1 || bytes.Length < tableBytes)
            return Result.Failure("the entry is cut short.");

        // The level table, then the pixels, which end where the table's last level does.
        ImmutableArray<TextureLevel> levels = [.. MemoryMarshal.Cast<byte, TextureLevel>(bytes[..tableBytes])];
        bytes = bytes[tableBytes..];
        TextureLevel last = levels[^1];
        int pixelBytes = last.Offset + last.Size;
        if (pixelBytes < 0 || bytes.Length < pixelBytes)
            return Result.Failure("the entry is cut short.");

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
    /// The image in <paramref name="file"/> as a texture, with the levels under it when its <c>.meta</c> asks, each half
    /// the one above, all in one array. SDL itself is called first, so SDL_image binds to the SDL every gem shares.
    /// </summary>
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
                return Result.Failure($"{file.Path} did not decode: {CString.Read(SDL_GetError())}");

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
            return Result.Failure($"{file.Path} did not decode: {CString.Read(SDL_GetError())}");

        // R, G, B, A bytes in memory whatever the machine's byte order: what the formats below are.
        SDL_Surface* surface = SDL_ConvertSurface(decoded, SDL_PIXELFORMAT_RGBA32);
        SDL_DestroySurface(decoded);
        if (surface == null)
            return Result.Failure($"{file.Path} did not convert to RGBA8: {CString.Read(SDL_GetError())}");

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

        // Each level is scaled from the one above, which is then freed, and stored as it is or as BC7 blocks. A surface SDL
        // made by converting or scaling needs no lock to be read.
        ImmutableArray<TextureLevel> levels = table.MoveToImmutable();
        byte[] pixels = GC.AllocateUninitializedArray<byte>(total);
        for (int i = 0; i < count; i++)
        {
            TextureLevel level = levels[i];
            if (i > 0)
            {
                SDL_Surface* above = surface;
                surface = SDL_ScaleSurface(above, level.Width, level.Height, SDL_SCALEMODE_LINEAR);
                SDL_DestroySurface(above);
                if (surface == null)
                    return Result.Failure($"{file.Path} did not scale to mip {i}: {CString.Read(SDL_GetError())}");
            }

            byte* source = (byte*)surface->pixels;
            int pitch = surface->pitch;
            if (!isCompressed)
            {
                // SDL pads each row to its pitch; here they are packed tight.
                int row = level.Width * TexelBytes;
                for (int y = 0; y < level.Height; y++)
                {
                    new ReadOnlySpan<byte>(source + (y * pitch), row).CopyTo(
                        pixels.AsSpan(level.Offset + (y * row), row)
                    );
                }

                continue;
            }

            // Each row of blocks on a worker; a block past the level's edge repeats its last texels.
            int columns = GetBlocks(level.Width);
            Parallel.For(
                0,
                GetBlocks(level.Height),
                row =>
                {
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

                        fixed (byte* to = &pixels[level.Offset + (((row * columns) + column) * BlockBytes)])
                        {
                            bc7enc_compress_block(to, (byte*)block, &options);
                        }
                    }
                }
            );
        }

        SDL_DestroySurface(surface);

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
            return Result.Failure($"No memory stream was made: {CString.Read(SDL_GetError())}");

        TextureLevel top = texture.Levels[0];
        bool saved;
        fixed (byte* pixels = texture.Pixels.Span)
        {
            SDL_Surface* surface = SDL_CreateSurfaceFrom(
                top.Width,
                top.Height,
                SDL_PIXELFORMAT_RGBA32,
                pixels + top.Offset,
                top.Width * TexelBytes
            );
            saved = surface != null && SDL_SavePNG_IO(surface, io, closeio: false);
            SDL_DestroySurface(surface);
        }

        if (!saved)
        {
            string error = CString.Read(SDL_GetError());
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
