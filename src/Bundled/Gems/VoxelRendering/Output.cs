using System.Drawing;
using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// Shows a view: its finished image scaled into its rectangle of its target and encoded as its image's format expects
/// (<see cref="TargetEncoding"/>): as it is for a render texture, sRGB and dithered for an SDR window, PQ or scRGB for an
/// HDR one. Over the image, the failure banner: what the failures said, as glyphs (<see cref="FailureHandler.Text"/>),
/// and the codes the frame's shaders raised (<see cref="Raised"/>). A pipeline per target format, made the first time
/// one is drawn into. It also keeps a copy of the window's image as the world loads (<see cref="Keep"/>), shown while
/// the world that replaces it is readied out of sight. Render thread.
/// </summary>
internal sealed class Output : IDisposable
{
    private const string Vertex = "Shaders/Passes/Fullscreen.vert.hlsl";
    private const string Fragment = "Shaders/Rendering/Output/Output.frag.hlsl";

    /// <summary>
    /// What empties the raised codes each frame, a word per code.
    /// </summary>
    private static readonly byte[] _noCodes = new byte[FailureHandler.CodeCount * sizeof(uint)];

    private readonly IGpuDriver _gpu;

    /// <summary>
    /// The failure banner's glyphs as last sent, and which packing of them that was.
    /// </summary>
    private readonly GpuBuffer _banner;
    private readonly Dictionary<GpuFormat, GpuPipeline> _pipelines = [];
    private readonly GpuSampler _linear;
    private readonly GpuSampler _nearest;
    private int _bannerVersion = -1;

    /// <summary>
    /// The output's two stages, written whole once both are compiled, which the render thread reads.
    /// </summary>
    private volatile Compiled? _shaders;

    internal Output(IGpuDriver gpu, IShaderCompiler compiler, CancellationToken stopping)
    {
        _gpu = gpu;
        _banner = gpu.CreateBuffer(GpuBufferUsage.GraphicsRead, FailureHandler.TextWords * sizeof(uint));
        Raised = gpu.CreateBuffer(
            GpuBufferUsage.GraphicsRead | GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite,
            (uint)_noCodes.Length);
        _linear = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
        _nearest = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Nearest, GpuWrap.Clamp));
        CompileAsync(compiler, stopping).FireAndForget();
    }

    /// <summary>
    /// A word per code, 1 once a shader raised it this frame.
    /// </summary>
    internal GpuBuffer Raised { get; }

    /// <summary>
    /// The finished image last kept (<see cref="Keep"/>), the window's as the world loaded, which the window shows in
    /// place of the world while it is readied out of sight; none until one is kept, and once it is let go.
    /// </summary>
    internal GpuTexture Kept { get; private set; }

    public void Dispose()
    {
        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            _gpu.Release(pipeline);
        }

        _gpu.Release(_banner);
        _gpu.Release(Raised);
        _gpu.Release(_linear);
        _gpu.Release(_nearest);
        _gpu.Release(Kept);
    }

    /// <summary>
    /// Draws <paramref name="image"/> into the rectangle of <paramref name="target"/>, read crisply when
    /// <paramref name="isCrisp"/> as it is scaled, with the failure banner over it (<see cref="SendBanner"/>). The rest
    /// of the target is cleared first when <paramref name="clears"/>; only the clear until the pipeline is made.
    /// </summary>
    internal void Draw(ViewTarget target, GpuTexture image, bool isCrisp, bool clears, float time, uint frameIndex)
    {
        GpuPipeline pipeline = GetPipeline(target.Format);
        if (pipeline == GpuPipeline.None || image == GpuTexture.None)
        {
            if (clears) Clear(target.Texture);

            return;
        }

        OutputConstants constants = new(
            image.Id,
            (isCrisp ? _nearest : _linear).Id,
            _banner.Id,
            Raised.Id,
            target.Area.Width,
            target.Area.Height,
            time,
            (uint)target.Encoding,
            frameIndex);
        using GpuDebugGroup group = _gpu.Commands.DebugGroup("Output");
        using GpuRenderPass render = _gpu.Commands
            .RenderPass([new GpuTextureLevel(target.Texture)], clears ? GpuContents.Clear : GpuContents.Keep);
        render.Viewport(target.Area)
            .Scissor(target.Area)
            .Reads([image, _banner, Raised])
            .Pipeline(pipeline)
            .Constants(constants)
            .Draw(3);
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
            _gpu.Upload(_banner, 0, MemoryMarshal.AsBytes(failures.Text));
        }

        _gpu.Upload(Raised, 0, _noCodes);
    }

    /// <summary>
    /// Clears a target to black, as a window nothing is drawn into this frame is.
    /// </summary>
    internal void Clear(GpuTexture target)
        => _gpu.Commands.RenderPass([new GpuTextureLevel(target)], GpuContents.Clear).Dispose();

    /// <summary>
    /// Copies <paramref name="image"/>, the finished image of the view shown in the window, into <see cref="Kept"/>,
    /// made again when its size or format changes.
    /// </summary>
    internal void Keep(GpuTexture image)
    {
        if (image == GpuTexture.None) return;

        Size size = _gpu.GetSize(image);
        GpuFormat format = _gpu.GetFormat(image);
        if (Kept == GpuTexture.None || _gpu.GetSize(Kept) != size || _gpu.GetFormat(Kept) != format)
        {
            Forget();
            Kept = _gpu.CreateTexture(
                new GpuTextureDescription(format, GpuTextureUsage.Sampled, (uint)size.Width, (uint)size.Height));
        }

        _gpu.Commands.CopyTexture(new GpuTextureRegion(image), new GpuTextureRegion(Kept));
    }

    /// <summary>
    /// Lets the kept image go, once the world it stood in for is shown.
    /// </summary>
    internal void Forget()
    {
        _gpu.Release(Kept);
        Kept = GpuTexture.None;
    }

    /// <summary>
    /// The pipeline for <paramref name="format"/>; none until the shaders are compiled, and for good, logged, when one
    /// does not.
    /// </summary>
    private GpuPipeline GetPipeline(GpuFormat format)
    {
        if (_pipelines.TryGetValue(format, out GpuPipeline pipeline) || _shaders is not { } shaders) return pipeline;

        Result<GpuPipeline> made = shaders.Vertex.Ok && shaders.Fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDescription
            {
                Vertex = shaders.Vertex.Value,
                Fragment = shaders.Fragment.Value,
                ColorFormats = [format],
                Cull = GpuCull.None,
            })
            : Result.Failure<GpuPipeline>(shaders.Vertex.Ok ? shaders.Fragment.Error : shaders.Vertex.Error);
        if (!made.Ok) Debugging.Error($"Views are not shown: {Fragment} does not compile: {made.Error}");

        pipeline = made.Ok ? made.Value : GpuPipeline.None;
        _pipelines[format] = pipeline;

        return pipeline;
    }

    private async Task CompileAsync(IShaderCompiler compiler, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await compiler.CompileAsync(Vertex, GpuStage.Vertex, GpuShaderFormat.Spirv, stopping)
            .ConfigureAwait(false);
        Result<GpuShader> fragment = await compiler.CompileAsync(
            Fragment,
            GpuStage.Fragment,
            GpuShaderFormat.Spirv,
            stopping).ConfigureAwait(false);
        _shaders = new Compiled(vertex, fragment);
    }

    /// <summary>
    /// The output's constants, as Output.frag.hlsl's <c>OutputConstants</c>: the image and the sampler it is read
    /// through, the banner's glyphs and the raised codes, the view's size as it is shown, the time, how it is encoded,
    /// and the frame's number, which dithers it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OutputConstants(
        uint Image,
        uint Sampler,
        uint Text,
        uint Failures,
        float Width,
        float Height,
        float Time,
        uint Encoding,
        uint FrameIndex);

    /// <summary>
    /// The two stages as compiled, or why each did not.
    /// </summary>
    private sealed record Compiled(Result<GpuShader> Vertex, Result<GpuShader> Fragment);
}
