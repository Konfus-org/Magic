using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Utils;

namespace WorldRenderer;

/// <summary>
/// The picture after the lighting, to the output: the panes composed over it, each frame resolved into the output's
/// size over the last, the exposure metered, the bloom, and the final pass with the camera's effects and the
/// tonemap. The final pass is specialised to the camera's effects, compiled again on a worker when they change and
/// swapped in once built.
/// </summary>
internal sealed class Post(IGraphicsApi gpu) : IDisposable
{
    public const int BloomLevels = 6;

    private const int HistogramBins = 256;
    private const float ExposureMin = 1e-5f;
    private const float ExposureMax = 1e4f;

    private readonly GpuTexture[] _history = new GpuTexture[2];
    private GpuTexture _bloom;
    private GpuBuffer _histogram;
    private GpuBuffer _exposureState;
    private GpuSampler _sampler;
    private GpuPipeline _final;
    private ImmutableArray<Handle<Shader>> _effects = [];
    private ImmutableArray<Handle<Shader>> _compilingEffects = [];
    private Task? _compiling;
    private CompiledBox? _compiled;
    private int _current;
    private int _outputWidth;
    private int _outputHeight;
    private bool _isTableReady;

    public GpuBuffer Constants { get; private set; }

    public void Dispose()
    {
        Destroy(null);
        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Constants, _histogram, _exposureState])
        {
            if (!buffer.IsNone)
                gpu.Destroy(buffer);
        }

        if (!_sampler.IsNone)
            gpu.Destroy(_sampler);

        if (!_final.IsNone)
            gpu.Destroy(_final);
    }

    public Result Create()
    {
        (string Name, long Bytes)[] descriptions =
        [
            ("Post constants", PostConstants.Bytes),
            ("Luminance histogram", (long)HistogramBins * 4),
            ("Exposure state", 16),
        ];
        GpuBuffer[] made = new GpuBuffer[descriptions.Length];
        for (int i = 0; i < descriptions.Length; i++)
        {
            Result<GpuBuffer> buffer = gpu.CreateBuffer(
                new BufferDescription
                {
                    Bytes = descriptions[i].Bytes,
                    Usage = BufferUsages.Storage,
                    Name = descriptions[i].Name,
                }
            );
            if (!buffer.Ok)
                return Result.Failure($"the {descriptions[i].Name} buffer was not made: {buffer.Error}");

            made[i] = buffer.Value;
        }

        (Constants, _histogram, _exposureState) = (made[0], made[1], made[2]);
        Result<GpuSampler> sampler = gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
        );
        if (!sampler.Ok)
            return Result.Failure($"the post sampler was not made: {sampler.Error}");

        _sampler = sampler.Value;

        return Result.Success();
    }

    /// <summary>
    /// Makes the resolved pictures and the bloom for an output size, the old ones destroyed and forgotten by the
    /// graph.
    /// </summary>
    public Result Resize(int width, int height, RenderGraph graph)
    {
        Destroy(graph);
        _outputWidth = width;
        _outputHeight = height;
        for (int i = 0; i < 2; i++)
        {
            Result<GpuTexture> history = gpu.CreateTexture(
                new TextureDescription
                {
                    Width = width,
                    Height = height,
                    Format = GpuFormat.R16G16B16A16Sfloat,
                    Usage = TextureUsages.Storage | TextureUsages.Sampled,
                    Name = $"Resolved picture {i}",
                }
            );
            if (!history.Ok)
                return Result.Failure($"a resolved picture was not made: {history.Error}");

            _history[i] = history.Value;
        }

        Result<GpuTexture> bloom = gpu.CreateTexture(
            new TextureDescription
            {
                Width = Math.Max(width / 2, 1),
                Height = Math.Max(height / 2, 1),
                Format = GpuFormat.R16G16B16A16Sfloat,
                Usage = TextureUsages.Storage | TextureUsages.Sampled,
                Levels = BloomLevels,
                Name = "Bloom",
            }
        );
        if (!bloom.Ok)
            return Result.Failure($"the bloom was not made: {bloom.Error}");

        _bloom = bloom.Value;
        _isTableReady = false;

        return Result.Success();
    }

    /// <summary>
    /// The camera's settings for the frame: starts compiling the final pass for its effects when they changed, and
    /// swaps the pass in once built. Main thread.
    /// </summary>
    public void Update(RendererPipelines pipelines, PostProcessing settings, CancellationToken cancel)
    {
        if (_compiling is { IsCompleted: true })
        {
            _compiling = null;
            CompiledBox? compiled = Interlocked.Exchange(ref _compiled, null);
            Result<GpuPipeline> made = compiled is { Result.Ok: true }
                ? pipelines.CreateCompute(compiled.Result.Value, "Post final")
                : Result.Failure(compiled?.Result.Error ?? "the compile faulted.");
            if (made.Ok)
            {
                if (!_final.IsNone)
                    gpu.Destroy(_final);

                _final = made.Value;
            }
            else
            {
                Log.Error($"The camera's effects are not run: {made.Error}");
            }

            _effects = _compilingEffects;
        }

        ImmutableArray<Handle<Shader>> wanted =
        [
            settings.Effect0,
            settings.Effect1,
            settings.Effect2,
            settings.Effect3,
        ];
        if (_compiling is not null || wanted.AsSpan().SequenceEqual(_effects.AsSpan()))
            return;

        _compilingEffects = wanted;
        _compiling = CompileAsync(pipelines, wanted, cancel);
    }

    /// <summary>
    /// The frame's constants uploaded, in a transfer pass the caller opened, the tables cleared the first time.
    /// </summary>
    public void Prepare(RenderGraph graph, FrameTargets targets, PostProcessing settings, float time)
    {
        _current = 1 - _current;
        if (!_isTableReady)
        {
            _isTableReady = true;
            foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[_histogram, _exposureState])
            {
                graph.Use(buffer, GpuAccess.CopyDestination);
                graph.Recorder.FillBuffer(buffer, 0);
            }
        }

        PostConstants constants = new()
        {
            Picture = targets.Picture.Id,
            History = _history[_current].Id,
            HistoryPrevious = _history[1 - _current].Id,
            Histogram = _histogram.Id,
            ExposureState = _exposureState.Id,
            Bloom = _bloom.Id,
            Sampler = _sampler.Id,
            BloomLevels = BloomLevels,
            ExposureCompensation = settings.ExposureCompensation,
            BloomStrength = settings.BloomStrength,
            ExposureMin = ExposureMin,
            ExposureMax = ExposureMax,
            Time = time,
        };
        GpuUpload upload = gpu.BeginUpload(PostConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, PostConstants.Bytes);
    }

    /// <summary>
    /// The picture to the output: composed, resolved, metered, bloomed, and finished with the camera's effects.
    /// </summary>
    public void Record(
        RenderGraph graph,
        RendererPipelines pipelines,
        GpuBuffer frameConstants,
        SunShadows shadows,
        Lighting lighting,
        DistanceField field,
        Translucency translucency,
        FrameTargets targets
    )
    {
        GpuConstants header = new(frameConstants.Id, shadows.Constants.Id, lighting.Constants.Id, field.Constants.Id);
        graph.BeginComputePass("Post: compose");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(targets.Hdr, GpuAccess.ShaderRead);
        graph.Use(targets.Picture, GpuAccess.ShaderWrite);
        translucency.UseForComposite(graph, targets);
        graph.Recorder.BindPipeline(pipelines.PostCompose);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(targets.Width), Groups(targets.Height));
        graph.EndPass();

        graph.BeginComputePass("Post: resolve");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(targets.Picture, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.Velocity, GpuAccess.ShaderRead);
        graph.Use(_history[1 - _current], GpuAccess.ShaderRead);
        graph.Use(_history[_current], GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.PostResolve);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(_outputWidth), Groups(_outputHeight));
        graph.EndPass();

        graph.BeginComputePass("Post: histogram");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_history[_current], GpuAccess.ShaderRead);
        graph.Use(_histogram, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.PostHistogram);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(_outputWidth, 16), Groups(_outputHeight, 16));
        graph.EndPass();

        graph.BeginComputePass("Post: exposure");
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_histogram, GpuAccess.ShaderWrite);
        graph.Use(_exposureState, GpuAccess.ShaderWrite);
        graph.Recorder.BindPipeline(pipelines.PostExposure);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(1);
        graph.EndPass();

        for (int level = 0; level < BloomLevels; level++)
        {
            graph.BeginComputePass($"Post: bloom down {level}");
            graph.Use(lighting.Constants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_history[_current], GpuAccess.ShaderRead);
            graph.Use(_exposureState, GpuAccess.ShaderRead);
            if (level > 0)
                graph.Use(_bloom, GpuAccess.ShaderRead, level - 1);

            graph.Use(_bloom, GpuAccess.ShaderWrite, level);
            graph.Recorder.BindPipeline(pipelines.PostBloomDown);
            graph.Recorder.PushConstants(new GpuConstants(frameConstants.Id, (uint)level, lighting.Constants.Id));
            graph.Recorder.Dispatch(Groups(BloomWidth(level)), Groups(BloomHeight(level)));
            graph.EndPass();
        }

        for (int level = BloomLevels - 2; level >= 0; level--)
        {
            graph.BeginComputePass($"Post: bloom up {level}");
            graph.Use(lighting.Constants, GpuAccess.ShaderRead);
            graph.Use(Constants, GpuAccess.ShaderRead);
            graph.Use(_bloom, GpuAccess.ShaderRead, level + 1);
            graph.Use(_bloom, GpuAccess.ShaderWrite, level);
            graph.Recorder.BindPipeline(pipelines.PostBloomUp);
            graph.Recorder.PushConstants(new GpuConstants(frameConstants.Id, (uint)level, lighting.Constants.Id));
            graph.Recorder.Dispatch(Groups(BloomWidth(level)), Groups(BloomHeight(level)));
            graph.EndPass();
        }

        graph.BeginComputePass("Post: final");
        graph.Use(frameConstants, GpuAccess.ShaderRead);
        graph.Use(lighting.Constants, GpuAccess.ShaderRead);
        graph.Use(Constants, GpuAccess.ShaderRead);
        graph.Use(_history[_current], GpuAccess.ShaderRead);
        graph.Use(_bloom, GpuAccess.ShaderRead);
        graph.Use(_exposureState, GpuAccess.ShaderRead);
        graph.Use(targets.Visibility, GpuAccess.ShaderRead);
        graph.Use(targets.Depth, GpuAccess.ShaderRead);
        graph.Use(targets.GBufferNormal, GpuAccess.ShaderRead);
        graph.Use(targets.Output, GpuAccess.ShaderWrite);
        field.UseForSampling(graph);
        graph.Recorder.BindPipeline(_final.IsNone ? pipelines.PostFinal : _final);
        graph.Recorder.PushConstants(header);
        graph.Recorder.Dispatch(Groups(_outputWidth), Groups(_outputHeight));
        graph.EndPass();
    }

    private static uint Groups(int count, int size = 8) => (uint)((count + size - 1) / size);

    private async Task CompileAsync(
        RendererPipelines pipelines,
        ImmutableArray<Handle<Shader>> effects,
        CancellationToken cancel
    )
    {
        Result<CompiledShader> result = await pipelines.CompileFinalAsync(effects, cancel).ConfigureAwait(false);
        Interlocked.Exchange(ref _compiled, new CompiledBox(result));
    }

    private int BloomWidth(int level) => Math.Max((_outputWidth / 2) >> level, 1);

    private int BloomHeight(int level) => Math.Max((_outputHeight / 2) >> level, 1);

    private void Destroy(RenderGraph? graph)
    {
        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[_history[0], _history[1], _bloom])
        {
            if (texture.IsNone)
                continue;

            graph?.Forget(texture);
            gpu.Destroy(texture);
        }

        _history[0] = default;
        _history[1] = default;
        _bloom = default;
    }
}

/// <summary>
/// A compiled final pass handed from the worker that compiled it to the main thread.
/// </summary>
internal sealed record CompiledBox(Result<CompiledShader> Result);

/// <summary>
/// What the post passes read, the twin of <c>Scene/Post.slang</c>'s.
/// </summary>
[ShaderStruct]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct PostConstants
{
    public const int Bytes = 64;

    public uint Picture;

    public uint History;

    public uint HistoryPrevious;

    public uint Histogram;

    public uint ExposureState;

    public uint Bloom;

    public uint Sampler;

    public uint BloomLevels;

    public float ExposureCompensation;

    public float BloomStrength;

    public float ExposureMin;

    public float ExposureMax;

    public float Time;

    public float Padding0;

    public float Padding1;

    public float Padding2;
}
