using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;
using static Magic.ImGuiDebugUI.ImGui;
using static Magic.ImGuiDebugUI.ImTextureStatus;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// ImGui's frame drawn over a target: it takes in ImGui's textures as they are, uploads the vertices and indices, and
/// records the draws into a pass of their own, its shaders reading the vertices, the draw's texture and the constants
/// through the heap. It owns every GPU object it draws with. Main thread, but the compile on its worker.
/// </summary>
internal sealed class DebugUIOverlay : IDisposable
{
    private const string ShaderPath = "Shaders/DrawDebugUI.slang";
    private const int MinBufferBytes = 16 * 1024;

    private readonly IGraphicsApi _gpu;
    private readonly List<GpuTexture> _textures = [];
    private GpuBuffer _constants;
    private GpuBuffer _vertices;
    private GpuBuffer _indices;
    private GpuSampler _sampler;
    private GpuPipeline _pipeline;
    private GpuFormat _pipelineFormat;

    /// <summary>
    /// Both stages, written whole by the compile on its worker and read by <see cref="Record"/>.
    /// </summary>
    private volatile CompiledStages? _compiled;
    private bool _hasFailed;

    /// <summary>
    /// Starts compiling the overlay's shaders, which stop with <paramref name="lifetime"/>.
    /// </summary>
    public DebugUIOverlay(IGraphicsApi gpu, IShaderCompiler shaders, Assets assets, CancellationToken lifetime)
    {
        _gpu = gpu;
        CompileAsync(shaders, assets.Find<Shader>(ShaderPath), lifetime).FireAndForget();
    }

    public void Dispose()
    {
        foreach (GpuTexture texture in _textures)
        {
            _gpu.Destroy(texture);
        }

        foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[_constants, _vertices, _indices])
        {
            if (!buffer.IsNone)
                _gpu.Destroy(buffer);
        }

        if (!_sampler.IsNone)
            _gpu.Destroy(_sampler);

        if (!_pipeline.IsNone)
            _gpu.Destroy(_pipeline);
    }

    /// <summary>
    /// <paramref name="data"/>, the frame ImGui just ended, over <paramref name="target"/>: its textures taken in first,
    /// and nothing drawn while there is nothing to draw or the pipeline isn't made.
    /// </summary>
    public unsafe void Record(ImDrawData_t* data, RenderGraph graph, GpuTexture target)
    {
        TextureDescription description = _gpu.GetDescription(target);
        if (!TryCreateResources(description.Format))
            return;

        graph.BeginTransferPass("Debug UI: upload");
        TakeTextures(data, graph);
        bool canDraw = data->TotalIdxCount > 0 && TryUpload(data, graph, description);
        graph.EndPass();
        if (canDraw)
            RecordDraws(data, graph, target);
    }

    /// <summary>
    /// The compiled stages, compiling one each from <paramref name="shader"/>.
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
        _compiled = new CompiledStages(vertexResult, pixelResult);
    }

    /// <summary>
    /// Whether everything that lasts is made: the sampler, the constants and the pipeline for <paramref name="format"/>,
    /// once the shaders compile. A failure is logged once, and the UI stays undrawn.
    /// </summary>
    private bool TryCreateResources(GpuFormat format)
    {
        if (_hasFailed || _compiled is not { } compiled)
            return false;

        if (!_pipeline.IsNone && _pipelineFormat == format)
            return true;

        Result made = CreateResources(compiled, format);
        if (made.Ok)
            return true;

        Log.Error($"The debug UI is not drawn: {made.Error}");
        _hasFailed = true;

        return false;
    }

    private Result CreateResources(CompiledStages compiled, GpuFormat format)
    {
        if (!compiled.Vertex.Ok)
            return Result.Failure(compiled.Vertex.Error);

        if (!compiled.Pixel.Ok)
            return Result.Failure(compiled.Pixel.Error);

        Result<GpuPipeline> pipeline = _gpu.CreatePipeline(
            new GraphicsPipelineDescription
            {
                VertexSpirv = compiled.Vertex.Value.Spirv,
                VertexEntryPoint = compiled.Vertex.Value.EntryPoint,
                PixelSpirv = compiled.Pixel.Value.Spirv,
                PixelEntryPoint = compiled.Pixel.Value.EntryPoint,
                ColorFormats = [format],
                Blend = BlendMode.Alpha,
                Cull = CullMode.None,
                Name = "Debug UI",
            }
        );
        if (!pipeline.Ok)
            return Result.Failure(pipeline.Error);

        if (!_pipeline.IsNone)
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
    /// What ImGui asks of its textures, in the transfer pass open: a new one made and filled, a changed one filled again
    /// and one it let go of destroyed, once the frame just ended draws nothing with it. A texture is known to ImGui by
    /// its GPU id.
    /// </summary>
    private unsafe void TakeTextures(ImDrawData_t* data, RenderGraph graph)
    {
        ImVector_ImTextureDataPtr_t* textures = data->Textures;
        for (int i = 0; textures != null && i < textures->Size; i++)
        {
            ImTextureData_t* texture = textures->Data[i];
            if (texture->Status == ImTextureStatus_WantCreate)
            {
                Result<GpuTexture> made = _gpu.CreateTexture(
                    new TextureDescription
                    {
                        Width = texture->Width,
                        Height = texture->Height,
                        Format = GpuFormat.R8G8B8A8Unorm,
                        Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
                        Name = "Debug UI texture",
                    }
                );
                if (!made.Ok)
                {
                    Log.Error($"A debug UI texture was not made: {made.Error}");
                    continue;
                }

                _textures.Add(made.Value);
                ImTextureData_SetTexID(texture, made.Value.Id);
            }

            if (texture->Status is ImTextureStatus_WantCreate or ImTextureStatus_WantUpdates)
            {
                FillTexture(texture, graph);
                ImTextureData_SetStatus(texture, ImTextureStatus_OK);
            }
            else if (texture->Status == ImTextureStatus_WantDestroy && texture->UnusedFrames > 0)
            {
                GpuTexture gone = new((uint)texture->TexID);
                _textures.Remove(gone);
                graph.Forget(gone);
                _gpu.Destroy(gone);
                ImTextureData_SetTexID(texture, 0);
                ImTextureData_SetStatus(texture, ImTextureStatus_Destroyed);
            }
        }
    }

    /// <summary>
    /// The texture's pixels whole, as RGBA8, which is how ImGui's atlas holds them.
    /// </summary>
    private unsafe void FillTexture(ImTextureData_t* texture, RenderGraph graph)
    {
        int bytes = texture->Width * texture->Height * 4;
        GpuUpload upload = _gpu.BeginUpload(bytes);
        new ReadOnlySpan<byte>(texture->Pixels, bytes).CopyTo(upload.Bytes);
        GpuTexture target = new((uint)texture->TexID);
        graph.Use(target, GpuAccess.CopyDestination);
        graph.Recorder.CopyBufferToTexture(upload.Buffer, upload.Offset, target);
    }

    /// <summary>
    /// The vertices of every draw list in one buffer and their indices, widened to the 32 bits a draw takes, in another,
    /// with the constants, in the transfer pass open; whether they fit, as the frame has nothing to draw from if not.
    /// </summary>
    private unsafe bool TryUpload(ImDrawData_t* data, RenderGraph graph, in TextureDescription target)
    {
        int vertexBytes = data->TotalVtxCount * sizeof(ImDrawVert_t);
        int indexBytes = data->TotalIdxCount * sizeof(uint);
        if (!TryFit(ref _vertices, vertexBytes, BufferUsages.Storage, "Debug UI vertices", graph))
            return false;

        if (!TryFit(ref _indices, indexBytes, BufferUsages.Index, "Debug UI indices", graph))
            return false;

        GpuUpload vertexUpload = _gpu.BeginUpload(vertexBytes);
        GpuUpload indexUpload = _gpu.BeginUpload(indexBytes);
        Span<ImDrawVert_t> vertices = MemoryMarshal.Cast<byte, ImDrawVert_t>(vertexUpload.Bytes);
        Span<uint> indices = MemoryMarshal.Cast<byte, uint>(indexUpload.Bytes);
        for (int listIndex = 0; listIndex < data->CmdLists.Size; listIndex++)
        {
            ImDrawList_t* list = data->CmdLists.Data[listIndex];
            new ReadOnlySpan<ImDrawVert_t>(list->VtxBuffer.Data, list->VtxBuffer.Size).CopyTo(vertices);
            vertices = vertices[list->VtxBuffer.Size..];
            for (int i = 0; i < list->IdxBuffer.Size; i++)
            {
                indices[i] = list->IdxBuffer.Data[i];
            }

            indices = indices[list->IdxBuffer.Size..];
        }

        graph.Use(_vertices, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(vertexUpload.Buffer, vertexUpload.Offset, _vertices, 0, (ulong)vertexBytes);
        graph.Use(_indices, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(indexUpload.Buffer, indexUpload.Offset, _indices, 0, (ulong)indexBytes);

        DebugUIConstants constants = new()
        {
            InverseSize = new Vector2(1f / target.Width, 1f / target.Height),
            Vertices = _vertices.Id,
            Sampler = _sampler.Id,
        };
        GpuUpload constantUpload = _gpu.BeginUpload(DebugUIConstants.Bytes);
        MemoryMarshal.Write(constantUpload.Bytes, in constants);
        graph.Use(_constants, GpuAccess.CopyDestination);
        graph.Recorder.CopyBuffer(constantUpload.Buffer, constantUpload.Offset, _constants, 0, DebugUIConstants.Bytes);

        return true;
    }

    /// <summary>
    /// Whether <paramref name="buffer"/> holds <paramref name="bytes"/>, made again at the power of two that does when it
    /// doesn't, so it seldom has to grow.
    /// </summary>
    private bool TryFit(ref GpuBuffer buffer, int bytes, BufferUsages usage, string name, RenderGraph graph)
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

        if (!buffer.IsNone)
        {
            graph.Forget(buffer);
            _gpu.Destroy(buffer);
        }

        buffer = made.Value;

        return true;
    }

    /// <summary>
    /// Each draw clipped to its rectangle, its vertices counted from its first and its texture read: a pass that draws
    /// over what the target holds.
    /// </summary>
    private unsafe void RecordDraws(ImDrawData_t* data, RenderGraph graph, GpuTexture target)
    {
        graph.BeginDrawPass(
            "Debug UI",
            new GpuRenderTargets { Colors = [new GpuColorTarget { Texture = target, Load = TargetLoad.Keep }] }
        );
        graph.Use(_constants, GpuAccess.ShaderRead);
        graph.Use(_vertices, GpuAccess.ShaderRead);
        graph.Use(_indices, GpuAccess.Index);
        foreach (GpuTexture texture in _textures)
        {
            graph.Use(texture, GpuAccess.ShaderRead);
        }

        graph.Recorder.BindPipeline(_pipeline);
        graph.Recorder.BindIndexBuffer(_indices);
        TextureDescription description = _gpu.GetDescription(target);
        int vertexBase = 0;
        int indexBase = 0;
        for (int listIndex = 0; listIndex < data->CmdLists.Size; listIndex++)
        {
            ImDrawList_t* list = data->CmdLists.Data[listIndex];
            for (int commandIndex = 0; commandIndex < list->CmdBuffer.Size; commandIndex++)
            {
                RecordDraw(graph, list->CmdBuffer.Data[commandIndex], description, vertexBase, indexBase);
            }

            vertexBase += list->VtxBuffer.Size;
            indexBase += list->IdxBuffer.Size;
        }

        graph.EndPass();
    }

    private unsafe void RecordDraw(
        RenderGraph graph,
        in ImDrawCmd_t command,
        in TextureDescription target,
        int vertexBase,
        int indexBase
    )
    {
        if (command.UserCallback != null || command.ElemCount == 0)
            return;

        // Clipped to the target, in whole pixels rounded out.
        int left = Math.Max((int)MathF.Floor(command.ClipRect.x), 0);
        int top = Math.Max((int)MathF.Floor(command.ClipRect.y), 0);
        int right = Math.Min((int)MathF.Ceiling(command.ClipRect.z), target.Width);
        int bottom = Math.Min((int)MathF.Ceiling(command.ClipRect.w), target.Height);
        if (right <= left || bottom <= top)
            return;

        // The texture by the id ImGui's own texture was given, or the id the draw names alone; one not made yet is left out.
        ulong texture = command.TexRef._TexData != null ? command.TexRef._TexData->TexID : command.TexRef._TexID;
        if (texture == 0)
            return;

        graph.Recorder.SetScissor((uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
        graph.Recorder.PushConstants(
            new GpuConstants(_constants.Id, (uint)vertexBase + command.VtxOffset, (uint)texture)
        );
        graph.Recorder.DrawIndexed(command.ElemCount, firstIndex: (uint)indexBase + command.IdxOffset);
    }

    /// <summary>
    /// The two stages as compiled, or why each did not, boxed so it is written whole from the compile's worker.
    /// </summary>
    private sealed record CompiledStages(Result<CompiledShader> Vertex, Result<CompiledShader> Pixel);
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
