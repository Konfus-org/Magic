using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Shows a view: its finished image, what the post stage made (<c>Ldr</c>) or else the lit scene as it is, scaled into
/// its rectangle of its target and encoded as its image's format expects (<see cref="TargetEncoding"/>): as it is for
/// a render texture, sRGB and dithered for an SDR window, PQ or scRGB for an HDR one. Or, while the frame is viewed at
/// a stage that makes no image for the view, that nothing draws to it, in the failure's magenta. Over the image, the
/// failure banner: what the failures said, as glyphs (<see cref="FailureHandler.Text"/>), and the codes the frame's
/// shaders raised (<see cref="Raised"/>). A pipeline per target format and shader, made the first time one is drawn
/// into. Render thread.
/// </summary>
internal sealed class Output : IDisposable
{
    private const string Fragment = "Shaders/Passes/Output.frag.hlsl";
    private const string Nothing = "Shaders/Passes/Nothing.frag.hlsl";
    private const string Fullscreen = "Shaders/Passes/Fullscreen.vert.hlsl";

    /// <summary>
    /// What empties the raised codes each frame, a word per code.
    /// </summary>
    private static readonly byte[] _noCodes = new byte[FailureHandler.CodeCount * sizeof(uint)];

    private readonly IGpu _gpu;
    private readonly GpuResources _resources;

    /// <summary>
    /// The failure banner's glyphs as last sent, and which packing of them that was.
    /// </summary>
    private readonly GpuBuffer _banner;
    private int _bannerVersion = -1;
    private readonly Dictionary<(GpuFormat Format, bool IsNothing), GpuPipeline> _pipelines = [];
    private readonly GpuSampler _linear;
    private readonly GpuSampler _nearest;

    /// <summary>
    /// The output's vertex stage and its two fragment stages, written whole once all are compiled, which the render
    /// thread reads.
    /// </summary>
    private volatile Compiled? _shaders;

    internal Output(IGpu gpu, GpuResources resources, IShaderCompiler compiler, CancellationToken stopping)
    {
        _gpu = gpu;
        _resources = resources;
        _banner = resources.MakeBuffer(GpuBufferUsage.GraphicsRead, FailureHandler.TextWords * sizeof(uint));
        Raised = resources.MakeBuffer(GpuContract.Failures, (uint)_noCodes.Length);
        _linear = resources.GetSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
        _nearest = resources.GetSampler(new GpuSamplerDescription(GpuFilter.Nearest, GpuWrap.Clamp));
        CompileAsync(compiler, stopping).FireAndForget();
    }

    /// <summary>
    /// A word per code, 1 once a shader raised it this frame: the frame's <c>Failures</c>.
    /// </summary>
    internal GpuBuffer Raised { get; }

    public void Dispose()
    {
        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            _gpu.Release(pipeline);
        }
    }

    /// <summary>
    /// Draws the view's finished image into its rectangle of its target, read with <paramref name="filter"/> as it is
    /// scaled, with the failure banner over it (<see cref="SendBanner"/>); or, with no image
    /// (null), the failure's magenta, saying that nothing draws to the stage the frame is viewed at. The rest of the
    /// target is cleared first when <paramref name="clears"/>; only the clear until the pipeline is made, or while the
    /// image is not made yet.
    /// </summary>
    internal void Draw(
        ViewTarget target,
        in FrameConstants constants,
        BoundResource? image,
        GpuFilter filter,
        bool clears)
    {
        GpuPipeline pipeline = GetPipeline(target.Format, isNothing: image is null);
        if (pipeline == GpuPipeline.None || image?.Texture == GpuTexture.None)
        {
            if (clears) Clear(target.Texture);

            return;
        }

        if (image is { } drawn)
        {
            Record(
                target,
                constants,
                pipeline,
                clears,
                [new GpuSampledTexture(drawn.Texture, filter == GpuFilter.Nearest ? _nearest : _linear)],
                [_banner, Raised]);
        }
        else
        {
            Record(target, constants, pipeline, clears, [], []);
        }
    }

    /// <summary>
    /// Sends the banner's glyphs when <paramref name="failures"/> packed them again, and empties the raised codes,
    /// before the frame's passes run, so a code shows the frame it is raised.
    /// </summary>
    internal void SendBanner(FailureHandler failures)
    {
        if (failures.TextVersion != _bannerVersion)
        {
            _bannerVersion = failures.TextVersion;
            _resources.Upload(_banner, 0, MemoryMarshal.AsBytes(failures.Text));
        }

        _resources.Upload(Raised, 0, _noCodes);
    }

    /// <summary>
    /// Clears a target to black, as a window nothing is drawn into this frame is.
    /// </summary>
    internal void Clear(GpuTexture target)
    {
        _gpu.Commands.RenderPass([new GpuTextureLevel(target)], GpuContents.Clear).Dispose();
    }

    /// <summary>
    /// Records the fullscreen triangle with <paramref name="pipeline"/> into the view's rectangle of its target, its
    /// fragment stage reading <paramref name="textures"/>, then <paramref name="buffers"/> at the registers after
    /// them.
    /// </summary>
    private void Record(
        ViewTarget target,
        in FrameConstants constants,
        GpuPipeline pipeline,
        bool clears,
        ReadOnlySpan<GpuSampledTexture> textures,
        ReadOnlySpan<GpuBuffer> buffers)
    {
        using GpuDebugGroup group = _gpu.Commands.DebugGroup("Output");
        using GpuRenderPass render = _gpu.Commands
            .RenderPass([new GpuTextureLevel(target.Texture)], clears ? GpuContents.Clear : GpuContents.Keep);
        render.Viewport(target.Area).Scissor(target.Area).Pipeline(pipeline).Constants(GpuStage.Fragment, constants);
        if (textures.Length > 0) render.Textures(GpuStage.Fragment, textures);

        if (buffers.Length > 0) render.Buffers(GpuStage.Fragment, buffers);

        render.Draw(3);
    }

    /// <summary>
    /// The pipeline for <paramref name="format"/> with the finished image's fragment stage, or the nothing-drawn one
    /// when <paramref name="isNothing"/>; none until the shaders are compiled, and for good, logged, when one does not.
    /// </summary>
    private GpuPipeline GetPipeline(GpuFormat format, bool isNothing)
    {
        if (_pipelines.TryGetValue((format, isNothing), out GpuPipeline pipeline) || _shaders is not { } shaders)
        {
            return pipeline;
        }

        Result<GpuShader> vertex = shaders.Vertex;
        Result<GpuShader> fragment = isNothing ? shaders.Nothing : shaders.Fragment;
        Result<GpuPipeline> made = vertex.Ok && fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDescription
            {
                Vertex = vertex.Value,
                Fragment = fragment.Value,
                ColorFormats = [format],
                Cull = GpuCull.None
            })
            : Result.Failure<GpuPipeline>(vertex.Ok ? fragment.Error : vertex.Error);
        if (!made.Ok)
        {
            Debugging.Error($"Views are not shown: {(isNothing ? Nothing : Fragment)} does not compile: {made.Error}");
        }

        pipeline = made.Ok ? made.Value : GpuPipeline.None;
        _pipelines[(format, isNothing)] = pipeline;
        return pipeline;
    }

    private async Task CompileAsync(IShaderCompiler compiler, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await compiler.CompileAsync(Fullscreen, GpuStage.Vertex, _gpu.ShaderFormat, stopping)
            .ConfigureAwait(false);
        Result<GpuShader> fragment = await compiler.CompileAsync(
            Fragment,
            GpuStage.Fragment,
            _gpu.ShaderFormat,
            stopping).ConfigureAwait(false);
        Result<GpuShader> nothing = await compiler.CompileAsync(Nothing, GpuStage.Fragment, _gpu.ShaderFormat, stopping)
            .ConfigureAwait(false);
        _shaders = new Compiled(vertex, fragment, nothing);
    }

    /// <summary>
    /// The three stages as compiled, or why each did not.
    /// </summary>
    private sealed record Compiled(Result<GpuShader> Vertex, Result<GpuShader> Fragment, Result<GpuShader> Nothing);
}
