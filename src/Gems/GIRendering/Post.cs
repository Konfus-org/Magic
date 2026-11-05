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

namespace Magic.GIRendering;

/// <summary>
/// The picture after the lighting, to the output: the panes composed over it, each frame resolved into the output's
/// size over the last, the exposure metered, the bloom, and the final pass with the camera's effects and the
/// tonemap. The final pass is specialised to the camera's effects, compiled again on a worker when they change and
/// swapped in once built.
/// </summary>
internal sealed class Post(
    IGraphicsApi gpu,
    RenderGraph graph,
    GpuPipelines pipelines,
    Lighting lighting,
    GlassDraws glassDraws
) : IDisposable
{
    public const int BloomLevels = 6;

    private const int HistogramBins = 256;

    private const float ExposureMin = 1e-5f;

    private const float ExposureMax = 1e4f;

    private readonly GpuPass _composePass = pipelines.Compute(
        "Post: compose",
        "PostPasses",
        "PostCompose",
        isTiled: true
    );

    private readonly GpuPass _resolvePass = pipelines.Compute("Post: resolve", "PostPasses", "PostResolve");

    private readonly GpuPass _histogramPass = pipelines.Compute("Post: histogram", "PostPasses", "PostHistogram");

    private readonly GpuPass _exposurePass = pipelines.Compute("Post: exposure", "PostPasses", "PostExposure");

    private readonly GpuPass _bloomDownPass = pipelines.Compute("Post: bloom down", "PostPasses", "PostBloomDown");

    private readonly GpuPass _bloomUpPass = pipelines.Compute("Post: bloom up", "PostPasses", "PostBloomUp");

    private readonly GpuPass _finalPass = pipelines.Compute(
        "Post: final",
        "PostPasses",
        "PostFinal",
        PassShaders.NoEffects
    );

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

    private bool _isExposureReady;

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
    public Result Resize(int width, int height)
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
    public void Update(PostProcessing settings, CancellationToken cancel)
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
        _compiling = CompileAsync(wanted, cancel);
    }

    /// <summary>
    /// The frame's constants uploaded, in a transfer pass the caller opened, the tables cleared the first time.
    /// </summary>
    public void Prepare(FrameTargets targets, PostProcessing settings, float time, bool hasReflections)
    {
        _current = 1 - _current;
        if (!_isTableReady)
        {
            _isTableReady = true;
            graph.Use(_histogram, GpuAccess.CopyDestination);
            graph.Recorder.FillBuffer(_histogram, 0);
        }

        // Once only: the exposure outlasts a resize, as a debug view of one part of the light can't meter it again.
        if (!_isExposureReady)
        {
            _isExposureReady = true;
            graph.Use(_exposureState, GpuAccess.CopyDestination);
            graph.Recorder.FillBuffer(_exposureState, 0);
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
            HasReflections = hasReflections ? 1u : 0u,
        };
        GpuUpload upload = gpu.BeginUpload(PostConstants.Bytes);
        MemoryMarshal.Write(upload.Bytes, in constants);
        graph.Use(Constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(upload.Buffer, upload.Offset, Constants, 0, PostConstants.Bytes);
    }

    /// <summary>
    /// The picture to the output: composed, resolved, metered, bloomed, and finished with the camera's effects.
    /// </summary>
    public void Record(in RenderView view)
    {
        GpuBuffer frameConstants = view.Constants;
        FrameTargets targets = view.Targets;
        GpuConstants header = lighting.GetHeader(frameConstants);
        GpuPassRun composeRun = _composePass
            .Run(header)
            .Reads(frameConstants, lighting.Constants, Constants, targets.Hdr)
            .Writes(targets.Picture);
        glassDraws.UseForComposite(targets);
        composeRun.Dispatch(Groups(targets.Width), Groups(targets.Height));

        _resolvePass
            .Run(header)
            .Reads(
                frameConstants,
                lighting.Constants,
                Constants,
                targets.Picture,
                targets.Depth,
                targets.Velocity,
                _history[1 - _current]
            )
            .Writes(_history[_current])
            .Dispatch(Groups(_outputWidth), Groups(_outputHeight));

        _histogramPass
            .Run(header)
            .Reads(frameConstants, lighting.Constants, Constants, _history[_current])
            .Writes(_histogram)
            .Dispatch(Groups(_outputWidth, 16), Groups(_outputHeight, 16));

        _exposurePass.Run(header).Reads(lighting.Constants, Constants).Writes(_histogram, _exposureState).Dispatch(1);

        for (int level = 0; level < BloomLevels; level++)
        {
            GpuPassRun bloomDownRun = _bloomDownPass
                .Run(
                    new GpuConstants(frameConstants.Id, (uint)level, lighting.Constants.Id),
                    name: $"Post: bloom down {level}"
                )
                .Reads(lighting.Constants, Constants, _history[_current], _exposureState);
            if (level > 0)
                bloomDownRun.Reads(_bloom, level - 1);

            bloomDownRun.Writes(_bloom, level).Dispatch(Groups(BloomWidth(level)), Groups(BloomHeight(level)));
        }

        for (int level = BloomLevels - 2; level >= 0; level--)
        {
            _bloomUpPass
                .Run(
                    new GpuConstants(frameConstants.Id, (uint)level, lighting.Constants.Id),
                    name: $"Post: bloom up {level}"
                )
                .Reads(lighting.Constants, Constants)
                .Reads(_bloom, level + 1)
                .Writes(_bloom, level)
                .Dispatch(Groups(BloomWidth(level)), Groups(BloomHeight(level)));
        }

        _finalPass
            .Run(header, pipeline: _final)
            .Reads(
                frameConstants,
                lighting.Constants,
                Constants,
                _history[_current],
                _bloom,
                _exposureState,
                targets.Picture,
                targets.Visibility,
                targets.Depth,
                targets.GBufferNormal,
                targets.GBufferBase,
                targets.GBufferSurface,
                targets.Velocity
            )
            .Writes(targets.Output)
            .Dispatch(Groups(_outputWidth), Groups(_outputHeight));
    }

    private static uint Groups(int count, int size = 8) => (uint)((count + size - 1) / size);

    private async Task CompileAsync(ImmutableArray<Handle<Shader>> effects, CancellationToken cancel)
    {
        Result<CompiledShader> result = await pipelines.CompileFinalAsync(effects, cancel).ConfigureAwait(false);
        Interlocked.Exchange(ref _compiled, new CompiledBox(result));
    }

    private int BloomWidth(int level) => Math.Max((_outputWidth / 2) >> level, 1);

    private int BloomHeight(int level) => Math.Max((_outputHeight / 2) >> level, 1);

    private void Destroy(RenderGraph? forgetIn)
    {
        foreach (GpuTexture texture in (ReadOnlySpan<GpuTexture>)[_history[0], _history[1], _bloom])
        {
            if (texture.IsNone)
                continue;

            forgetIn?.Forget(texture);
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

    public uint HasReflections;

    public float Padding1;

    public float Padding2;
}
