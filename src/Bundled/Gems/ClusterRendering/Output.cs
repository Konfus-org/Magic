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
/// its rectangle of its target, encoded to sRGB and dithered for a window's UNORM image, as it is for a render texture.
/// A pipeline per target format, made the first time one is drawn into. Render thread.
/// </summary>
internal sealed class Output : IDisposable
{
    private const string Fragment = "Shaders/Passes/Output.frag.hlsl";
    private const string Fullscreen = "Shaders/Passes/Fullscreen.vert.hlsl";

    private readonly IGpu _gpu;
    private readonly Dictionary<GpuFormat, GpuPipeline> _pipelines = [];
    private readonly GpuSampler _sampler;

    /// <summary>
    /// The output's two stages, once both are compiled, handed to the render thread.
    /// </summary>
    private (Result<GpuShader> Vertex, Result<GpuShader> Fragment)? _shaders;

    public Output(IGpu gpu, IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        _gpu = gpu;
        _sampler = gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddress.Clamp));
        CompileAsync(compiler, assets, threads, stopping).FireAndForget();
    }

    public void Dispose()
    {
        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            _gpu.Release(pipeline);
        }

        _gpu.Release(_sampler);
    }

    /// <summary>
    /// Draws the view's finished image into its rectangle of its target, clearing the rest of the target first when
    /// <paramref name="clears"/>; only the clear until the pipeline is made and the view has an image.
    /// </summary>
    public void Draw(ViewTarget target, in FrameConstants constants, BoundResource image, bool clears)
    {
        GpuPipeline pipeline = PipelineFor(target.Format);
        if (pipeline == GpuPipeline.None || image.Texture == GpuTexture.None)
        {
            if (clears)
            {
                Clear(target.Texture);
            }

            return;
        }

        GpuCommands commands = _gpu.Commands;
        commands.PushDebugGroup("Output");
        commands.Push(GpuStage.Fragment, constants);
        commands.BeginRenderPass([new GpuBinding(Texture: target.Texture)], clears ? GpuLoad.Clear : GpuLoad.Load);
        commands.SetViewport(target.Area);
        commands.SetScissor(target.Area);
        commands.BindPipeline(pipeline);
        commands.BindTextures(GpuStage.Fragment, 0, [new GpuBinding(Texture: image.Texture, Sampler: _sampler)]);
        commands.Draw(3);
        commands.EndRenderPass();
        commands.PopDebugGroup();
    }

    /// <summary>
    /// Clears a target to black, as a window nothing is drawn into this frame is.
    /// </summary>
    public void Clear(GpuTexture target)
    {
        _gpu.Commands.BeginRenderPass([new GpuBinding(Texture: target)], GpuLoad.Clear);
        _gpu.Commands.EndRenderPass();
    }

    private GpuPipeline PipelineFor(GpuFormat format)
    {
        if (_pipelines.TryGetValue(format, out GpuPipeline pipeline) || _shaders is not ({ } vertex, { } fragment))
        {
            return pipeline;
        }

        Result<GpuPipeline> made = vertex.Ok && fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDesc { Vertex = vertex.Value, Fragment = fragment.Value, Colors = [format], Cull = GpuCull.None })
            : Result.Failure<GpuPipeline>(vertex.Ok ? fragment.Error : vertex.Error);
        if (!made.Ok)
        {
            Debugging.Error($"Views are not shown: their output does not compile: {made.Error}");
        }

        _pipelines[format] = made.Ok ? made.Value : GpuPipeline.None;
        return _pipelines[format];
    }

    private async Task CompileAsync(IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await CompileAsync(Fullscreen, GpuStage.Vertex, _gpu.ShaderFormat, compiler, assets, stopping).ConfigureAwait(false);
        Result<GpuShader> fragment = await CompileAsync(Fragment, GpuStage.Fragment, _gpu.ShaderFormat, compiler, assets, stopping).ConfigureAwait(false);
        threads.Post(EngineThread.Render, () => _shaders = (vertex, fragment));
    }

    private static async Task<Result<GpuShader>> CompileAsync(string path, GpuStage stage, GpuShaderFormat format, IShaderCompiler compiler, Assets assets, CancellationToken stopping)
    {
        Result<Shader> shader = await assets.LoadAsync(assets.Find<Shader>(path), stopping).ConfigureAwait(false);
        return shader.Ok ? await compiler.CompileAsync(shader.Value.Text, path, stage, format, stopping).ConfigureAwait(false) : Result.Failure<GpuShader>(shader.Error);
    }
}
