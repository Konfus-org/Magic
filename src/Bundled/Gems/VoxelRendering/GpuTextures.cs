using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The textures materials sample and cards show, each a GPU texture of its own, which a surface names by its heap id
/// (<c>TextureRef</c>, Shaders/Include/Surface.hlsli), and the sampler they are all read through. What is kept is what
/// is held: <see cref="Record"/> holds a texture, starting its load the first time, and hands back its id once it is in;
/// <see cref="Release"/> lets one hold go, and <see cref="Purge"/> lets go of whatever nothing holds any more, once a
/// frame. Each texture loads on a worker, is resampled into the smallest square of a few sizes that holds it, with its
/// mips down to a block, and compressed to BC7, a quarter of the memory and of the reads of eight bits a channel, which
/// the cache keeps, so it is compressed once; it is taken in on the render thread as <see cref="Take"/> runs. Render
/// thread.
/// </summary>
internal sealed class GpuTextures : IDisposable
{
    /// <summary>
    /// What a surface reads as no texture: Shaders/Include/Surface.hlsli's <c>TextureNone</c>.
    /// </summary>
    internal const uint None = 0xFFFFFFFFu;

    /// <summary>
    /// Texels a side of a texture's smallest mip: a block of BC7.
    /// </summary>
    private const int SmallestMip = 4;

    /// <summary>
    /// The sizes a texture is resampled into, texels a side: the smallest that holds it, the largest for anything bigger.
    /// </summary>
    private static readonly int[] _sizes = [256, 512, 1024, 2048];

    private readonly IGpuDriver _gpu;
    private readonly Assets _assets;
    private readonly Caches _caches;
    private readonly VoxelRenderingSettings _settings;
    private readonly CancellationToken _stopping;
    private readonly Resident<Texture, TextureEntry, PreparedTexture> _textures;

    /// <summary>
    /// The taps the sampler filters with at a grazing angle, 1 to 16, as it was made.
    /// </summary>
    private float _anisotropy;

    internal GpuTextures(
        IGpuDriver gpu,
        Assets assets,
        Caches caches,
        VoxelRenderingSettings settings,
        CancellationToken stopping)
    {
        _gpu = gpu;
        _assets = assets;
        _caches = caches;
        _settings = settings;
        _stopping = stopping;
        _textures = new(
            assets,
            "A material's texture does not load, so its material is drawn as the failure",
            PrepareAsync,
            stopping);
        _anisotropy = Math.Clamp(settings.Anisotropy, 1, 16);
        Sampler = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, _anisotropy));
    }

    /// <summary>
    /// The textures taken in, or failed, this frame, until it ends: whose models' cards and materials are worked out
    /// anew.
    /// </summary>
    internal HashSet<Handle<Texture>> Changed { get; } = [];

    /// <summary>
    /// What every texture a surface names is read through: linear, wrapping, with as many taps as the settings say.
    /// </summary>
    internal GpuSampler Sampler { get; private set; }

    public void Dispose()
    {
        foreach (TextureEntry entry in _textures.Entries.Values)
        {
            _gpu.Release(entry.Texture);
        }

        _gpu.Release(Sampler);
    }

    /// <summary>
    /// Holds <paramref name="texture"/>, starting its load the first time: its id, <see cref="None"/> while it loads.
    /// </summary>
    internal uint Record(Handle<Texture> texture)
    {
        if (texture == Handle<Texture>.None) return None;

        return GetRef(_textures.Record(texture, static () => new TextureEntry()));
    }

    internal void Release(Handle<Texture> texture)
    {
        if (texture != Handle<Texture>.None) _textures.Release(texture);
    }

    /// <summary>
    /// The texture as a surface names it once it is in; <see cref="None"/> until then, or when nothing holds it.
    /// </summary>
    internal uint GetRef(Handle<Texture> texture)
        => _textures.Entries.TryGetValue(texture, out TextureEntry? entry) ? GetRef(entry) : None;

    internal bool HasFailed(Handle<Texture> texture)
        => _textures.Entries.TryGetValue(texture, out TextureEntry? entry) && entry.HasFailed;

    /// <summary>
    /// Lets go of every texture nothing holds any more.
    /// </summary>
    internal void Purge() => _textures.Purge(Evict);

    /// <summary>
    /// Follows the settings' filtering, then takes in what workers finished since the last frame: each texture prepared
    /// made on the GPU in place of what it had, and each that did not load counted failed.
    /// </summary>
    internal void Take()
    {
        float anisotropy = Math.Clamp(_settings.Anisotropy, 1, 16);
        if (anisotropy != _anisotropy)
        {
            _anisotropy = anisotropy;
            _gpu.Release(Sampler);
            Sampler = _gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, anisotropy));
        }

        _textures.Take(TakeTexture);
    }

    /// <summary>
    /// Ends the frame: the textures that changed this frame are forgotten.
    /// </summary>
    internal void EndFrame() => Changed.Clear();

    /// <summary>
    /// The texture's file changed and it loaded again: it is prepared again, and taken in in place of what it had.
    /// </summary>
    internal void Reload(Texture texture) => _textures.Reload(texture);

    private static uint GetRef(TextureEntry entry) => entry.Texture == GpuTexture.None ? None : entry.Texture.Id;

    /// <summary>
    /// The size a texture is resampled into: the smallest that holds it.
    /// </summary>
    private static uint GetSize(Texture texture)
    {
        int largest = Math.Max(texture.Width, texture.Height);
        int size = 0;
        while (size < _sizes.Length - 1 && _sizes[size] < largest)
        {
            size++;
        }

        return (uint)_sizes[size];
    }

    /// <summary>
    /// Mips of a texture of <paramref name="size"/> texels a side, down to <see cref="SmallestMip"/>.
    /// </summary>
    private static int GetMipCount(uint size) => BitOperations.Log2(size) - BitOperations.Log2(SmallestMip) + 1;

    /// <summary>
    /// The texture's levels at its size, compressed, from the cache or made on a worker, for the render thread to make.
    /// </summary>
    private async Task<PreparedTexture> PrepareAsync(Texture texture)
    {
        uint size = GetSize(texture);
        byte[][] levels = await Task.Run(() => ReadOrCompressAsync(texture, size), _stopping).ConfigureAwait(false);

        return new PreparedTexture(texture.Format == GpuFormat.Rgba8Srgb, size, levels);
    }

    /// <summary>
    /// What the cache keeps of the texture at its size, or its levels resampled and compressed, then kept. A texture
    /// with no file to be versioned by is kept by its pixels.
    /// </summary>
    private async Task<byte[][]> ReadOrCompressAsync(Texture texture, uint size)
    {
        ulong version = _assets.GetVersion(texture.Handle);
        version = (version != 0 ? version : XxHash3.HashToUInt64(texture.Pixels.Span)) ^ size;

        if (await _caches.ReadAsync<BlockTexture>(texture.Handle.Id, version, _stopping)
            .ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached.Value.Levels;
        }

        byte[][] levels = GpuConverter.Convert(texture, size, GetMipCount(size));
        await _caches.WriteAsync(texture.Handle.Id, version, new BlockTexture(levels), _stopping).ConfigureAwait(false);

        return levels;
    }

    /// <summary>
    /// A texture prepared, made on the GPU in place of what it had, or that did not load (none), counted failed.
    /// </summary>
    private void TakeTexture(Handle<Texture> texture, TextureEntry entry, PreparedTexture? prepared)
    {
        Changed.Add(texture);
        if (prepared is null)
        {
            entry.HasFailed = true;

            return;
        }

        _gpu.Release(entry.Texture);
        GpuTexture made = _gpu.CreateTexture(new GpuTextureDescription(
            prepared.IsColor ? GpuFormat.Bc7Srgb : GpuFormat.Bc7Unorm,
            GpuTextureUsage.Sampled,
            prepared.Size,
            prepared.Size,
            (uint)prepared.Levels.Length));
        for (int level = 0; level < prepared.Levels.Length; level++)
        {
            _gpu.Upload(new GpuTextureRegion(made, (uint)level), prepared.Levels[level]);
        }

        entry.Texture = made;
        entry.HasFailed = false;
    }

    /// <summary>
    /// The texture leaves, and its GPU texture with it.
    /// </summary>
    private void Evict(Handle<Texture> texture, TextureEntry entry)
    {
        Changed.Remove(texture);
        _gpu.Release(entry.Texture);
    }

    /// <summary>
    /// A texture as it is held: its GPU texture once it is in.
    /// </summary>
    private sealed class TextureEntry
    {
        internal GpuTexture Texture { get; set; }

        internal bool HasFailed { get; set; }
    }

    /// <summary>
    /// A texture made ready on a worker: whether it is colour (sRGB), its size, and its levels compressed.
    /// </summary>
    private sealed record PreparedTexture(bool IsColor, uint Size, byte[][] Levels);
}

/// <summary>
/// A texture as the cache keeps it: each level's BC7 blocks.
/// </summary>
internal sealed record BlockTexture(byte[][] Levels)
{
    /// <summary>
    /// Raised whenever how a texture is resampled or compressed changes, so the cache's are made again.
    /// </summary>
    internal const int CacheVersion = 1;

    internal static Result<BlockTexture> Read(Stream bytes)
    {
        using BinaryReader reader = new(bytes, Encoding.UTF8, leaveOpen: true);
        byte[][] levels = new byte[reader.ReadInt32()][];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = reader.ReadBytes(reader.ReadInt32());
        }

        return Result.Success(new BlockTexture(levels));
    }

    /// <summary>
    /// The levels as the cache keeps them: how many, then each one's length and its bytes.
    /// </summary>
    internal void Write(Stream bytes)
    {
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(Levels.Length);
        foreach (byte[] level in Levels)
        {
            writer.Write(level.Length);
            writer.Write(level);
        }
    }
}
