using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The world's post effects on a view's image (<see cref="IRendering.ApplyPostEffects"/>), each in order, a run of pixel
/// effects as one pass of Shaders/Renderer/Post.slang's <c>ApplyPixelEffects</c> specialized with the run,
/// <c>Then&lt;A, Then&lt;B, C&gt;&gt;</c> for three, its values packed after the header and the images as the run lays
/// them out. A run goes on in another pass past <see cref="MaxRun"/> effects or where its values would not fit the
/// pass's constants. Render thread.
/// </summary>
internal sealed class PostPasses(IGpuDriver gpu, Passes passes, FramePool pool, Assets assets)
{
    /// <summary>
    /// The most effects a pass runs.
    /// </summary>
    private const int MaxRun = 4;

    /// <summary>
    /// The most bytes a run's values take: a pass's constants less its header and its images.
    /// </summary>
    private const int MaxValueBytes = Passes.MaxConstantBytes - 16;

    private readonly Handle<Shader> _post = assets.Find<Shader>("Shaders/Renderer/Post.slang");

    /// <summary>
    /// Each run's entry point, made the first time it runs.
    /// </summary>
    private readonly Dictionary<RunKey, ShaderEntry> _entries = [];

    /// <summary>
    /// The pixel effects in a row of the world's, gathered until another kind of effect ends the run.
    /// </summary>
    private readonly List<IPixelEffect> _run = [];

    internal GpuTexture ApplyPostEffects(
        IRendering rendering,
        RenderView view,
        GpuTexture image,
        RenderEffects<IPostEffect> effects)
    {
        _run.Clear();
        foreach (IPostEffect effect in effects)
        {
            if (effect is IPixelEffect pixel)
            {
                _run.Add(pixel);

                continue;
            }

            image = ApplyPixelEffects(view, image, CollectionsMarshal.AsSpan(_run));
            _run.Clear();
            image = effect.Record(rendering, view, image);
        }

        return ApplyPixelEffects(view, image, CollectionsMarshal.AsSpan(_run));
    }

    internal GpuTexture ApplyPixelEffects(RenderView view, GpuTexture image, ReadOnlySpan<IPixelEffect> effects)
    {
        Span<byte> values = stackalloc byte[MaxValueBytes];
        Span<byte> written = stackalloc byte[MaxValueBytes];
        int start = 0;
        int used = 0;
        for (int i = 0; i < effects.Length; i++)
        {
            int size = effects[i].WriteValues(written);
            if (used + size > MaxValueBytes || i - start == MaxRun)
            {
                image = Apply(view, image, effects[start..i], values[..used]);
                start = i;
                used = 0;
            }

            written[..size].CopyTo(values[used..]);
            used += size;
        }

        return start < effects.Length ? Apply(view, image, effects[start..], values[..used]) : image;
    }

    /// <summary>
    /// One pass of a run of effects over <paramref name="image"/> into a texture like it.
    /// </summary>
    private GpuTexture Apply(
        RenderView view,
        GpuTexture image,
        ReadOnlySpan<IPixelEffect> run,
        ReadOnlySpan<byte> values)
    {
        Size size = gpu.GetSize(image);
        GpuTexture output = pool.Texture(new GpuTextureDescription(
            gpu.GetFormat(image),
            GpuTextureUsage.ComputeWrite | GpuTextureUsage.Sampled,
            (uint)size.Width,
            (uint)size.Height));
        Span<byte> constants = stackalloc byte[MaxValueBytes + Unsafe.SizeOf<PostImages>()];
        PostImages images = new(image, output);
        MemoryMarshal.Write(constants, in images);
        values.CopyTo(constants[Unsafe.SizeOf<PostImages>()..]);
        passes.Dispatch(
            passes.GetHeader(view),
            GetEntry(run),
            constants[..(Unsafe.SizeOf<PostImages>() + values.Length)],
            GpuGroups.For(size, 8),
            [image],
            [output]);

        return output;
    }

    /// <summary>
    /// The pass specialized with a run's struct, each effect's module loaded beside it.
    /// </summary>
    private ShaderEntry GetEntry(ReadOnlySpan<IPixelEffect> run)
    {
        RunKey key = new(
            run[0].Shader,
            run.Length > 1 ? run[1].Shader : Handle<Shader>.None,
            run.Length > 2 ? run[2].Shader : Handle<Shader>.None,
            run.Length > 3 ? run[3].Shader : Handle<Shader>.None);
        if (_entries.TryGetValue(key, out ShaderEntry entry)) return entry;

        Handle<Shader>[] modules = new Handle<Shader>[run.Length];
        string type = string.Empty;
        for (int i = run.Length - 1; i >= 0; i--)
        {
            modules[i] = run[i].Shader;
            string name = ShaderType.Of(run[i].Shader, assets.GetPath(run[i].Shader) ?? string.Empty).Name;
            type = type.Length == 0 ? name : $"Then<{name}, {type}>";
        }

        entry = new ShaderEntry(_post, "ApplyPixelEffects")
        {
            Types = [new ShaderType(Handle<Shader>.None, type)],
            Modules = modules,
        };
        _entries[key] = entry;

        return entry;
    }

    /// <summary>
    /// A run's effects' shaders, in order, none past its last.
    /// </summary>
    private readonly record struct RunKey(
        Handle<Shader> First,
        Handle<Shader> Second,
        Handle<Shader> Third,
        Handle<Shader> Fourth);
}
