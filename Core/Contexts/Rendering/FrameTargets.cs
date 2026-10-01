using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The textures one <see cref="RenderTarget"/> is drawn through, sized to it (a window's pixels, a render texture's
/// size) and made again when that changes. The frame renders into <c>Hdr</c> with <c>Depth</c>, the tonemap pass writes
/// <c>Ldr</c> (what a screenshot reads back), and presenting blits it to the window's swapchain or into the render
/// texture's pool layer. A data pass may add targets of its own by name.
/// </summary>
internal sealed class FrameTargets
{
    public const GpuFormat LdrFormat = GpuFormat.Rgba8Srgb;

    public const GpuFormat HdrFormat = GpuFormat.Rgba16Float;

    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);

    public FrameTargets(RenderTarget target, GpuFormat depthFormat)
    {
        RenderTarget = target;

        Hdr = _targets["Hdr"] = new Target { Format = HdrFormat, Usage = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler };
        Ldr = _targets["Ldr"] = new Target { Format = LdrFormat, Usage = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler };
        Depth = _targets["Depth"] = new Target { Format = depthFormat, Usage = GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler, IsDepth = true };
    }

    public RenderTarget RenderTarget { get; }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public Target Hdr { get; }

    public Target Ldr { get; }

    public Target Depth { get; }

    /// <summary>The texture format a pass output is: the engine's own for Hdr and Ldr, else what the pass asks for.</summary>
    public static GpuFormat Format(PassOutput output)
    {
        if (string.Equals(output.Name, "Hdr", StringComparison.OrdinalIgnoreCase))
            return HdrFormat;
        if (string.Equals(output.Name, "Ldr", StringComparison.OrdinalIgnoreCase))
            return LdrFormat;

        return output.Format switch
        {
            TargetFormat.Rgba8Unorm => GpuFormat.Rgba8Unorm,
            TargetFormat.Rgba16Float => GpuFormat.Rgba16Float,
            TargetFormat.R16Float => GpuFormat.R16Float,
            TargetFormat.R32Float => GpuFormat.R32Float,
            TargetFormat.Rg16Float => GpuFormat.Rg16Float,
            TargetFormat.B10G11R11Float => GpuFormat.R11G11B10Float,
            _ => LdrFormat,
        };
    }

    /// <summary>After a ping-pong pass: what was written becomes the target.</summary>
    public static void Swap(Target target)
    {
        (target.Texture, target.Twin) = (target.Twin, target.Texture);
    }

    public Target? Get(string name)
    {
        return _targets.GetValueOrDefault(name);
    }

    /// <summary>Declares a target; an existing one keeps its texture unless the format or scale changed (then <see cref="Ensure"/> makes a new one).</summary>
    public Target Define(IRendering gpu, string name, GpuFormat format, GpuTextureUsage usage, float scale)
    {
        if (_targets.TryGetValue(name, out Target? existing))
        {
            if (existing.Format != format || existing.Scale != scale)
            {
                ReleaseTextures(gpu, existing);
                existing.Format = format;
                existing.Scale = scale;
                existing.Usage = usage;
            }

            return existing;
        }

        Target target = new() { Format = format, Usage = usage, Scale = scale };
        _targets[name] = target;
        return target;
    }

    /// <summary>Makes every target match the render target's size, creating missing ones.</summary>
    public void Ensure(IRendering gpu, uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;

        Width = width;
        Height = height;

        foreach (Target target in _targets.Values)
        {
            uint w = Math.Max(1, (uint)MathF.Round(width * target.Scale));
            uint h = Math.Max(1, (uint)MathF.Round(height * target.Scale));
            if (target.Texture.IsValid && target.Width == w && target.Height == h)
                continue;

            ReleaseTextures(gpu, target);
            target.Width = w;
            target.Height = h;
            target.Texture = gpu.CreateTexture(new TextureDesc(target.Format, target.Usage, w, h));
        }
    }

    /// <summary>The texture a pass writes when it also reads <paramref name="target"/>; created on first use.</summary>
    public GpuTexture TwinOf(IRendering gpu, Target target)
    {
        if (!target.Twin.IsValid)
            target.Twin = gpu.CreateTexture(new TextureDesc(target.Format, target.Usage, target.Width, target.Height));

        return target.Twin;
    }

    public void Release(IRendering gpu)
    {
        foreach (Target target in _targets.Values)
            ReleaseTextures(gpu, target);

        Width = Height = 0;
    }

    private static void ReleaseTextures(IRendering gpu, Target target)
    {
        gpu.Release(target.Texture);
        gpu.Release(target.Twin);

        target.Texture = default;
        target.Twin = default;
        target.Width = target.Height = 0;
    }

    /// <summary>A texture a pass can read or write, by name.</summary>
    public sealed class Target
    {
        public GpuFormat Format { get; set; }

        public GpuTextureUsage Usage { get; set; }

        public float Scale { get; set; } = 1f;

        public GpuTexture Texture { get; set; }

        /// <summary>For a pass that reads and writes the same target: it writes here, then the two swap.</summary>
        public GpuTexture Twin { get; set; }

        public uint Width { get; set; }

        public uint Height { get; set; }

        public bool IsDepth { get; set; }
    }
}
