using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Interfaces;

namespace DeferredRendererGem;

/// <summary>
/// The ambient occlusion compute pipelines: the horizon search and the blur that smooths and upsamples it.
/// </summary>
internal sealed record AoPipelines(GpuPipeline Gtao, GpuPipeline Blur);

/// <summary>
/// The screen-space ambient occlusion, as commands: after a target's views are drawn, two compute passes per view turn
/// its depth and normals into the occlusion texture the lighting reads (<c>Ao</c>: the bent normal and the visibility
/// per pixel). The search runs at the view's resolution or half of it (<c>AoRaw</c>, sized by the setting), the blur
/// always at full. Nothing temporal: a fixed dither that the blur removes, so the result holds still.
/// </summary>
internal static class AmbientOcclusion
{
    public const string GtaoShader = "Lighting/Gtao.comp.hlsl";
    public const string BlurShader = "Lighting/AoBlur.comp.hlsl";

    public const GpuFormat RawFormat = GpuFormat.Rgba16Float;
    public const GpuFormat Format = GpuFormat.Rgba8Unorm;

    private const int Group = 8; // Gtao.comp.hlsl and AoBlur.comp.hlsl: pixels a side per group

    public static AoPipelines Create(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        return new AoPipelines(
            gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, GtaoShader, GpuStage.Compute)),
            gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, BlurShader, GpuStage.Compute)));
    }

    /// <summary>
    /// The occlusion targets at the scale the settings ask for; before the targets are sized for the frame.
    /// </summary>
    public static void Prepare(IRendering gpu, FrameTargets targets, AoSettings settings)
    {
        const GpuTextureUsage usage = GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite;
        targets.Define(gpu, FrameTargets.AoRawName, RawFormat, usage, Scale(settings));
    }

    public static float Scale(AoSettings settings)
    {
        return settings.HalfResolution ? 0.5f : 1f;
    }

    /// <summary>
    /// The occlusion of one view: the search into <c>AoRaw</c>, the blur into <c>Ao</c>. Outside any pass, after the
    /// view's depth and normals are drawn, before its lighting. Nothing when the setting is off (the lighting then
    /// ignores the texture). Returns the dispatches recorded.
    /// </summary>
    public static int Record(RenderCommands commands, AoPipelines pipelines, FrameTargets targets, in ViewPlan view, GpuSampler nearest, AoSettings settings)
    {
        if (!settings.Enabled)
            return 0;

        float scale = Scale(settings);
        uint rawWidth = (uint)MathF.Ceiling(view.Rect.Width * scale), rawHeight = (uint)MathF.Ceiling(view.Rect.Height * scale);
        commands.Push(GpuStage.Compute, view.Shade);

        commands.BeginComputePass([new GpuBinding(Texture: targets.AoRaw.Texture)]);
        commands.BindPipeline(pipelines.Gtao);
        commands.BindTextures(GpuStage.Compute, 0,
        [
            new GpuBinding(Texture: targets.Depth.Texture, Sampler: nearest),
            new GpuBinding(Texture: targets.GBuffer.Normal.Texture, Sampler: nearest),
        ]);
        commands.Dispatch(Groups(rawWidth), Groups(rawHeight));
        commands.EndComputePass();

        commands.BeginComputePass([new GpuBinding(Texture: targets.Ao.Texture)]);
        commands.BindPipeline(pipelines.Blur);
        commands.BindTextures(GpuStage.Compute, 0,
        [
            new GpuBinding(Texture: targets.AoRaw.Texture, Sampler: nearest),
            new GpuBinding(Texture: targets.Depth.Texture, Sampler: nearest),
        ]);
        commands.Dispatch(Groups((uint)view.Rect.Width), Groups((uint)view.Rect.Height));
        commands.EndComputePass();

        return 2;
    }

    private static uint Groups(uint pixels)
    {
        return (Math.Max(1, pixels) + Group - 1) / Group;
    }
}
