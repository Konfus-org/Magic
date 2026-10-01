using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Utils;

namespace Magic.Systems.Rendering;

/// <summary>
/// Material textures into the <see cref="TextureTable"/> pools: loading, fitting each to its pool's square power-of-two size
/// with a full mip chain, and the uploads. Smaller sources are enlarged with nearest sampling (an 8x8 checkerboard stays
/// crisp), larger or non-square ones shrunk with a box filter; mips are 2x2 box averages of the level above. A source already
/// at the size with its whole chain keeps the loader's mips. Bytes keep the source's colour space.
/// </summary>
internal static class Textures
{
    /// <summary>A packed reference for the texture, loading it the first time; <see cref="TextureTable.Failed"/> when it cannot be used.</summary>
    public static uint Acquire(RenderContext ctx, Handle<Texture> handle)
    {
        if (!handle.IsValid)
            return TextureTable.None;

        if (ctx.Textures.Entries.TryAcquire(handle.Id, out uint packed))
            return packed;

        packed = Load(ctx, handle.Id);
        ctx.Textures.Entries.Add(handle.Id, packed);
        return packed;
    }

    public static void Release(RenderContext ctx, Handle<Texture> handle)
    {
        if (!ctx.Textures.Entries.Release(handle.Id, out uint packed))
            return;

        ctx.Textures.FreeLayer(packed);
        ctx.Textures.Rendered.Remove(handle.Id);
    }

    /// <summary>The texture changed on disk: uploaded into a layer again (a fixed texture gets one, a broken one loses its). Materials rebuild their records after.</summary>
    public static void Reload(RenderContext ctx, ulong id)
    {
        if (!ctx.Textures.Entries.TryGet(id, out uint packed))
            return;

        ctx.Textures.FreeLayer(packed);
        ctx.Textures.Rendered.Remove(id);
        ctx.Textures.Entries.Set(id, Load(ctx, id));
    }

    /// <summary>Copies grown arrays over and uploads what was acquired since the last frame.</summary>
    public static void Flush(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        TextureTable table = ctx.Textures;
        for (int i = 0; i < TextureTable.Classes; i++)
        {
            TextureTable.PoolClass pool = table.Pools[i];
            if (!pool.Grown.IsValid)
                continue;

            // Layers past the old capacity were allocated after the grow: they only exist in the new array.
            uint copied = Math.Min(pool.Used, pool.Capacity);
            for (uint layer = 0; layer < copied; layer++)
            {
                uint width = (uint)pool.Size;
                for (uint level = 0; level < pool.Levels; level++)
                {
                    gpu.Copy(new TextureRegion(pool.Texture, level, layer, 0, 0, width, width), new TextureRegion(pool.Grown, level, layer, 0, 0, width, width));
                    width = Math.Max(1, width / 2);
                }
            }

            gpu.Release(pool.Texture);
            pool.Texture = pool.Grown;
            pool.Capacity = pool.GrownCapacity;
            pool.Grown = default;
            Debugging.Log.Debug($"Texture pool class {i} grown to {pool.Capacity} layers.");
        }

        foreach ((int classIndex, uint layer, byte[] pixels, int[] offsets) in table.Pending)
        {
            TextureTable.PoolClass pool = table.Pools[classIndex];
            uint width = (uint)pool.Size;
            for (int level = 0; level < offsets.Length - 1; level++)
            {
                ReadOnlySpan<byte> bytes = pixels.AsSpan(offsets[level], offsets[level + 1] - offsets[level]);
                gpu.Upload(new TextureRegion(pool.Texture, (uint)level, layer, 0, 0, width, width), bytes);
                width = Math.Max(1, width / 2);
            }
        }

        table.Pending.Clear();
    }

    /// <summary>All eight arrays with the one sampler, t0..t7 of the fragment stage; an absent class binds the 256 array of its format.</summary>
    public static void Bindings(RenderContext ctx, Span<GpuBinding> bindings)
    {
        TextureTable table = ctx.Textures;
        for (int i = 0; i < TextureTable.Classes; i++)
        {
            GpuTexture texture = table.Pools[i].Texture.IsValid ? table.Pools[i].Texture : table.Pools[i < 4 ? 0 : 4].Texture;
            bindings[i] = new GpuBinding(Texture: texture, Sampler: table.Sampler);
        }
    }

    /// <summary>Makes the two 256 arrays, which every absent class binds in its place.</summary>
    public static void CreateBase(RenderContext ctx)
    {
        ctx.Textures.Create(ctx.Gpu, ctx.Textures.Pools[0]);
        ctx.Textures.Create(ctx.Gpu, ctx.Textures.Pools[4]);
    }

    /// <summary>Loads, fits and queues the texture; its packed reference, or <see cref="TextureTable.Failed"/> (logged) when it cannot be used.</summary>
    private static uint Load(RenderContext ctx, ulong id)
    {
        if (RenderTexture.IsAt(ctx.Assets.PathOf(id)))
            return LoadRendered(ctx, id);

        Texture? texture = ctx.Assets.Load(new Handle<Texture>(id));
        if (texture is null)
            return TextureTable.Failed; // the asset manager logged why

        if (texture.Format is not (TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb) || texture.Levels.Length == 0)
        {
            Debugging.Log.Warn($"{texture.Path} is {texture.Format}; the pools take RGBA8 only, so it draws as failed.");
            return TextureTable.Failed;
        }

        TextureTable table = ctx.Textures;
        int size = table.SizeFor(texture.Width, texture.Height);
        int classIndex = (texture.Format == TextureFormat.Rgba8Srgb ? 0 : 4) + Array.IndexOf(TextureTable.PoolSizes, size);
        TextureTable.PoolClass pool = table.Pools[classIndex];
        if (!table.TryAllocateLayer(ctx.Gpu, pool, out uint layer))
        {
            Debugging.Log.Warn($"Texture pool class {classIndex} is full ({TextureTable.MaxLayers} layers); {texture.Path} draws as failed.");
            return TextureTable.Failed;
        }

        (byte[] pixels, int[] offsets) = FitToPool(texture, pool.Size);
        table.Pending.Add((classIndex, layer, pixels, offsets));
        return ((uint)classIndex << 16) | layer;
    }

    /// <summary>
    /// A render texture: an sRGB layer (Ldr's format, so a camera's frame is blitted in unchanged) sized like an image of its
    /// size would be, cleared to black until a camera draws into it.
    /// </summary>
    private static uint LoadRendered(RenderContext ctx, ulong id)
    {
        RenderTexture? texture = ctx.Assets.Load(new Handle<RenderTexture>(id));
        if (texture is null)
            return TextureTable.Failed; // the asset manager logged why

        if (texture.Width <= 0 || texture.Height <= 0)
        {
            Debugging.Log.Warn($"{texture.Path} is {texture.Width}x{texture.Height}; a render texture needs a size, so it draws as failed.");
            return TextureTable.Failed;
        }

        TextureTable table = ctx.Textures;
        int classIndex = Array.IndexOf(TextureTable.PoolSizes, table.SizeFor(texture.Width, texture.Height));
        TextureTable.PoolClass pool = table.Pools[classIndex];
        if (!table.TryAllocateLayer(ctx.Gpu, pool, out uint layer))
        {
            Debugging.Log.Warn($"Texture pool class {classIndex} is full ({TextureTable.MaxLayers} layers); {texture.Path} draws as failed.");
            return TextureTable.Failed;
        }

        int levels = (int)pool.Levels;
        int[] offsets = new int[levels + 1];
        for (int i = 0, width = pool.Size; i < levels; i++, width = Math.Max(1, width / 2))
            offsets[i + 1] = offsets[i] + (width * width * 4);

        uint packed = ((uint)classIndex << 16) | layer;
        table.Pending.Add((classIndex, layer, new byte[offsets[levels]], offsets));
        table.Rendered[id] = new RenderedTexture(packed, texture.Width, texture.Height);
        return packed;
    }

    /// <summary>
    /// Every mip level of the texture at <paramref name="size"/>, level 0 first, tightly packed RGBA8 in one array;
    /// <c>Offsets[i]</c> is where level i starts and the last offset is where the chain ends. A source that is already
    /// square at the size with its whole chain is used as it is.
    /// </summary>
    private static (byte[] Pixels, int[] Offsets) FitToPool(Texture texture, int size)
    {
        int levels = 1 + (int)Math.Log2(size);
        int[] offsets = new int[levels + 1];

        if (HasChain(texture, size, levels))
        {
            for (int i = 0; i < levels; i++)
                offsets[i] = texture.Levels[i].Offset;
            offsets[levels] = texture.Levels[levels - 1].Offset + texture.Levels[levels - 1].Size;
            return (texture.Pixels, offsets);
        }

        int total = 0;
        for (int i = 0, width = size; i < levels; i++, width /= 2)
        {
            offsets[i] = total;
            total += width * width * 4;
        }
        offsets[levels] = total;
        byte[] pixels = new byte[total];

        TextureLevel level0 = texture.Levels[0];
        ReadOnlySpan<byte> source = texture.Pixels.AsSpan(level0.Offset, level0.Size);
        Span<byte> top = pixels.AsSpan(0, offsets[1]);
        if (level0.Width == size && level0.Height == size)
            source.CopyTo(top);
        else
            Resize(source, level0.Width, level0.Height, top, size, size);

        for (int i = 1, width = size; i < levels; i++, width /= 2)
        {
            Span<byte> above = pixels.AsSpan(offsets[i - 1], offsets[i] - offsets[i - 1]);
            Span<byte> level = pixels.AsSpan(offsets[i], offsets[i + 1] - offsets[i]);
            Downsample(above, width, width, level);
        }

        return (pixels, offsets);
    }

    /// <summary>Whether the loader's levels are the pool's chain for <paramref name="size"/>: square, halving, tightly packed.</summary>
    private static bool HasChain(Texture texture, int size, int levels)
    {
        if (texture.Levels.Length != levels)
            return false;

        int offset = texture.Levels[0].Offset;
        for (int i = 0, width = size; i < levels; i++, width /= 2)
        {
            TextureLevel level = texture.Levels[i];
            if (level.Width != width || level.Height != width || level.Size != width * width * 4 || level.Offset != offset)
                return false;

            offset += level.Size;
        }

        return offset <= texture.Pixels.Length;
    }

    private static void Resize(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        if (sw <= dw && sh <= dh)
        {
            for (int y = 0; y < dh; y++)
            {
                int sy = y * sh / dh;
                for (int x = 0; x < dw; x++)
                {
                    int sx = x * sw / dw;
                    src.Slice(((sy * sw) + sx) * 4, 4).CopyTo(dst.Slice(((y * dw) + x) * 4, 4));
                }
            }
            return;
        }

        for (int y = 0; y < dh; y++)
        {
            int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = ((sy * sw) + sx) * 4;
                        r += src[i];
                        g += src[i + 1];
                        b += src[i + 2];
                        a += src[i + 3];
                        n++;
                    }
                }

                int o = ((y * dw) + x) * 4;
                dst[o] = (byte)(r / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(b / n);
                dst[o + 3] = (byte)(a / n);
            }
        }
    }

    private static void Downsample(ReadOnlySpan<byte> src, int w, int h, Span<byte> dst)
    {
        int dw = Math.Max(1, w / 2), dh = Math.Max(1, h / 2);
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int i00 = (((y * 2) * w) + (x * 2)) * 4;
                int i10 = i00 + 4;
                int i01 = i00 + (w * 4);
                int i11 = i01 + 4;
                int o = ((y * dw) + x) * 4;
                for (int c = 0; c < 4; c++)
                    dst[o + c] = (byte)((src[i00 + c] + src[i10 + c] + src[i01 + c] + src[i11 + c] + 2) / 4);
            }
        }
    }
}
