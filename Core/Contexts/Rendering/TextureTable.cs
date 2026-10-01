using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Contexts.Rendering;

/// <summary>
/// Every material texture on the GPU, in eight <c>Texture2DArray</c>s: {256, 512, 1024, 2048} × {sRGB, linear}, all
/// bound at once so a material picks its texture with a packed <c>(class &lt;&lt; 16) | layer</c> and the fragment stage
/// never rebinds (SDL GPU has no bindless, so this is what makes one indirect draw per class possible). Arrays start
/// small and double up to <see cref="MaxLayers"/>; a grown array waits in <see cref="PoolClass.Grown"/> until its layers are
/// copied over. Texture ids are reference counted; one that cannot be used keeps an entry too, as <see cref="Failed"/>,
/// so it hot reloads like any other. <see cref="Pending"/> is the pixels not uploaded yet.
/// </summary>
internal sealed class TextureTable
{
    public const int Classes = 8;

    /// <summary>No texture: the surface's factor stands alone.</summary>
    public const uint None = uint.MaxValue;

    /// <summary>A texture that did not load. Never a real reference: real ones keep the class (0..7) in the top 16 bits.</summary>
    public const uint Failed = uint.MaxValue - 1;

    /// <summary>
    /// The largest size, in pixels a side, a material texture is kept at on the GPU: the last of <see cref="PoolSizes"/>.
    /// A larger source is shrunk to it when it loads. Raising it (4096) keeps big textures sharp up close, at four
    /// times the VRAM for every texture in that class (about 22 MB a layer at 2048 with its mips, 89 MB at 4096);
    /// lowering it blurs anything authored larger and saves that memory. Lower it by dropping sizes from the end of
    /// <see cref="PoolSizes"/> as well, so no size is listed twice.
    /// </summary>
    public const int MaxSize = 2048;

    /// <summary>
    /// How many textures of one size and colour space can be on the GPU at once: the most layers one of the eight
    /// arrays grows to. A texture that arrives when its array is full draws as failed (and is logged), so raising it
    /// lets a scene use more distinct textures; nothing is paid up front, since arrays start at four layers and
    /// double as they fill. Lowering it caps VRAM sooner at the price of those failures. It cannot pass the GPU's
    /// array-layer limit (2048 on most), nor 65535: a material packs the layer into 16 bits.
    /// </summary>
    public const uint MaxLayers = 256;

    public static readonly int[] PoolSizes = [256, 512, 1024, MaxSize];

    public TextureTable(IRendering gpu, float anisotropy)
    {
        Sampler = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Repeat, Math.Clamp(anisotropy, 1f, 16f)));

        // Only the two 256 arrays exist from the start; a bigger class is created the first time a texture lands in it,
        // and until then its slot binds the 256 array of the same format. Nothing samples an absent class, so nothing sees
        // the difference; what is saved is ~180 MB of VRAM at start-up.
        for (int i = 0; i < Classes; i++)
        {
            int size = PoolSizes[i % 4];
            Pools[i] = new PoolClass { Size = size, Srgb = i < 4, Levels = 1 + (uint)Math.Log2(size) };
        }
    }

    /// <summary>The same for all eight arrays.</summary>
    public GpuSampler Sampler { get; }

    public PoolClass[] Pools { get; } = new PoolClass[Classes];

    /// <summary>Texture id to packed reference (or <see cref="Failed"/>).</summary>
    public RefCountTable<ulong, uint> Entries { get; } = new();

    /// <summary>The render textures among <see cref="Entries"/> (a material samples them): their layer and the size a camera draws them at.</summary>
    public Dictionary<ulong, RenderedTexture> Rendered { get; } = [];

    /// <summary>Fitted mip chains waiting for upload: class, layer, the pixels and where each level starts (the last offset ends the chain).</summary>
    public List<(int Class, uint Layer, byte[] Pixels, int[] Offsets)> Pending { get; } = [];

    public int Resident => Entries.Count;

    /// <summary>The packed reference of a texture already in the pool, without taking a reference; <see cref="None"/> otherwise.</summary>
    public uint Lookup(Handle<Assets.Texture> handle)
    {
        return Entries.TryGet(handle.Id, out uint packed) ? packed : None;
    }

    public bool Owns(ulong id)
    {
        return Entries.Contains(id);
    }

    public void FreeLayer(uint packed)
    {
        if (packed != Failed)
            Pools[packed >> 16].Free.Push(packed & 0xFFFF);
    }

    /// <summary>A layer in the class, making its array (or a bigger one) as needed; false when the class is at the cap.</summary>
    public bool TryAllocateLayer(IRendering gpu, PoolClass pool, out uint layer)
    {
        if (!pool.Texture.IsValid)
            Create(gpu, pool);

        if (pool.Free.Count > 0)
        {
            layer = pool.Free.Pop();
            return true;
        }

        uint capacity = pool.Grown.IsValid ? pool.GrownCapacity : pool.Capacity;
        if (pool.Used >= capacity)
        {
            if (capacity >= MaxLayers)
            {
                layer = 0;
                return false;
            }

            uint grown = Math.Min(MaxLayers, capacity * 2);
            gpu.Release(pool.Grown);
            pool.Grown = gpu.CreateTexture(new TextureDesc(Format(pool.Srgb), Usage(pool.Srgb), (uint)pool.Size, (uint)pool.Size, pool.Levels, grown));
            pool.GrownCapacity = grown;
        }

        layer = pool.Used++;
        return true;
    }

    /// <summary>The pool size a source of this size lands in, capped at <see cref="MaxSize"/>.</summary>
    public int SizeFor(int width, int height)
    {
        int largest = Math.Max(width, height);
        foreach (int size in PoolSizes)
        {
            if (size >= largest)
                return size;
        }

        return MaxSize;
    }

    public void Create(IRendering gpu, PoolClass pool)
    {
        pool.Capacity = 4;
        pool.Texture = gpu.CreateTexture(new TextureDesc(Format(pool.Srgb), Usage(pool.Srgb), (uint)pool.Size, (uint)pool.Size, pool.Levels, pool.Capacity));
    }

    private static GpuFormat Format(bool srgb)
    {
        return srgb ? GpuFormat.Rgba8Srgb : GpuFormat.Rgba8Unorm;
    }

    /// <summary>The sRGB arrays are also blit targets: render textures live in them, drawn into from Ldr (the same format).</summary>
    private static GpuTextureUsage Usage(bool srgb)
    {
        return srgb ? GpuTextureUsage.Sampler | GpuTextureUsage.ColorTarget : GpuTextureUsage.Sampler;
    }

    /// <summary>One array of the pool: its size and colour space, texture, layers in use and free, and a bigger array waiting for its copy.</summary>
    public sealed class PoolClass
    {
        public int Size { get; set; }

        public bool Srgb { get; set; }

        public uint Levels { get; set; }

        public GpuTexture Texture { get; set; }

        public uint Capacity { get; set; }

        public uint Used { get; set; }

        public Stack<uint> Free { get; } = [];

        public GpuTexture Grown { get; set; }

        public uint GrownCapacity { get; set; }
    }
}

/// <summary>A render texture's pool layer (<see cref="Packed"/>) and the size cameras draw it at.</summary>
internal readonly record struct RenderedTexture(uint Packed, int Width, int Height)
{
    public int Class => (int)(Packed >> 16);

    public uint Layer => Packed & 0xFFFF;
}
