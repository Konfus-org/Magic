using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
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
    private const int HeaderBytes = 28;
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
    public int Version => 2;

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
        BinaryPrimitives.WriteInt32LittleEndian(header, value.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], (int)value.Format);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], value.Levels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], value.Pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], value.Size);
        header[24] = (byte)value.Usage;
        header[25] = (byte)value.Filter;
        header[26] = (byte)value.Wrap;
        header[27] = (byte)((value.HasMipmaps ? 1 : 0) | (value.IsCompressed ? 2 : 0));
        bytes.Advance(HeaderBytes);
        foreach (TextureLevel level in value.Levels)
        {
            Span<byte> row = bytes.GetSpan(16);
            BinaryPrimitives.WriteInt32LittleEndian(row, level.Width);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], level.Height);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], level.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..], level.Size);
            bytes.Advance(16);
        }

        bytes.Write(value.Pixels.Span);
    }

    public Result<Texture> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
            return Result.Failure("the entry is cut short.");

        ReadOnlySpan<byte> header = bytes;
        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int pixelBytes = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        bytes = bytes[HeaderBytes..];
        if (levelCount < 0 || pixelBytes < 0 || bytes.Length < (levelCount * 16) + pixelBytes)
            return Result.Failure("the entry is cut short.");

        ImmutableArray<TextureLevel>.Builder levels = ImmutableArray.CreateBuilder<TextureLevel>(levelCount);
        for (int i = 0; i < levelCount; i++)
        {
            levels.Add(
                new TextureLevel(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[12..])
                )
            );
            bytes = bytes[16..];
        }

        return Result.Success(
            new Texture
            {
                Width = BinaryPrimitives.ReadInt32LittleEndian(header),
                Height = BinaryPrimitives.ReadInt32LittleEndian(header[4..]),
                Format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
                Pixels = bytes[..pixelBytes].ToArray(),
                Levels = levels.MoveToImmutable(),
                Size = BinaryPrimitives.ReadInt32LittleEndian(header[20..]),
                Usage = (TextureUsage)header[24],
                Filter = (TextureFilter)header[25],
                Wrap = (TextureWrap)header[26],
                HasMipmaps = (header[27] & 1) != 0,
                IsCompressed = (header[27] & 2) != 0,
            }
        );
    }

    private static Result<Texture> Import(in AssetFile file)
    {
        SDL_Surface* decoded = Decode(file.Path, file.Bytes.Span, file.Metadata.Get<int>(nameof(Texture.Size)));
        if (decoded == null)
            return Result.Failure($"{file.Path} did not decode: {CString.Read(SDL_GetError())}");

        // R, G, B, A bytes in memory whatever the machine's byte order: what the formats below are.
        SDL_Surface* top = SDL_ConvertSurface(decoded, SDL_PIXELFORMAT_RGBA32);
        SDL_DestroySurface(decoded);
        if (top == null)
            return Result.Failure($"{file.Path} did not convert to RGBA8: {CString.Read(SDL_GetError())}");

        Result<Texture> texture = CreateTexture(top, file);
        SDL_DestroySurface(top);

        return texture;
    }

    /// <summary>
    /// The top level of <paramref name="texture"/> as a PNG file's bytes; why not for a block-compressed one, whose texels
    /// are not there to write.
    /// </summary>
    private static Result<ReadOnlyMemory<byte>> Export(Texture texture)
    {
        if (texture.IsCompressed)
            return Result.Failure("A block-compressed texture is not written as a PNG.");

        TextureLevel top = texture.Levels[0];
        fixed (byte* pixels = texture.Pixels.Span)
        {
            SDL_Surface* surface = SDL_CreateSurfaceFrom(
                top.Width,
                top.Height,
                SDL_PIXELFORMAT_RGBA32,
                pixels + top.Offset,
                top.Width * TexelBytes
            );
            if (surface == null)
                return Result.Failure($"The texture did not make a surface: {CString.Read(SDL_GetError())}");

            Result<ReadOnlyMemory<byte>> png = EncodePng(surface);
            SDL_DestroySurface(surface);

            return png;
        }
    }

    /// <summary>
    /// <paramref name="surface"/> as a PNG file's bytes, written to a stream in memory and read back out of it.
    /// </summary>
    private static Result<ReadOnlyMemory<byte>> EncodePng(SDL_Surface* surface)
    {
        SDL_IOStream* io = SDL_IOFromDynamicMem();
        if (io == null)
            return Result.Failure($"No memory stream was made: {CString.Read(SDL_GetError())}");

        if (!SDL_SavePNG_IO(surface, io, closeio: false))
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
    /// The image in <paramref name="file"/>, an <c>.svg</c> drawn <paramref name="width"/> pixels wide (0: its own size);
    /// null when SDL_image cannot read it. SDL itself is called first, so SDL_image binds to the SDL every gem shares.
    /// </summary>
    private static SDL_Surface* Decode(string path, ReadOnlySpan<byte> file, int width)
    {
        fixed (byte* bytes = file)
        {
            SDL_IOStream* io = SDL_IOFromConstMem(bytes, (nuint)file.Length);
            if (io == null)
                return null;

            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // A height of 0 keeps it in proportion. Unlike the other loaders, this one leaves the stream open.
                SDL_Surface* drawn = IMG_LoadSizedSVG_IO(io, width, 0);
                SDL_CloseIO(io);

                return drawn;
            }

            // A .tga has no signature to be recognised by, so it is named.
            if (!path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                return IMG_Load_IO(io, closeio: true);

            fixed (byte* type = "TGA"u8)
            {
                return IMG_LoadTyped_IO(io, closeio: true, type);
            }
        }
    }

    /// <summary>
    /// <paramref name="top"/> as a texture, with the levels under it when its <c>.meta</c> asks, each half the one above,
    /// all in one array. Every surface made here is freed here; <paramref name="top"/> is the caller's.
    /// </summary>
    private static Result<Texture> CreateTexture(SDL_Surface* top, in AssetFile file)
    {
        bool hasMipmaps = file.Metadata.Get<bool>(nameof(Texture.HasMipmaps));
        bool isCompressed = file.Metadata.Get<bool>(nameof(Texture.IsCompressed));
        TextureUsage usage = file.Metadata.Get<TextureUsage>(nameof(Texture.Usage));
        int count = hasMipmaps ? 1 + BitOperations.Log2((uint)Math.Max(top->w, top->h)) : 1;
        ImmutableArray<TextureLevel>.Builder levels = ImmutableArray.CreateBuilder<TextureLevel>(count);
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            int width = Math.Max(1, top->w >> i);
            int height = Math.Max(1, top->h >> i);
            int size = isCompressed ? GetBlocks(width) * GetBlocks(height) * BlockBytes : width * height * TexelBytes;
            levels.Add(new TextureLevel(width, height, total, size));
            total += size;
        }

        byte[] pixels = GC.AllocateUninitializedArray<byte>(total);
        SDL_Surface* above = top;
        for (int i = 0; i < count; i++)
        {
            TextureLevel level = levels[i];
            SDL_Surface* surface =
                i == 0 ? top : SDL_ScaleSurface(above, level.Width, level.Height, SDL_SCALEMODE_LINEAR);
            if (above != top)
                SDL_DestroySurface(above);

            if (surface == null)
                return Result.Failure($"{file.Path} did not scale to mip {i}: {CString.Read(SDL_GetError())}");

            StoreLevel(surface, pixels, level, isCompressed);
            above = surface;
        }

        if (above != top)
            SDL_DestroySurface(above);

        return Result.Success(
            new Texture
            {
                Width = top->w,
                Height = top->h,
                Format = GetFormat(usage, isCompressed),
                Pixels = pixels,
                Levels = levels.MoveToImmutable(),
                Usage = usage,
                HasMipmaps = hasMipmaps,
                IsCompressed = isCompressed,
                Filter = file.Metadata.Get<TextureFilter>(nameof(Texture.Filter)),
                Wrap = file.Metadata.Get<TextureWrap>(nameof(Texture.Wrap)),
                Size = file.Metadata.Get<int>(nameof(Texture.Size)),
            }
        );
    }

    private static TextureFormat GetFormat(TextureUsage usage, bool isCompressed)
    {
        if (usage == TextureUsage.Color)
            return isCompressed ? TextureFormat.Bc7Srgb : TextureFormat.Rgba8Srgb;

        return isCompressed ? TextureFormat.Bc7Unorm : TextureFormat.Rgba8Unorm;
    }

    /// <summary>
    /// How many blocks cover <paramref name="texels"/> texels along one side.
    /// </summary>
    private static int GetBlocks(int texels) => (texels + BlockTexels - 1) / BlockTexels;

    /// <summary>
    /// <paramref name="surface"/>'s pixels at <paramref name="level"/>'s place in <paramref name="pixels"/>: as they are,
    /// or as BC7 blocks, a row of blocks to a worker.
    /// </summary>
    private static void StoreLevel(SDL_Surface* surface, byte[] pixels, TextureLevel level, bool isCompressed)
    {
        if (!isCompressed)
        {
            CopyRows(surface, pixels.AsSpan(level.Offset, level.Size));

            return;
        }

        byte[] texels = GC.AllocateUninitializedArray<byte>(level.Width * level.Height * TexelBytes);
        CopyRows(surface, texels);
        Parallel.For(0, GetBlocks(level.Height), row => CompressRow(texels, pixels, level, row));
    }

    /// <summary>
    /// <paramref name="surface"/>'s RGBA8 pixels as tightly packed rows, where SDL pads each to its pitch. A surface SDL
    /// made by converting or scaling needs no lock to be read.
    /// </summary>
    private static void CopyRows(SDL_Surface* surface, Span<byte> to)
    {
        int row = surface->w * TexelBytes;
        byte* pixels = (byte*)surface->pixels;
        for (int y = 0; y < surface->h; y++)
        {
            new ReadOnlySpan<byte>(pixels + (y * surface->pitch), row).CopyTo(to.Slice(y * row, row));
        }
    }

    /// <summary>
    /// One row of <paramref name="level"/>'s blocks into <paramref name="blocks"/>, a block past the level's edge repeating
    /// its last texels.
    /// </summary>
    private static void CompressRow(byte[] texels, byte[] blocks, TextureLevel level, int row)
    {
        bc7enc_compress_block_params options = _blockOptions;
        Span<byte> block = stackalloc byte[BlockTexels * BlockTexels * TexelBytes];
        int columns = GetBlocks(level.Width);
        for (int column = 0; column < columns; column++)
        {
            for (int y = 0; y < BlockTexels; y++)
            {
                int fromY = Math.Min((row * BlockTexels) + y, level.Height - 1);
                for (int x = 0; x < BlockTexels; x++)
                {
                    int fromX = Math.Min((column * BlockTexels) + x, level.Width - 1);
                    texels
                        .AsSpan(((fromY * level.Width) + fromX) * TexelBytes, TexelBytes)
                        .CopyTo(block[(((y * BlockTexels) + x) * TexelBytes)..]);
                }
            }

            fixed (byte* to = &blocks[level.Offset + (((row * columns) + column) * BlockBytes)])
            fixed (byte* from = block)
            {
                bc7enc_compress_block(to, from, &options);
            }
        }
    }

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
