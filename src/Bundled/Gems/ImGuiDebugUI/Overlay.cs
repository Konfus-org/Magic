using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// Draws ImGui's frame over the window, a frame behind, from the <see cref="DrawLists"/> the main thread copied: takes
/// in what changed in ImGui's textures, uploads the vertices and indices, and records one render pass of draws onto
/// what the gems before it drew. It owns every GPU object it draws with: its textures, buffers, sampler and a pipeline
/// per target format, made the first time one is drawn into. Render thread.
/// </summary>
internal sealed class Overlay : IDisposable
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
    private readonly IWindow _window;
    private readonly GpuSampler _sampler;
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

    public Overlay(IGpu gpu, IWindow window, IShaderCompiler compiler, Assets assets, Threads threads, CancellationToken stopping)
    {
        _gpu = gpu;
        _window = window;
        _sampler = gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddress.Clamp));
        CompileAsync(compiler, assets, threads, stopping).FireAndForget();
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
    /// Takes in <paramref name="lists"/>' texture changes, then draws them over the window; nothing while the window has
    /// no image or the pipeline is not made.
    /// </summary>
    public void Draw(DrawLists lists)
    {
        Apply(lists.Textures);
        lists.Textures.Clear();

        GpuTexture target = _gpu.Target(_window);
        Size size = _gpu.Size(target);
        GpuPipeline pipeline = PipelineFor(_gpu.Format(target));
        if (lists.Calls.Count == 0 || size.IsEmpty || pipeline == GpuPipeline.None)
        {
            return;
        }

        Upload(ref _vertexBuffer, ref _vertexBytes, GpuBufferUsage.Vertex, MemoryMarshal.AsBytes(lists.Vertices));
        Upload(ref _indexBuffer, ref _indexBytes, GpuBufferUsage.Index, MemoryMarshal.AsBytes(lists.Indices));

        GpuCommands commands = _gpu.Commands;
        commands.PushDebugGroup("Debug UI");
        commands.BeginRenderPass([new GpuBinding(Texture: target)], GpuLoad.Load);
        commands.SetViewport(new Rectangle(Point.Empty, size));
        commands.BindPipeline(pipeline);
        commands.BindVertexBuffers(0, [_vertexBuffer]);
        commands.BindIndexBuffer(_indexBuffer, wide: false);
        commands.Push(GpuStage.Vertex, new Vector4(1f / size.Width, 1f / size.Height, 0, 0));

        uint bound = 0;
        foreach (DrawCall call in lists.Calls)
        {
            if (!_textures.TryGetValue(call.Texture, out (GpuTexture Texture, int, int) texture))
            {
                continue;
            }

            if (bound != call.Texture)
            {
                commands.BindTextures(GpuStage.Fragment, 0, [new GpuBinding(Texture: texture.Texture, Sampler: _sampler)]);
                bound = call.Texture;
            }

            commands.SetScissor(call.Clip);
            commands.DrawIndexed(call.Indices, call.FirstIndex, call.VertexOffset);
        }

        commands.EndRenderPass();
        commands.PopDebugGroup();
    }

    /// <summary>
    /// Each change into its GPU texture, made again when its size changed, or released when ImGui let it go.
    /// </summary>
    private void Apply(List<TextureChange> changes)
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
                known = (_gpu.CreateTexture(new GpuTextureDesc(GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampler, (uint)change.Width, (uint)change.Height)), change.Width, change.Height);
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
            ? _gpu.CreatePipeline(new GpuPipelineDesc
            {
                Vertex = vertex.Value,
                Fragment = fragment.Value,
                Colors = [format],
                Blends = [GpuBlend.Alpha],
                Buffers = _buffers,
                Inputs = _inputs,
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
