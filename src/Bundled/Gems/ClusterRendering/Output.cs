using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// Shows a view: its finished image, what the post stage made (<c>Ldr</c>) or else the lit scene as it is, scaled into
/// its rectangle of its target and encoded as its image's format expects (<see cref="TargetEncoding"/>): as it is for
/// a render texture, sRGB and dithered for an SDR window, PQ or scRGB for an HDR one. Or, while the frame is viewed at
/// a stage that makes no image for the view, that nothing draws to it, in the failure's magenta. A pipeline per
/// target format and shader, made the first time one is drawn into. Render thread.
/// </summary>
internal sealed class Output : IDisposable
{
    private const string Fragment = "Shaders/Passes/Output.frag.hlsl";
    private const string Nothing = "Shaders/Passes/Nothing.frag.hlsl";
    private const string Fullscreen = "Shaders/Passes/Fullscreen.vert.hlsl";

    private readonly IGpu _gpu;
    private readonly Dictionary<(GpuFormat Format, bool IsNothing), GpuPipeline> _pipelines = [];
    private readonly GpuSampler _linear;
    private readonly GpuSampler _nearest;

    /// <summary>
    /// The output's vertex stage and its two fragment stages, once all are compiled, handed to the render thread.
    /// </summary>
    private (Result<GpuShader> Vertex, Result<GpuShader> Fragment, Result<GpuShader> Nothing)? _shaders;

    public Output(IGpu gpu, IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        _gpu = gpu;
        _linear = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
        _nearest = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Nearest, GpuWrap.Clamp));
        CompileAsync(compiler, assets, threads, stopping).FireAndForget();
    }

    public void Dispose()
    {
        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            _gpu.Release(pipeline);
        }

        _gpu.Release(_linear);
        _gpu.Release(_nearest);
    }

    /// <summary>
    /// Draws the view's finished image into its rectangle of its target, read with <paramref name="filter"/> as it is
    /// scaled, with the words of what failed this frame over it (<paramref name="failures"/>), clearing the rest of
    /// the target first when <paramref name="clears"/>; only the clear until the pipeline is made and the view has an
    /// image.
    /// </summary>
    public void Draw(ViewTarget target, in FrameConstants constants, BoundResource image, GpuFilter filter, bool clears, Failures failures)
    {
        GpuPipeline pipeline = PipelineFor(target.Format, isNothing: false);
        if (pipeline == GpuPipeline.None || image.Texture == GpuTexture.None)
        {
            if (clears)
            {
                Clear(target.Texture);
            }

            return;
        }

        Record(target, constants, pipeline, clears, [new GpuSampledTexture(image.Texture, filter == GpuFilter.Nearest ? _nearest : _linear)], [failures.Text, failures.Raised]);
    }

    /// <summary>
    /// Draws into the view's rectangle of its target that nothing draws to the stage the frame is viewed at, in the
    /// failure's magenta, clearing the rest of the target first when <paramref name="clears"/>; only the clear until
    /// the pipeline is made.
    /// </summary>
    public void DrawNothing(ViewTarget target, in FrameConstants constants, bool clears)
    {
        GpuPipeline pipeline = PipelineFor(target.Format, isNothing: true);
        if (pipeline == GpuPipeline.None)
        {
            if (clears)
            {
                Clear(target.Texture);
            }

            return;
        }

        Record(target, constants, pipeline, clears, [], []);
    }

    /// <summary>
    /// Clears a target to black, as a window nothing is drawn into this frame is.
    /// </summary>
    public void Clear(GpuTexture target)
    {
        _gpu.Commands.RenderPass([new GpuTextureLevel(target)], GpuContents.Clear).Dispose();
    }

    /// <summary>
    /// Records the fullscreen triangle with <paramref name="pipeline"/> into the view's rectangle of its target, its
    /// fragment stage reading <paramref name="textures"/>, then <paramref name="buffers"/> at the registers after
    /// them.
    /// </summary>
    private void Record(ViewTarget target, in FrameConstants constants, GpuPipeline pipeline, bool clears, ReadOnlySpan<GpuSampledTexture> textures, ReadOnlySpan<GpuBuffer> buffers)
    {
        using GpuDebugGroup group = _gpu.Commands.DebugGroup("Output");
        using GpuRenderPass render = _gpu.Commands.RenderPass([new GpuTextureLevel(target.Texture)], clears ? GpuContents.Clear : GpuContents.Keep);
        render.Viewport(target.Area).Scissor(target.Area).Pipeline(pipeline).Constants(GpuStage.Fragment, constants);
        if (textures.Length > 0)
        {
            render.Textures(GpuStage.Fragment, textures);
        }

        if (buffers.Length > 0)
        {
            render.Buffers(GpuStage.Fragment, buffers);
        }

        render.Draw(3);
    }

    /// <summary>
    /// The pipeline for <paramref name="format"/> with the finished image's fragment stage, or the nothing-drawn one
    /// when <paramref name="isNothing"/>; none until the shaders are compiled, and for good, logged, when one does not.
    /// </summary>
    private GpuPipeline PipelineFor(GpuFormat format, bool isNothing)
    {
        if (_pipelines.TryGetValue((format, isNothing), out GpuPipeline pipeline) || _shaders is not ({ } vertex, { } finished, { } nothing))
        {
            return pipeline;
        }

        Result<GpuShader> fragment = isNothing ? nothing : finished;
        Result<GpuPipeline> made = vertex.Ok && fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDescription { Vertex = vertex.Value, Fragment = fragment.Value, ColorFormats = [format], Cull = GpuCull.None })
            : Result.Failure<GpuPipeline>(vertex.Ok ? fragment.Error : vertex.Error);
        if (!made.Ok)
        {
            Debugging.Error($"Views are not shown: {(isNothing ? Nothing : Fragment)} does not compile: {made.Error}");
        }

        _pipelines[(format, isNothing)] = made.Ok ? made.Value : GpuPipeline.None;
        return _pipelines[(format, isNothing)];
    }

    private async Task CompileAsync(IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await CompileAsync(Fullscreen, GpuStage.Vertex, _gpu.ShaderFormat, compiler, assets, stopping).ConfigureAwait(false);
        Result<GpuShader> fragment = await CompileAsync(Fragment, GpuStage.Fragment, _gpu.ShaderFormat, compiler, assets, stopping).ConfigureAwait(false);
        Result<GpuShader> nothing = await CompileAsync(Nothing, GpuStage.Fragment, _gpu.ShaderFormat, compiler, assets, stopping).ConfigureAwait(false);
        threads.Post(EngineThread.Render, () => _shaders = (vertex, fragment, nothing));
    }

    private static async Task<Result<GpuShader>> CompileAsync(string path, GpuStage stage, GpuShaderFormat format, IShaderCompiler compiler, Assets assets, CancellationToken stopping)
    {
        Result<Shader> shader = await assets.LoadAsync(assets.Find<Shader>(path), stopping).ConfigureAwait(false);
        return shader.Ok ? await compiler.CompileAsync(shader.Value.Text, path, stage, format, stopping).ConfigureAwait(false) : Result.Failure<GpuShader>(shader.Error);
    }
}
