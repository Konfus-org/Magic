using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ImGuiDebugUI.Resources.Scripts;

/// <summary>
/// The overlay script that draws the debug UI: ImGui's frame, a frame behind, from the <see cref="DrawLists"/> the main
/// thread copied, over the window once every view in it is shown. It takes in ImGui's textures as they are, uploads
/// the vertices and indices, and records the draws into the overlay's render pass, encoded as the target expects, its
/// shaders reading the vertices, the draw's texture and its constants through the heap. It owns every GPU object it
/// draws with: its textures, buffers, sampler and a pipeline per target format, made the first time one is drawn into,
/// and lets them go as it is disposed. Render thread.
/// </summary>
internal sealed class DebugUi : IOverlayScript, IDisposable
{
    private const string OverlayShader = "Shaders/Overlay/Overlay.slang";

    private readonly IGpuDriver _gpu;
    private readonly DrawLists _lists;
    private readonly Dictionary<uint, (GpuTexture Texture, int Width, int Height, uint Version)> _textures = [];
    private readonly List<uint> _gone = [];
    private readonly Dictionary<GpuFormat, GpuPipeline> _pipelines = [];
    private GpuSampler _sampler;
    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;
    private uint _vertexBytes;
    private uint _indexBytes;

    /// <summary>
    /// Both stages, written whole once compiled, which the render thread reads.
    /// </summary>
    private volatile Compiled? _shaders;

    /// <summary>
    /// Made by the renderer with the debug UI's draw lists, which the debug UI this gem provides copies each frame;
    /// another gem's debug UI has none, and the script is not made.
    /// </summary>
    public DebugUi(IDebugUI ui, IGpuDriver gpu, IShaderCompiler compiler, Assets assets, App app)
    {
        _gpu = gpu;
        _lists = ui is ImGuiDebugUIGem imgui
            ? imgui.DrawLists
            : throw new InvalidOperationException("The debug UI is not ImGui's, so it has no draw lists to draw.");
        CompileAsync(compiler, assets.Find<Shader>(OverlayShader), app.Quitting).FireAndForget();
    }

    public void Dispose()
    {
        foreach ((GpuTexture texture, _, _, _) in _textures.Values)
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
    /// Takes in ImGui's textures as they are, then draws them over the target; nothing while there is nothing to draw
    /// or the pipeline is not made. The sampler is made here, on the render thread, the first time.
    /// </summary>
    public void Record(in OverlayContext context)
    {
        if (_sampler == GpuSampler.None)
            _sampler = _gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));

        Follow();

        GpuPipeline pipeline = GetPipeline(context.Format);

        if (_lists.Calls.Length == 0 || context.Area.IsEmpty || pipeline == GpuPipeline.None) return;

        Upload(
            ref _vertexBuffer,
            ref _vertexBytes,
            GpuBufferUsage.GraphicsRead,
            MemoryMarshal.AsBytes(_lists.Vertices));
        Upload(ref _indexBuffer, ref _indexBytes, GpuBufferUsage.Index, MemoryMarshal.AsBytes(_lists.Indices));

        // ImGui's vertices are in window pixels: the vertex stage is given one over the area's size to place them, and
        // the fragment stage the target's encoding.
        OverlayConstants constants = new(
            new Vector2(1f / context.Area.Width, 1f / context.Area.Height),
            _vertexBuffer,
            0,
            GpuTexture.None,
            _sampler,
            (uint)context.Encoding);
        using GpuRenderPass pass = context.Begin()
            .Pipeline(pipeline)
            .Reads([_vertexBuffer])
            .IndexBuffer(_indexBuffer, GpuIndexSize.Bits16);

        // Each draw clipped to its rectangle, its vertices counted from its first and its texture read; one whose
        // texture is not taken in is left out.
        foreach (DrawCall call in _lists.Calls)
        {
            if (!_textures.TryGetValue(call.Texture, out (GpuTexture Texture, int, int, uint) texture)) continue;

            pass.Reads([texture.Texture])
                .Scissor(call.Clip)
                .Constants(constants with { FirstVertex = (uint)call.VertexOffset, Texture = texture.Texture })
                .DrawIndexed(call.Indices, call.FirstIndex);
        }
    }

    /// <summary>
    /// Takes in ImGui's textures as they are: each whose pixels' version this copy has not, into its GPU texture, made
    /// again when its size changed; and each gone released.
    /// </summary>
    private void Follow()
    {
        foreach ((uint id, TextureChange texture) in _lists.Known)
        {
            _textures.TryGetValue(id, out (GpuTexture Texture, int Width, int Height, uint Version) known);

            if (known.Version == texture.Version) continue;

            if (known.Texture == GpuTexture.None || known.Width != texture.Width || known.Height != texture.Height)
            {
                _gpu.Release(known.Texture);
                known.Texture = _gpu.CreateTexture(new GpuTextureDescription(
                    GpuFormat.Rgba8Unorm,
                    GpuTextureUsage.Sampled,
                    (uint)texture.Width,
                    (uint)texture.Height));
            }

            _gpu.Upload(new GpuTextureRegion(known.Texture), texture.Pixels);
            _textures[id] = (known.Texture, texture.Width, texture.Height, texture.Version);
        }

        _gone.Clear();
        foreach ((uint id, (GpuTexture Texture, int Width, int Height, uint Version) texture) in _textures)
        {
            if (!_lists.Known.ContainsKey(id))
            {
                _gpu.Release(texture.Texture);
                _gone.Add(id);
            }
        }

        foreach (uint id in _gone)
        {
            _textures.Remove(id);
        }
    }

    /// <summary>
    /// The bytes into the buffer, made again bigger first when they do not fit.
    /// </summary>
    private void Upload(ref GpuBuffer buffer, ref uint size, GpuBufferUsage usage, ReadOnlySpan<byte> bytes)
    {
        // Made again at the power of two that holds the bytes, and at least 16 KiB, so it seldom has to grow.
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
    private GpuPipeline GetPipeline(GpuFormat format)
    {
        if (_pipelines.TryGetValue(format, out GpuPipeline pipeline) || format == GpuFormat.Invalid
            || _shaders is not { } shaders)
        {
            return pipeline;
        }

        Result<GpuShader> vertex = shaders.Vertex;
        Result<GpuShader> fragment = shaders.Fragment;

        Result<GpuPipeline> made = vertex.Ok && fragment.Ok
            ? _gpu.CreatePipeline(new GpuPipelineDescription
            {
                Vertex = vertex.Value,
                Fragment = fragment.Value,
                ColorFormats = [format],
                Blends = [GpuBlend.Alpha],
                Cull = GpuCull.None,
            })
            : Result.Failure<GpuPipeline>(vertex.Ok ? fragment.Error : vertex.Error);
        if (!made.Ok) Debugging.Error($"The debug UI is not drawn: {made.Error}");

        return _pipelines[format] = made.Ok ? made.Value : GpuPipeline.None;
    }

    private async Task CompileAsync(IShaderCompiler compiler, Handle<Shader> shader, CancellationToken stopping)
    {
        Result<GpuShader> vertex = await compiler.CompileAsync(
            new ShaderEntry(shader, "DrawOverlay"),
            _gpu.ShaderFormat,
            stopping).ConfigureAwait(false);
        Result<GpuShader> fragment = await compiler.CompileAsync(
            new ShaderEntry(shader, "ShadeOverlay"),
            _gpu.ShaderFormat,
            stopping).ConfigureAwait(false);
        _shaders = new Compiled(vertex, fragment);
    }

    /// <summary>
    /// The constants both stages read, laid out as Overlay.slang's <c>OverlayConstants</c>: one over the target's size,
    /// the vertices, the draw's first vertex and texture, the sampler, and the target's <see cref="TargetEncoding"/>,
    /// which the fragment stage encodes ImGui's sRGB colours for.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct OverlayConstants(
        Vector2 InvTargetSize,
        GpuBuffer Vertices,
        uint FirstVertex,
        GpuTexture Texture,
        GpuSampler Sampler,
        uint Encoding);

    /// <summary>
    /// The two stages as compiled, or why each did not.
    /// </summary>
    private sealed record Compiled(Result<GpuShader> Vertex, Result<GpuShader> Fragment);
}
