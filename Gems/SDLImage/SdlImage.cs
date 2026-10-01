using Magic.Contexts.Assets;
using Magic.Interfaces;
using SDL3;
using System.Runtime.InteropServices;

namespace SDLImageGem;

/// <summary>
/// Loads <see cref="Texture"/> assets with SDL3_image: any format it decodes (png, jpg, bmp, tga, webp, ...),
/// converted to RGBA8 with a linear mip chain when the sidecar asks for one. Block compression is not
/// implemented; a texture asking for it is loaded uncompressed. SDL3_image has no init call of its own, so
/// this gem only needs SDL to be up.
/// </summary>
internal sealed class SdlImage : IGem, IAssetLoader<Texture>
{
    public void Load(Texture asset, byte[] bytes)
    {
        nint decoded;
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            nint io = SDL.IOFromConstMem(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
            if (io == IntPtr.Zero)
                throw new InvalidOperationException($"SDL_IOFromConstMem failed: {SDL.GetError()}");

            if (asset.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // A vector image is rasterised at the sidecar's size (0: the size the file states), square unless it says otherwise.
                decoded = Image.LoadSizedSVGIO(io, asset.Size, asset.Size);
                SDL.CloseIO(io);
            }
            else
                decoded = Image.LoadIO(io, closeio: true); // closes io even on failure

            if (decoded == IntPtr.Zero)
                throw new InvalidOperationException($"IMG_Load failed: {SDL.GetError()}");
        }
        finally
        {
            pin.Free();
        }

        // ABGR8888 is R, G, B, A in memory on a little-endian machine: what the GPU formats below expect.
        nint surface = SDL.ConvertSurface(decoded, SDL.PixelFormat.ABGR8888);
        SDL.DestroySurface(decoded);

        if (surface == IntPtr.Zero)
            throw new InvalidOperationException($"SDL_ConvertSurface failed: {SDL.GetError()}");

        try
        {
            SDL.Surface level0 = Marshal.PtrToStructure<SDL.Surface>(surface);
            asset.Width = level0.Width;
            asset.Height = level0.Height;

            int levelCount = asset.Mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(level0.Width, level0.Height))) : 1;

            List<TextureLevel> levels = [];
            List<byte[]> pixels = [];
            nint current = surface;
            for (int i = 0; i < levelCount; i++)
            {
                if (i > 0)
                {
                    SDL.Surface previous = Marshal.PtrToStructure<SDL.Surface>(current);
                    int nextWidth = Math.Max(1, previous.Width / 2);
                    int nextHeight = Math.Max(1, previous.Height / 2);
                    nint next = SDL.ScaleSurface(current, nextWidth, nextHeight, SDL.ScaleMode.Linear);
                    if (current != surface)
                        SDL.DestroySurface(current);

                    current = next != IntPtr.Zero ? next : throw new InvalidOperationException($"SDL_ScaleSurface failed: {SDL.GetError()}");
                }

                byte[] data = Copy(current, out int width, out int height);
                levels.Add(new TextureLevel(width, height, levels.Sum(level => level.Size), data.Length));
                pixels.Add(data);
            }

            if (current != surface)
                SDL.DestroySurface(current);

            asset.Levels = [.. levels];
            asset.Pixels = [.. pixels.SelectMany(level => level)];
        }
        finally
        {
            SDL.DestroySurface(surface);
        }

        asset.Format = asset.Usage is TextureUsage.Normal or TextureUsage.Mask ? TextureFormat.Rgba8Unorm : TextureFormat.Rgba8Srgb;
    }

    /// <summary>The surface's pixels as tightly packed RGBA rows (SDL pads rows to its pitch).</summary>
    private static byte[] Copy(nint surface, out int width, out int height)
    {
        SDL.Surface info = Marshal.PtrToStructure<SDL.Surface>(surface);
        width = info.Width;
        height = info.Height;
        int rowBytes = width * 4;
        byte[] data = GC.AllocateUninitializedArray<byte>(rowBytes * height);

        bool locked = SDL.LockSurface(surface);
        try
        {
            info = Marshal.PtrToStructure<SDL.Surface>(surface);
            for (int y = 0; y < height; y++)
                Marshal.Copy(info.Pixels + (y * info.Pitch), data, y * rowBytes, rowBytes);
        }
        finally
        {
            if (locked)
                SDL.UnlockSurface(surface);
        }

        return data;
    }
}
