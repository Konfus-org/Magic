using System.IO.Hashing;
using System.Numerics;
using System.Threading.Channels;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The textures materials sample, in eight texture arrays every surface pipeline binds at once, since a GPU without
/// bindless textures cannot pick one by an index: colour (sRGB) or data (linear), each 256, 512, 1024 or 2048 texels
/// square, with its mips down to a 4 x 4 block, compressed to BC7, a quarter of the memory and of the reads of eight
/// bits a channel. A
/// texture is resampled on a worker into the smallest that holds it (2048 for anything bigger), compressed and kept
/// in the cache, so it is compressed once, and takes a layer of it; a surface names it by <c>TextureRef</c>,
/// <c>(pool &lt;&lt; 16) | layer</c> (Shaders/Include/SurfaceBindings.hlsli). A pool grows by doubling its layers,
/// copying the old on the GPU, so a layer that copy will write is not uploaded into until the frame after. Render
/// thread.
/// </summary>
internal sealed class TexturePools : IDisposable
{
    /// <summary>
    /// What a <c>TextureRef</c> holds for no texture: a surface's factor alone.
    /// </summary>
    public const uint None = uint.MaxValue;

    public const int Count = 8;

    /// <summary>
    /// The pools' names, which the frame binds them by and a surface's shader reads them by
    /// (Shaders/Include/SurfaceBindings.hlsli): colour (sRGB) then data (linear), each 256, 512, 1024 and 2048 texels a
    /// side.
    /// </summary>
    public static readonly string[] Names = ["PoolSrgb256", "PoolSrgb512", "PoolSrgb1024", "PoolSrgb2048", "PoolLinear256", "PoolLinear512", "PoolLinear1024", "PoolLinear2048"];

    /// <summary>
    /// Raised whenever how a texture is resampled or compressed changes, so the cache's are made again.
    /// </summary>
    public const int CacheVersion = 2;

    private static readonly int[] _sizes = [256, 512, 1024, 2048];

    /// <summary>
    /// Texels a side of a pool's smallest mip: a block of BC7, as a level smaller than a block cannot be copied when a
    /// pool grows.
    /// </summary>
    private const int SmallestMip = 4;

    private readonly IGpu _gpu;
    private readonly Assets _assets;
    private readonly Caches _caches;
    private readonly CancellationToken _stopping;

    /// <summary>
    /// Textures prepared for their pool, or that did not load (no levels), for the render thread to place as
    /// <see cref="Flush"/> runs.
    /// </summary>
    private readonly Channel<Arrival> _arrived = Channel.CreateUnbounded<Arrival>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Pool[] _pools = new Pool[Count];
    private readonly Dictionary<Handle<Texture>, Resident> _textures = [];

    /// <summary>
    /// Uploads held back a frame, as they write layers a pool's growing copies this frame.
    /// </summary>
    private readonly List<(Handle<Texture> Texture, byte[][] Levels)> _heldBack = [];

    private readonly GpuTexture _empty;

    private readonly ClusterRenderingSettings _settings;

    /// <summary>
    /// The taps the sampler filters with at a grazing angle, 1 to 16.
    /// </summary>
    private float _anisotropy;

    public TexturePools(IGpu gpu, Assets assets, Caches caches, ClusterRenderingSettings settings, CancellationToken stopping)
    {
        _gpu = gpu;
        _assets = assets;
        _caches = caches;
        _settings = settings;
        _stopping = stopping;
        for (int i = 0; i < Count; i++)
        {
            _pools[i] = new Pool(i < 4 ? GpuFormat.Bc7Srgb : GpuFormat.Bc7Unorm, (uint)_sizes[i % 4]);
        }

        _empty = gpu.CreateTexture(new GpuTextureDescription(GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled, 1, 1, 1, 1, GpuTextureKind.Texture2DArray));
        gpu.Upload(new GpuTextureRegion(_empty), [255, 255, 255, 255]);
        _anisotropy = Math.Clamp(settings.Anisotropy, 1, 16);
        Sampler = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, _anisotropy));
    }

    public void Dispose()
    {
        foreach (Pool pool in _pools)
        {
            _gpu.Release(pool.Texture);
        }

        _gpu.Release(_empty);
        _gpu.Release(Sampler);
    }

    public GpuSampler Sampler { get; private set; }

    /// <summary>
    /// The textures taken in, or failed, this frame, until it ends: whose models' cards and materials are worked out
    /// anew.
    /// </summary>
    public HashSet<Handle<Texture>> Changed { get; } = [];

    /// <summary>
    /// Filters with as many taps as the settings now say, 1 to 16, from now on: a sampler made again when that changed,
    /// the old one released. Render thread, each frame.
    /// </summary>
    public void FollowSettings()
    {
        float clamped = Math.Clamp(_settings.Anisotropy, 1, 16);
        if (clamped == _anisotropy)
        {
            return;
        }

        _anisotropy = clamped;
        _gpu.Release(Sampler);
        Sampler = _gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Repeat, clamped));
    }

    /// <summary>
    /// <paramref name="texture"/> as a surface names it, once it is in; starts loading it the first time it is asked for,
    /// and is <see cref="None"/> while it loads. Whether it failed is <see cref="HasFailed"/>.
    /// </summary>
    public uint RefOf(Handle<Texture> texture)
    {
        if (texture == Handle<Texture>.None)
        {
            return None;
        }

        if (_textures.TryGetValue(texture, out Resident? resident))
        {
            return resident.Ref;
        }

        _textures[texture] = new Resident();
        LoadAsync(texture).FireAndForget();
        return None;
    }

    public bool HasFailed(Handle<Texture> texture) => _textures.TryGetValue(texture, out Resident? resident) && resident.HasFailed;

    /// <summary>
    /// Lets the texture go once no material samples it: its layer goes back to its pool, for the next texture to take,
    /// and it loads again if it is asked for again.
    /// </summary>
    public void Release(Handle<Texture> texture)
    {
        if (!_textures.Remove(texture, out Resident? resident))
        {
            return;
        }

        Changed.Remove(texture);
        if (resident.Ref != None)
        {
            _pools[resident.Ref >> 16].Free(resident.Ref & 0xFFFF);
        }
    }

    /// <summary>
    /// The texture's file changed and it loaded again: it is resampled into the layer it has.
    /// </summary>
    public void Reload(Texture texture)
    {
        if (_textures.ContainsKey(texture.Handle))
        {
            PrepareAsync(texture).FireAndForget();
        }
    }

    /// <summary>
    /// A pool's texture as it stands, an empty one while it holds nothing.
    /// </summary>
    public GpuTexture TextureOf(int pool)
    {
        return _pools[pool].Texture == GpuTexture.None ? _empty : _pools[pool].Texture;
    }

    /// <summary>
    /// Takes in what workers finished since the last frame: each texture prepared is placed in its pool, and each that
    /// did not load is counted failed.
    /// </summary>
    public void Flush()
    {
        while (_arrived.Reader.TryRead(out Arrival arrival))
        {
            if (arrival.Levels is { } levels)
            {
                Place(arrival.Texture, arrival.Pool, levels);
            }
            else
            {
                Fail(arrival.Texture);
            }
        }
    }

    /// <summary>
    /// Ends the frame: what changed this frame is forgotten, and the uploads held back from a pool's growing go in now.
    /// </summary>
    public void EndFrame()
    {
        Changed.Clear();
        foreach (Pool pool in _pools)
        {
            pool.HasGrown = false;
        }

        foreach ((Handle<Texture> texture, byte[][] levels) in _heldBack)
        {
            Upload(texture, levels);
        }

        _heldBack.Clear();
    }

    private async Task LoadAsync(Handle<Texture> handle)
    {
        Result<Texture> loaded = await _assets.LoadAsync(handle, _stopping).ConfigureAwait(false);
        if (!loaded.Ok)
        {
            Debugging.Error($"A material's texture does not load, so its material is drawn as the failure: {loaded.Error}");
            _arrived.Writer.TryWrite(new Arrival(handle, 0, null));
            return;
        }

        await PrepareAsync(loaded.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// The texture's levels at its pool's size, compressed, from the cache or made on a worker, for the render thread to
    /// place.
    /// </summary>
    private async Task PrepareAsync(Texture texture)
    {
        int pool = PoolOf(texture);
        byte[][] levels = await Task.Run(() => ReadOrCompressAsync(texture, pool), _stopping).ConfigureAwait(false);
        _arrived.Writer.TryWrite(new Arrival(texture.Handle, pool, levels));
    }

    /// <summary>
    /// What the cache keeps of the texture for its pool, or its levels resampled and compressed, then kept. A texture with
    /// no file to be versioned by is kept by its pixels.
    /// </summary>
    private async Task<byte[][]> ReadOrCompressAsync(Texture texture, int pool)
    {
        ulong version = _assets.Version(texture.Handle);
        version = (version != 0 ? version : XxHash3.HashToUInt64(texture.Pixels.Span)) ^ (ulong)pool;
        if (await _caches.ReadAsync<CompressedTexture>(texture.Handle.Id, version, _stopping).ConfigureAwait(false) is { Ok: true } cached)
        {
            return cached.Value.Levels;
        }

        byte[][] levels = Compress(Resample(texture, (uint)_sizes[pool % 4]), (uint)_sizes[pool % 4]);
        await _caches.WriteAsync(texture.Handle.Id, version, new CompressedTexture(levels), _stopping).ConfigureAwait(false);
        return levels;
    }

    /// <summary>
    /// Each level of a <paramref name="size"/> square compressed to BC7, the encoder's quickest search, which is still
    /// far from the blocks a slower one finds only in the sharpest edges.
    /// </summary>
    private static byte[][] Compress(byte[][] levels, uint size)
    {
        BcEncoder encoder = new();
        encoder.OutputOptions.Format = CompressionFormat.Bc7;
        encoder.OutputOptions.Quality = CompressionQuality.Fast;
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.Options.IsParallel = true;
        byte[][] compressed = new byte[levels.Length][];
        for (int level = 0; level < levels.Length; level++)
        {
            int side = (int)Math.Max(1, size >> level);
            compressed[level] = encoder.EncodeToRawBytes(levels[level], side, side, PixelFormat.Rgba32)[0];
        }

        return compressed;
    }

    private void Fail(Handle<Texture> texture)
    {
        if (_textures.TryGetValue(texture, out Resident? resident))
        {
            resident.HasFailed = true;
            Changed.Add(texture);
        }
    }

    /// <summary>
    /// Gives the texture a layer of its pool, unless it has one there, and uploads its levels into it.
    /// </summary>
    private void Place(Handle<Texture> texture, int pool, byte[][] levels)
    {
        if (!_textures.TryGetValue(texture, out Resident? resident))
        {
            return;
        }

        if (resident.Ref == None || (int)(resident.Ref >> 16) != pool)
        {
            if (resident.Ref != None)
            {
                _pools[resident.Ref >> 16].Free(resident.Ref & 0xFFFF);
            }

            resident.Ref = ((uint)pool << 16) | (uint)_pools[pool].Allocate(_gpu);
        }

        resident.HasFailed = false;
        Changed.Add(texture);
        Upload(texture, levels);
    }

    private void Upload(Handle<Texture> texture, byte[][] levels)
    {
        if (!_textures.TryGetValue(texture, out Resident? resident) || resident.Ref == None)
        {
            return;
        }

        Pool pool = _pools[resident.Ref >> 16];
        uint layer = resident.Ref & 0xFFFF;
        if (pool.HasGrown && layer < pool.Copied)
        {
            _heldBack.Add((texture, levels));
            return;
        }

        for (int level = 0; level < levels.Length; level++)
        {
            _gpu.Upload(new GpuTextureRegion(pool.Texture, (uint)level, layer), levels[level]);
        }
    }

    /// <summary>
    /// The pool for a texture: colour or data by its format, and the smallest size that holds it.
    /// </summary>
    private static int PoolOf(Texture texture)
    {
        int largest = Math.Max(texture.Width, texture.Height);
        int size = 0;
        while (size < _sizes.Length - 1 && _sizes[size] < largest)
        {
            size++;
        }

        return (texture.Format == GpuFormat.Rgba8Srgb ? 0 : 4) + size;
    }

    /// <summary>
    /// Every mip of a <paramref name="size"/> square from the texture's levels: each from the smallest of the texture's
    /// levels at least as big, a box of texels averaged where it shrinks and the nearest texel where it grows, so a small
    /// pattern stays crisp. Averaging sRGB bytes is not quite averaging light, which a mip of a colour texture does not
    /// show.
    /// </summary>
    private static byte[][] Resample(Texture texture, uint size)
    {
        int count = LevelsOf(size);
        byte[][] levels = new byte[count][];
        for (int level = 0; level < count; level++)
        {
            int side = (int)Math.Max(1, size >> level);
            TextureLevel source = texture.Levels[0];
            foreach (TextureLevel candidate in texture.Levels)
            {
                if (candidate.Width >= side && candidate.Height >= side)
                {
                    source = candidate;
                }
            }

            levels[level] = ResampleLevel(texture.Pixels.Span.Slice(source.Offset, source.Size), source.Width, source.Height, side);
        }

        return levels;
    }

    /// <summary>
    /// Mips of a pool of <paramref name="size"/> texels a side, down to <see cref="SmallestMip"/>.
    /// </summary>
    private static int LevelsOf(uint size) => BitOperations.Log2(size) - BitOperations.Log2(SmallestMip) + 1;

    private static byte[] ResampleLevel(ReadOnlySpan<byte> pixels, int width, int height, int side)
    {
        byte[] resampled = new byte[side * side * 4];
        int boxX = Math.Max(1, width / side);
        int boxY = Math.Max(1, height / side);
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int fromX = (int)((long)x * width / side);
                int fromY = (int)((long)y * height / side);
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < boxY; dy++)
                    {
                        for (int dx = 0; dx < boxX; dx++)
                        {
                            int texel = (Math.Min(fromY + dy, height - 1) * width) + Math.Min(fromX + dx, width - 1);
                            sum += pixels[(texel * 4) + channel];
                        }
                    }

                    resampled[(((y * side) + x) * 4) + channel] = (byte)(sum / (boxX * boxY));
                }
            }
        }

        return resampled;
    }

    /// <summary>
    /// A texture as the pools hold it: its reference, <see cref="None"/> until it is in.
    /// </summary>
    private sealed class Resident
    {
        public uint Ref { get; set; } = None;

        public bool HasFailed { get; set; }
    }

    /// <summary>
    /// One texture array: a format and a square size, its layers handed out in order, those let go first, doubled when
    /// they run out.
    /// </summary>
    private sealed class Pool(GpuFormat format, uint size)
    {
        /// <summary>
        /// Layers let go, which the next textures take before any new one.
        /// </summary>
        private readonly Stack<uint> _free = new();

        private uint _capacity;
        private uint _used;

        public GpuTexture Texture { get; private set; }

        /// <summary>
        /// It grew this frame, so its first <see cref="Copied"/> layers are written by the copy from the old array.
        /// </summary>
        public bool HasGrown { get; set; }

        public uint Copied { get; private set; }

        public int Allocate(IGpu gpu)
        {
            if (_free.TryPop(out uint layer))
            {
                return (int)layer;
            }

            if (_used == _capacity)
            {
                Grow(gpu, Math.Max(4, _capacity * 2));
            }

            return (int)_used++;
        }

        public void Free(uint layer)
        {
            _free.Push(layer);
        }

        private void Grow(IGpu gpu, uint capacity)
        {
            uint levels = (uint)LevelsOf(size);
            GpuTexture grown = gpu.CreateTexture(new GpuTextureDescription(format, GpuTextureUsage.Sampled, size, size, levels, capacity, GpuTextureKind.Texture2DArray));
            for (uint layer = 0; layer < _used; layer++)
            {
                for (uint level = 0; level < levels; level++)
                {
                    gpu.Commands.CopyTexture(new GpuTextureRegion(Texture, level, layer), new GpuTextureRegion(grown, level, layer));
                }
            }

            if (Texture != GpuTexture.None)
            {
                gpu.Release(Texture);
            }

            HasGrown = _used > 0;
            Copied = _used;
            Texture = grown;
            _capacity = capacity;
        }
    }

    /// <summary>
    /// What a worker finished for a texture: its levels compressed for the pool at index <see cref="Pool"/>, or none
    /// when it did not load.
    /// </summary>
    private readonly record struct Arrival(Handle<Texture> Texture, int Pool, byte[][]? Levels);
}

/// <summary>
/// A texture as its pool holds it and the cache keeps it: each level's BC7 blocks.
/// </summary>
internal sealed record CompressedTexture(byte[][] Levels);
