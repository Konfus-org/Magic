using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ImGuiDebugUI.Resources.Scripts;

/// <summary>
/// The pipeline script that draws the debug UI: ImGui's frame, a frame behind, from the <see cref="DrawLists"/> the
/// main thread copied, over whatever target its pass names, the window once every view in it is composed
/// (<c>Resources/Passes/DebugUi.pass</c>, in <c>Resources/Pipelines/Overlay.pipeline</c>). It takes in what changed in
/// ImGui's textures, uploads the vertices and indices, and records the draws into the pass's render pass, encoded as
/// the target expects. It owns every GPU object it draws with: its textures, buffers, sampler and a pipeline per
/// target format, made the first time one is drawn into, and lets them go with the listing that made it. Render
/// thread.
/// </summary>
internal sealed class DebugUi : IPipelineScript, IDisposable
{
    private const string VertexShader = "Shaders/Overlay/Overlay.vert.hlsl";
    private const string FragmentShader = "Shaders/Overlay/Overlay.frag.hlsl";

    private static readonly GpuVertexBuffer[] _buffers = [new(0, (uint)Unsafe.SizeOf<ImDrawVert>())];

    private static readonly GpuVertexInput[] _inputs =
    [
        new(0, 0, GpuVertexFormat.Float2, 0),
        new(1, 0, GpuVertexFormat.Float2, 8),
        new(2, 0, GpuVertexFormat.Ubyte4Norm, 16),
    ];

    private readonly IGpu _gpu;
    private readonly DrawLists _lists;
    private GpuSampler _sampler;
    private readonly Dictionary<uint, (GpuTexture Texture, int Width, int Height)> _textures = [];
    private readonly Dictionary<GpuFormat, GpuPipeline> _pipelines = [];
    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;
    private uint _vertexBytes;
    private uint _indexBytes;

    /// <summary>
    /// Both stages, once compiled, handed to the render thread.
    /// </summary>
    private (Result<GpuShader> Vertex, Result<GpuShader> Fragment)? _shaders;

    /// <summary>
    /// Made by the renderer for each listing of the frame it builds, with the debug UI's draw lists, which the debug UI
    /// this gem provides copies each frame; another gem's debug UI has none, and the script is not made.
    /// </summary>
    public DebugUi(IDebugUI ui, IGpu gpu, IShaderCompiler compiler, Assets assets, Threads threads, Lifetime lifetime)
    {
        _gpu = gpu;
        _lists = ui is ImGuiDebugUIGem imgui ? imgui.DrawLists : throw new InvalidOperationException("The debug UI is not ImGui's, so it has no draw lists to draw.");
        CompileAsync(compiler, assets, threads, lifetime.Stopping).FireAndForget();
    }

    public void Dispose()
    {
        foreach ((GpuTexture texture, _, _) in _textures.Values)
        {
            _gpu.Release(texture);
        }

        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            _gpu.Release(pipeline);
        }

        _gpu.Release(_vertexBuffer);
        _gpu.Release(_indexBuffer);
        _gpu.Release(_sampler);
    }

    /// <summary>
    /// Takes in the draw lists' texture changes, every texture as it stands the first time, as the changes before were
    /// taken in and cleared by the copy the listing before had, then draws them over the target; nothing while there is
    /// nothing to draw or the pipeline is not made. The sampler is made here, on the render thread, the first time, so
    /// having none is what tells the first time.
    /// </summary>
    public void Record(in PipelineScriptContext context)
    {
        if (_sampler == GpuSampler.None)
        {
            _sampler = _gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
            Apply(_lists.Known.Values);
        }

        Apply(_lists.Textures);
        _lists.Textures.Clear();

        GpuPipeline pipeline = PipelineFor(context.Format);
        if (_lists.Calls.Count == 0 || context.Area.IsEmpty || pipeline == GpuPipeline.None)
        {
            return;
        }

        Upload(ref _vertexBuffer, ref _vertexBytes, GpuBufferUsage.Vertex, MemoryMarshal.AsBytes(_lists.Vertices));
        Upload(ref _indexBuffer, ref _indexBytes, GpuBufferUsage.Index, MemoryMarshal.AsBytes(_lists.Indices));

        using GpuRenderPass pass = context.Begin()
            .Pipeline(pipeline)
            .VertexBuffers([_vertexBuffer])
            .IndexBuffer(_indexBuffer, GpuIndexSize.Bits16)
            .Constants(GpuStage.Vertex, new Vector4(1f / context.Area.Width, 1f / context.Area.Height, 0, 0))
            .Constants(GpuStage.Fragment, new ImGuiConstants((uint)context.Encoding));

        uint bound = 0;
        foreach (DrawCall call in _lists.Calls)
        {
            if (!_textures.TryGetValue(call.Texture, out (GpuTexture Texture, int, int) texture))
            {
                continue;
            }

            if (bound != call.Texture)
            {
                pass.Textures(GpuStage.Fragment, [new GpuSampledTexture(texture.Texture, _sampler)]);
                bound = call.Texture;
            }

            pass.Scissor(call.Clip).DrawIndexed(call.Indices, call.FirstIndex, call.VertexOffset);
        }
    }

    /// <summary>
    /// Each change into its GPU texture, made again when its size changed, or released when ImGui let it go.
    /// </summary>
    private void Apply(IEnumerable<TextureChange> changes)
    {
        foreach (TextureChange change in changes)
        {
            _textures.TryGetValue(change.Id, out (GpuTexture Texture, int Width, int Height) known);
            if (change.Pixels is not { } pixels)
            {
                _gpu.Release(known.Texture);
                _textures.Remove(change.Id);
                continue;
            }

            if (known.Texture == GpuTexture.None || known.Width != change.Width || known.Height != change.Height)
            {
                _gpu.Release(known.Texture);
                known = (_gpu.CreateTexture(new GpuTextureDescription(GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled, (uint)change.Width, (uint)change.Height)), change.Width, change.Height);
                _textures[change.Id] = known;
            }

            _gpu.Upload(new GpuTextureRegion(known.Texture), pixels);
        }
    }

    /// <summary>
    /// The bytes into the buffer, made again bigger first when they do not fit.
    /// </summary>
    private void Upload(ref GpuBuffer buffer, ref uint size, GpuBufferUsage usage, ReadOnlySpan<byte> bytes)
    {
        if (buffer == GpuBuffer.None || size < bytes.Length)
        {
            _gpu.Release(buffer);
            size = BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes.Length, 16 * 1024));
            buffer = _gpu.CreateBuffer(usage, size);
        }

        _gpu.Upload(buffer, 0, bytes);
    }

    /// <summary>
    /// The fragment stage's constant block, laid out as <c>Overlay.frag.hlsl</c>'s <c>cbuffer Overlay</c>: the
    /// target's <see cref="TargetEncoding"/>, which the shader encodes ImGui's sRGB colours for, padded to one 16-byte
    /// row.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ImGuiConstants(uint encoding)
    {
        public readonly uint Encoding = encoding;
        public readonly uint Pad0;
        public readonly uint Pad1;
        public readonly uint Pad2;
    }

    /// <summary>
    /// The pipeline for <paramref name="format"/>, made the first time; none until the shaders are compiled, and for
    /// good once one does not compile, which is logged.
    /// </summary>
    private GpuPipeline PipelineFor(GpuFormat format)
    {
        if (_pipelines.TryGetValue(format, out GpuPipeline pipeline) || format == GpuFormat.Invalid || _shaders is not ({ } vertex, { } fragment))
        {
            return pipeline;
        }

        Result<GpuPipeline> made = vertex.Ok && fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDescription
            {
                Vertex = vertex.Value,
                Fragment = fragment.Value,
                ColorFormats = [format],
                Blends = [GpuBlend.Alpha],
                VertexBuffers = _buffers,
                VertexInputs = _inputs,
                Cull = GpuCull.None,
            })
            : Result.Failure<GpuPipeline>(vertex.Ok ? fragment.Error : vertex.Error);
        if (!made.Ok)
        {
            Debugging.Error($"The debug UI is not drawn: {made.Error}");
        }

        _pipelines[format] = made.Ok ? made.Value : GpuPipeline.None;
        return _pipelines[format];
    }

    private async Task CompileAsync(IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await CompileAsync(VertexShader, GpuStage.Vertex, compiler, assets, stopping).ConfigureAwait(false);
        Result<GpuShader> fragment = await CompileAsync(FragmentShader, GpuStage.Fragment, compiler, assets, stopping).ConfigureAwait(false);
        threads.Post(EngineThread.Render, () => _shaders = (vertex, fragment));
    }

    private async Task<Result<GpuShader>> CompileAsync(string path, GpuStage stage, IShaderCompiler compiler, Assets assets, CancellationToken stopping)
    {
        Result<Shader> shader = await assets.LoadAsync(assets.Find<Shader>(path), stopping).ConfigureAwait(false);
        return shader.Ok ? await compiler.CompileAsync(shader.Value.Text, path, stage, _gpu.ShaderFormat, stopping).ConfigureAwait(false) : Result.Failure<GpuShader>(shader.Error);
    }
}
