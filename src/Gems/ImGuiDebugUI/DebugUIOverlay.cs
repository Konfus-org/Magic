using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// ImGui's frame drawn over the window's image from the newest copy the UI published, again until a newer comes, in the
/// overlay layer so it shows over the picture whichever gem drew it: it takes in the
/// texture changes queued up to that frame, uploads the vertices and indices, and records the draws into a pass of their
/// own, its shaders reading the vertices, the draw's texture and the constants through the heap. It owns every GPU object
/// it draws with. Render thread, but the compile on its worker.
/// </summary>
internal sealed class DebugUIOverlay : IDisposable
{
    private const string ShaderPath = "Shaders/DrawDebugUI.slang";
    private const int MinBufferBytes = 16 * 1024;

    private readonly IGraphicsApi _gpu;
    private readonly Mailbox<DebugUIFrame> _frames;
    private readonly ChannelReader<DebugUITextureChange> _textureChanges;

    /// <summary>
    /// By the id the UI gave it, each of ImGui's textures made.
    /// </summary>
    private readonly Dictionary<ulong, GpuTexture> _textures = [];
    private GpuBuffer _constants;
    private GpuBuffer _vertices;
    private GpuBuffer _indices;
    private GpuSampler _sampler;
    private GpuPipeline _pipeline;
    private GpuFormat _pipelineFormat;

    /// <summary>
    /// The newest copy taken, this overlay's until a newer is.
    /// </summary>
    private DebugUIFrame? _frame;

    /// <summary>
    /// Both stages, written whole by the compile on its worker and read by <see cref="Record"/>: null while it compiles,
    /// and for good once it or the pipeline fails, which is logged.
    /// </summary>
    private volatile CompiledStages? _compiled;

    /// <summary>
    /// Starts compiling the overlay's shaders, which stop with <paramref name="lifetime"/>.
    /// </summary>
    public DebugUIOverlay(
        IGraphicsApi gpu,
        IShaderCompiler shaders,
        Assets assets,
        Mailbox<DebugUIFrame> frames,
        ChannelReader<DebugUITextureChange> textureChanges,
        CancellationToken lifetime
    )
    {
        _gpu = gpu;
        _frames = frames;
        _textureChanges = textureChanges;
        CompileAsync(shaders, assets.Find<Shader>(ShaderPath), lifetime).FireAndForget();
    }

    public void Dispose()
    {
        foreach (GpuTexture texture in _textures.Values)
        {
            _gpu.Destroy(texture);
        }

        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[_constants, _vertices, _indices])
        {
            _gpu.Destroy(buffer);
        }

        _gpu.Destroy(_sampler);
        _gpu.Destroy(_pipeline);
    }

    /// <summary>
    /// The newest frame the UI published over the window's image, the texture changes up to it taken in first, and
    /// nothing drawn while there is no window, nothing to draw or the pipeline isn't made.
    /// </summary>
    public void Record(RenderGraph graph)
    {
        if (_frames.TryTake(out DebugUIFrame? newest))
        {
            if (_frame is { } drawn)
                _frames.Return(drawn);

            _frame = newest;
        }

        if (_frame is not { } frame)
            return;

        // The texture changes are taken whether or not there is a window to draw over, so none wait for good.
        TakeTextureChanges(frame.Index, graph);
        GpuTexture target = _gpu.WindowTarget;
        if (target.IsNone)
            return;

        TextureDescription description = _gpu.GetDescription(target);
        if (!TryCreateResources(description.Format))
            return;

        graph.BeginTransferPass("Debug UI: upload", RenderLayer.Overlay);
        bool canDraw = frame.Indices.Count > 0 && TryUpload(frame, graph, description);
        graph.EndPass();
        if (canDraw)
            RecordDraws(frame, graph, target, description);
    }

    /// <summary>
    /// The compiled stages, compiling one each from <paramref name="shader"/>; a stage that fails is logged, and the UI
    /// stays undrawn.
    /// </summary>
    private async Task CompileAsync(IShaderCompiler shaders, Handle<Shader> shader, CancellationToken cancel)
    {
        ShaderEntry vertex = new()
        {
            Module = shader,
            Name = "DrawDebugUIVertex",
            Stage = ShaderStage.Vertex,
        };
        ShaderEntry pixel = new()
        {
            Module = shader,
            Name = "DrawDebugUIPixel",
            Stage = ShaderStage.Pixel,
        };
        Task<Result<CompiledShader>> vertexCompile = shaders.CompileAsync(vertex, null, cancel);
        Task<Result<CompiledShader>> pixelCompile = shaders.CompileAsync(pixel, null, cancel);
        Result<CompiledShader> vertexResult = await vertexCompile.ConfigureAwait(false);
        Result<CompiledShader> pixelResult = await pixelCompile.ConfigureAwait(false);
        if (!vertexResult.Ok || !pixelResult.Ok)
        {
            Log.Error($"The debug UI is not drawn: {(vertexResult.Ok ? pixelResult.Error : vertexResult.Error)}");

            return;
        }

        _compiled = new CompiledStages(vertexResult.Value, pixelResult.Value);
    }

    /// <summary>
    /// Whether everything that lasts is made: the sampler, the constants and the pipeline for <paramref name="format"/>,
    /// once the shaders compile. A failure is logged once, and the UI stays undrawn.
    /// </summary>
    private bool TryCreateResources(GpuFormat format)
    {
        if (_compiled is not { } compiled)
            return false;

        if (!_pipeline.IsNone && _pipelineFormat == format)
            return true;

        Result made = CreateResources(compiled, format);
        if (made.Ok)
            return true;

        Log.Error($"The debug UI is not drawn: {made.Error}");
        _compiled = null;

        return false;
    }

    private Result CreateResources(CompiledStages compiled, GpuFormat format)
    {
        Result<GpuPipeline> pipeline = _gpu.CreatePipeline(
            new GraphicsPipelineDescription
            {
                VertexSpirv = compiled.Vertex.Spirv,
                VertexEntryPoint = compiled.Vertex.EntryPoint,
                PixelSpirv = compiled.Pixel.Spirv,
                PixelEntryPoint = compiled.Pixel.EntryPoint,
                ColorFormats = [format],
                Blend = BlendMode.Alpha,
                Cull = CullMode.None,
                Name = "Debug UI",
            }
        );
        if (!pipeline.Ok)
            return Result.Failure(pipeline.Error);

        _gpu.Destroy(_pipeline);
        _pipeline = pipeline.Value;
        _pipelineFormat = format;
        if (_sampler.IsNone)
        {
            Result<GpuSampler> sampler = _gpu.CreateSampler(
                new SamplerDescription { Wrap = TextureWrap.Clamp, HasMipmapBlend = false }
            );
            if (!sampler.Ok)
                return Result.Failure(sampler.Error);

            _sampler = sampler.Value;
        }

        if (!_constants.IsNone)
            return Result.Success();

        Result<GpuBuffer> constants = _gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = DebugUIConstants.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Debug UI constants",
            }
        );
        if (!constants.Ok)
            return Result.Failure(constants.Error);

        _constants = constants.Value;

        return Result.Success();
    }

    /// <summary>
    /// The texture changes queued up to <paramref name="frame"/>: a texture made or filled again, or destroyed. One let go of in a later frame may still be drawn in this one, so later changes wait.
    /// </summary>
    private void TakeTextureChanges(uint frame, RenderGraph graph)
    {
        while (_textureChanges.TryPeek(out DebugUITextureChange change) && change.Frame <= frame)
        {
            _textureChanges.TryRead(out _);
            if (change.Pixels is { } pixels)
                FillTexture(change, pixels, graph);
            else
                DestroyTexture(change.Texture);
        }
    }

    /// <summary>
    /// The texture made, unless it is, and filled with <paramref name="pixels"/> whole.
    /// </summary>
    private void FillTexture(in DebugUITextureChange change, byte[] pixels, RenderGraph graph)
    {
        if (!_textures.TryGetValue(change.Texture, out GpuTexture texture))
        {
            Result<GpuTexture> made = _gpu.CreateTexture(
                new TextureDescription
                {
                    Width = change.Width,
                    Height = change.Height,
                    Format = GpuFormat.R8G8B8A8Unorm,
                    Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
                    Name = "Debug UI texture",
                }
            );
            if (!made.Ok)
            {
                Log.Error($"A debug UI texture was not made: {made.Error}");

                return;
            }

            texture = made.Value;
            _textures.Add(change.Texture, texture);
        }

        GpuUpload upload = _gpu.BeginUpload(pixels.Length);
        pixels.CopyTo(upload.Bytes);
        // A pass each, as a pass's barriers come before all of it and a texture filled twice in a frame needs one between.
        graph.BeginTransferPass("Debug UI: texture", RenderLayer.Overlay);
        graph.Use(texture, GpuAccess.CopyDestination);
        graph.CopyBufferToTexture(upload.Buffer, upload.Offset, texture);
        graph.EndPass();
    }

    private void DestroyTexture(ulong id)
    {
        if (!_textures.Remove(id, out GpuTexture gone))
            return;

        _gpu.Destroy(gone);
    }

    /// <summary>
    /// The frame's vertices and indices into their buffers, with the constants, in the transfer pass open; whether they
    /// fit, as the frame has nothing to draw from if not.
    /// </summary>
    private bool TryUpload(DebugUIFrame frame, RenderGraph graph, in TextureDescription target)
    {
        ReadOnlySpan<byte> vertices = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(frame.Vertices));
        ReadOnlySpan<byte> indices = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(frame.Indices));
        int vertexBytes = vertices.Length;
        int indexBytes = indices.Length;
        if (!TryFit(ref _vertices, vertexBytes, BufferUsages.Storage, "Debug UI vertices"))
            return false;

        if (!TryFit(ref _indices, indexBytes, BufferUsages.Index, "Debug UI indices"))
            return false;

        GpuUpload vertexUpload = _gpu.BeginUpload(vertexBytes);
        GpuUpload indexUpload = _gpu.BeginUpload(indexBytes);
        vertices.CopyTo(vertexUpload.Bytes);
        indices.CopyTo(indexUpload.Bytes);
        graph.Use(_vertices, GpuAccess.CopyDestination);
        graph.CopyBuffer(vertexUpload.Buffer, vertexUpload.Offset, _vertices, 0, (ulong)vertexBytes);
        graph.Use(_indices, GpuAccess.CopyDestination);
        graph.CopyBuffer(indexUpload.Buffer, indexUpload.Offset, _indices, 0, (ulong)indexBytes);

        DebugUIConstants constants = new()
        {
            InverseSize = new Vector2(1f / target.Width, 1f / target.Height),
            Vertices = _vertices.Id,
            Sampler = _sampler.Id,
        };
        GpuUpload constantUpload = _gpu.BeginUpload(DebugUIConstants.Bytes);
        MemoryMarshal.Write(constantUpload.Bytes, in constants);
        graph.Use(_constants, GpuAccess.CopyDestination);
        graph.CopyBuffer(constantUpload.Buffer, constantUpload.Offset, _constants, 0, DebugUIConstants.Bytes);

        return true;
    }

    /// <summary>
    /// Whether <paramref name="buffer"/> holds <paramref name="bytes"/>, made again at the power of two that does when it
    /// doesn't, so it seldom has to grow.
    /// </summary>
    private bool TryFit(ref GpuBuffer buffer, int bytes, BufferUsages usage, string name)
    {
        if (!buffer.IsNone && _gpu.GetDescription(buffer).Bytes >= bytes)
            return true;

        Result<GpuBuffer> made = _gpu.CreateBuffer(
            new BufferDescription
            {
                Bytes = BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes, MinBufferBytes)),
                Usage = usage,
                Name = name,
            }
        );
        if (!made.Ok)
        {
            Log.Error($"The {name} buffer was not made: {made.Error}");

            return false;
        }

        _gpu.Destroy(buffer);
        buffer = made.Value;

        return true;
    }

    /// <summary>
    /// Each draw clipped to its rectangle, its vertices counted from its first and its texture read: a pass that draws
    /// over what the target holds.
    /// </summary>
    private void RecordDraws(
        DebugUIFrame frame,
        RenderGraph graph,
        GpuTexture target,
        in TextureDescription description
    )
    {
        graph.BeginDrawPass(
            "Debug UI",
            new GpuRenderTargets { Colors = [new GpuColorTarget { Texture = target, Load = TargetLoad.Keep }] },
            RenderLayer.Overlay
        );
        graph.Use(_constants, GpuAccess.ShaderRead);
        graph.Use(_vertices, GpuAccess.ShaderRead);
        graph.Use(_indices, GpuAccess.Index);
        foreach (GpuTexture texture in _textures.Values)
        {
            graph.Use(texture, GpuAccess.ShaderRead);
        }

        graph.BindPipeline(_pipeline);
        graph.BindIndexBuffer(_indices);
        foreach (DebugUIDraw draw in frame.Draws)
        {
            RecordDraw(graph, draw, description);
        }

        graph.EndPass();
    }

    private void RecordDraw(RenderGraph graph, in DebugUIDraw draw, in TextureDescription target)
    {
        // Clipped to the target, in whole pixels rounded out.
        int left = Math.Max((int)MathF.Floor(draw.Clip.X), 0);
        int top = Math.Max((int)MathF.Floor(draw.Clip.Y), 0);
        int right = Math.Min((int)MathF.Ceiling(draw.Clip.Z), target.Width);
        int bottom = Math.Min((int)MathF.Ceiling(draw.Clip.W), target.Height);
        if (right <= left || bottom <= top)
            return;

        // A texture not made yet is left out.
        if (!_textures.TryGetValue(draw.Texture, out GpuTexture texture))
            return;

        graph.SetScissor((uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
        graph.PushConstants(new GpuConstants(_constants.Id, draw.FirstVertex, texture.Id));
        graph.DrawIndexed(draw.IndexCount, firstIndex: draw.FirstIndex);
    }

    /// <summary>
    /// The two stages as compiled, boxed so they are written whole from the compile's worker.
    /// </summary>
    private sealed record CompiledStages(CompiledShader Vertex, CompiledShader Pixel);
}

/// <summary>
/// What the debug UI's shaders read, the twin of <c>DrawDebugUI.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct DebugUIConstants
{
    public const int Bytes = 16;

    public Vector2 InverseSize;

    public uint Vertices;

    public uint Sampler;
}
