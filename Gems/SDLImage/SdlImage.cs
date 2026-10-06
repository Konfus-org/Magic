using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Utils;
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
    public Result Load(Texture asset, byte[] bytes)
    {
        nint decoded;
        GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            nint io = SDL.IOFromConstMem(pin.AddrOfPinnedObject(), (nuint)bytes.Length);
            if (io == IntPtr.Zero)
                return Result.Failure($"SDL_IOFromConstMem failed: {SDL.GetError()}");

            if (asset.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // A vector image is rasterised at the sidecar's size (0: the size the file states), square unless it says otherwise.
                decoded = Image.LoadSizedSVGIO(io, asset.Size, asset.Size);
                SDL.CloseIO(io);
            }
            else
                decoded = Image.LoadIO(io, closeio: true); // closes io even on failure

            if (decoded == IntPtr.Zero)
                return Result.Failure($"IMG_Load failed: {SDL.GetError()}");
        }
        finally
        {
            pin.Free();
        }

        // ABGR8888 is R, G, B, A in memory on a little-endian machine: what the GPU formats below expect.
        nint surface = SDL.ConvertSurface(decoded, SDL.PixelFormat.ABGR8888);
        SDL.DestroySurface(decoded);

        if (surface == IntPtr.Zero)
            return Result.Failure($"SDL_ConvertSurface failed: {SDL.GetError()}");

        try
        {
            SDL.Surface level0 = Marshal.PtrToStructure<SDL.Surface>(surface);
            asset.Width = level0.Width;
            asset.Height = level0.Height;

            int levelCount = asset.Mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(level0.Width, level0.Height))) : 1;

            // Each level halves the one before, down to 1: sized up front, so the chain is copied once into one array.
            TextureLevel[] levels = new TextureLevel[levelCount];
            int size = 0;
            for (int i = 0; i < levelCount; i++)
            {
                int width = Math.Max(1, level0.Width >> i), height = Math.Max(1, level0.Height >> i);
                levels[i] = new TextureLevel(width, height, size, width * height * 4);
                size += levels[i].Size;
            }

            byte[] pixels = GC.AllocateUninitializedArray<byte>(size);
            nint current = surface;
            for (int i = 0; i < levelCount; i++)
            {
                if (i > 0)
                {
                    nint next = SDL.ScaleSurface(current, levels[i].Width, levels[i].Height, SDL.ScaleMode.Linear);
                    if (current != surface)
                        SDL.DestroySurface(current);

                    if (next == IntPtr.Zero)
                        return Result.Failure($"SDL_ScaleSurface failed: {SDL.GetError()}");

                    current = next;
                }

                Copy(current, pixels, levels[i]);
            }

            if (current != surface)
                SDL.DestroySurface(current);

            asset.Levels = levels;
            asset.Pixels = pixels;
        }
        finally
        {
            SDL.DestroySurface(surface);
        }

        asset.Format = asset.Usage is TextureUsage.Normal or TextureUsage.Mask ? TextureFormat.Rgba8Unorm : TextureFormat.Rgba8Srgb;

        return Result.Success();
    }

    /// <summary>
    /// The surface's pixels into <paramref name="level"/>'s place in <paramref name="pixels"/>, as tightly packed RGBA
    /// rows (SDL pads rows to its pitch).
    /// </summary>
    private static void Copy(nint surface, byte[] pixels, TextureLevel level)
    {
        int rowBytes = level.Width * 4;
        bool locked = SDL.LockSurface(surface);
        try
        {
            SDL.Surface info = Marshal.PtrToStructure<SDL.Surface>(surface);
            for (int y = 0; y < level.Height; y++)
                Marshal.Copy(info.Pixels + (y * info.Pitch), pixels, level.Offset + (y * rowBytes), rowBytes);
        }
        finally
        {
            if (locked)
                SDL.UnlockSurface(surface);
        }
    }
}
