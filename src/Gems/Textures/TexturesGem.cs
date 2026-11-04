using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Magic.Caching;
using Magic.Files.Pipeline;
using Magic.Files.Types;
using Magic.Plugins;
using Magic.Utils;
using StbImageSharp;

namespace Textures;

/// <summary>
/// Reads image files into textures ready to upload: decoded, their mip chain made, and every level packed into BC7
/// blocks, in sRGB for colour and linear for normals and masks. The blocks are kept in the cache under the file's
/// version, so an image is encoded once per change. It is public because nothing in its assembly makes it: Magic
/// does, by reflection, when a project lists the gem.
/// </summary>
public sealed class TexturesGem(Caches caches) : IGem, IAssetLoader<Texture>, ICache<Texture>
{
    private const int HeaderBytes = 24;

    public ImmutableArray<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    /// <summary>
    /// Raised when the bytes written change shape, or the levels are encoded otherwise.
    /// </summary>
    public int Version => 1;

    public async Task<Result<Texture>> LoadAsync(
        AssetFile file,
        IProgress<LoadProgress>? progress,
        CancellationToken cancel
    )
    {
        TextureUsage usage = file.Metadata.Get<TextureUsage>(nameof(Texture.Usage));
        bool hasMipmaps = file.Metadata.Get<bool>(nameof(Texture.HasMipmaps));
        Result<Texture> cached = await caches.ReadAsync(file.Id, file.Version, this, cancel).ConfigureAwait(false);
        Result<Texture> texture = cached.Ok ? cached : Encode(file.Path, file.Bytes.Span, usage, hasMipmaps);
        if (!texture.Ok)
            return texture;

        if (!cached.Ok)
            await caches.WriteAsync(file.Id, file.Version, texture.Value, this, cancel).ConfigureAwait(false);

        Texture made = texture.Value;
        made.Usage = usage;
        made.HasMipmaps = hasMipmaps;
        made.Filter = file.Metadata.Get<TextureFilter>(nameof(Texture.Filter));
        made.Wrap = file.Metadata.Get<TextureWrap>(nameof(Texture.Wrap));
        made.Size = file.Metadata.Get<int>(nameof(Texture.Size));

        return Result.Success(made);
    }

    public void Write(Texture value, IBufferWriter<byte> bytes)
    {
        Span<byte> header = bytes.GetSpan(HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header, value.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], value.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], (int)value.Format);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], value.Levels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], value.Pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], 0);
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

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        TextureFormat format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int pixelBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
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
                Width = width,
                Height = height,
                Format = format,
                Pixels = bytes[..pixelBytes].ToArray(),
                Levels = levels.MoveToImmutable(),
            }
        );
    }

    /// <summary>
    /// The file decoded and its levels encoded into BC7 blocks, level 0 first.
    /// </summary>
    private static Result<Texture> Encode(string path, ReadOnlySpan<byte> file, TextureUsage usage, bool hasMipmaps)
    {
        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(file.ToArray(), ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Result.Failure($"{path} did not decode: {exception.Message}");
        }

        if (image.Width <= 0 || image.Height <= 0)
            return Result.Failure($"{path} has no pixels.");

        BcEncoder encoder = new();
        encoder.OutputOptions.GenerateMipMaps = hasMipmaps;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = CompressionFormat.Bc7;
        byte[][] encoded = encoder.EncodeToRawBytes(image.Data, image.Width, image.Height, PixelFormat.Rgba32);

        ImmutableArray<TextureLevel>.Builder levels = ImmutableArray.CreateBuilder<TextureLevel>(encoded.Length);
        int total = 0;
        for (int i = 0; i < encoded.Length; i++)
        {
            levels.Add(
                new TextureLevel(
                    Math.Max(image.Width >> i, 1),
                    Math.Max(image.Height >> i, 1),
                    total,
                    encoded[i].Length
                )
            );
            total += encoded[i].Length;
        }

        byte[] pixels = new byte[total];
        foreach (TextureLevel level in levels)
        {
            encoded[levels.IndexOf(level)].CopyTo(pixels, level.Offset);
        }

        return Result.Success(
            new Texture
            {
                Width = image.Width,
                Height = image.Height,
                Format = usage == TextureUsage.Color ? TextureFormat.Bc7Srgb : TextureFormat.Bc7Unorm,
                Pixels = pixels,
                Levels = levels.MoveToImmutable(),
            }
        );
    }
}
