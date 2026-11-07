using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// ImGui's frame drawn over the window's image from the newest copy the UI published, again until a newer comes, in the
/// overlay layer so it shows over the picture whichever gem drew it: it takes in the texture changes queued up to that
/// frame, uploads the vertices, indices and constants into buffers named on the graph, and draws each of ImGui's draws
/// clipped to its rectangle in one draw pass, its shaders reading the vertices, the draw's texture and the constants
/// through the heap. It owns ImGui's textures and its sampler. Render thread.
/// </summary>
internal sealed class DebugUIOverlay : IDisposable
{
    private const string ShaderPath = "Shaders/DrawDebugUI.slang";
    private const int MinBufferBytes = 16 * 1024;

    private readonly IGraphicsApi _gpu;
    private readonly Mailbox<DebugUIFrame> _frames;
    private readonly ChannelReader<DebugUITextureChange> _textureChanges;
    private readonly DrawShaders _shaders;

    /// <summary>
    /// By the id the UI gave it, each of ImGui's textures made.
    /// </summary>
    private readonly Dictionary<ulong, GpuTexture> _textures = [];
    private GpuSampler _sampler;

    /// <summary>
    /// The newest copy taken, this overlay's until a newer is.
    /// </summary>
    private DebugUIFrame? _frame;

    public DebugUIOverlay(
        IGraphicsApi gpu,
        AssetManager assets,
        Mailbox<DebugUIFrame> frames,
        ChannelReader<DebugUITextureChange> textureChanges
    )
    {
        _gpu = gpu;
        _frames = frames;
        _textureChanges = textureChanges;
        Handle<Shader> shader = assets.Find<Shader>(ShaderPath);
        _shaders = new DrawShaders(
            new ShaderEntry
            {
                Module = shader,
                Name = "DrawDebugUIVertex",
                Stage = ShaderStage.Vertex,
            },
            new ShaderEntry
            {
                Module = shader,
                Name = "DrawDebugUIPixel",
                Stage = ShaderStage.Pixel,
            }
        )
        {
            Blend = BlendMode.Alpha,
            Cull = CullMode.None,
        };
    }

    public void Dispose()
    {
        foreach (GpuTexture texture in _textures.Values)
        {
            _gpu.Destroy(texture);
        }

        _gpu.Destroy(_sampler);
    }

    /// <summary>
    /// The newest frame the UI published over the window's image, the texture changes up to it taken in first, and
    /// nothing drawn while there is no window or nothing to draw.
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
        if (target.IsNone || frame.Indices.Count == 0)
            return;

        ReadOnlySpan<ImDrawVert_t> vertexRows = CollectionsMarshal.AsSpan(frame.Vertices);
        ReadOnlySpan<uint> indexRows = CollectionsMarshal.AsSpan(frame.Indices);
        Result<GpuBuffer> vertices = graph.Buffer(
            GetDescription("Debug UI vertices", MemoryMarshal.AsBytes(vertexRows).Length, BufferUsages.Storage)
        );
        Result<GpuBuffer> indices = graph.Buffer(
            GetDescription("Debug UI indices", indexRows.Length * sizeof(uint), BufferUsages.Index)
        );
        Result<GpuBuffer> constants = graph.Buffer(
            new BufferDescription
            {
                Bytes = DebugUIConstants.Bytes,
                Usage = BufferUsages.Storage,
                Name = "Debug UI constants",
            }
        );
        Result made =
            !vertices.Ok ? vertices
            : !indices.Ok ? indices
            : !constants.Ok ? constants
            : CreateSampler();
        if (!made.Ok)
        {
            Log.Error($"The debug UI is not drawn: {made.Error}");

            return;
        }

        TextureDescription description = _gpu.GetDescription(target);
        graph.BeginTransferPass("Debug UI: upload", RenderLayer.Overlay);
        graph.Upload(vertices.Value, vertexRows);
        graph.Upload(indices.Value, indexRows);
        graph.Upload(
            constants.Value,
            [
                new DebugUIConstants
                {
                    InverseSize = new Vector2(1f / description.Width, 1f / description.Height),
                    Vertices = vertices.Value.Id,
                    Sampler = _sampler.Id,
                },
            ]
        );
        graph.EndPass();
        RecordDraws(frame, graph, target, description, vertices.Value, indices.Value, constants.Value);
    }

    /// <summary>
    /// A buffer of the power of two that holds <paramref name="bytes"/>, so it is seldom made again as the UI changes.
    /// </summary>
    private static BufferDescription GetDescription(string name, int bytes, BufferUsages usage) =>
        new()
        {
            Bytes = BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes, MinBufferBytes)),
            Usage = usage,
            Name = name,
        };

    /// <summary>
    /// The sampler made, unless it is.
    /// </summary>
    private Result CreateSampler()
    {
        if (!_sampler.IsNone)
            return Result.Success();

        Result<GpuSampler> sampler = _gpu.CreateSampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (sampler.Ok)
            _sampler = sampler.Value;

        return sampler;
    }

    /// <summary>
    /// The texture changes queued up to <paramref name="frame"/>: a texture made or filled again, or destroyed. One let
    /// go of in a later frame may still be drawn in this one, so later changes wait.
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

        // A pass each, as a pass's barriers come before all of it and a texture filled twice in a frame needs one between.
        graph.BeginTransferPass("Debug UI: texture", RenderLayer.Overlay);
        graph.Upload(texture, pixels);
        graph.EndPass();
    }

    private void DestroyTexture(ulong id)
    {
        if (!_textures.Remove(id, out GpuTexture gone))
            return;

        _gpu.Destroy(gone);
    }

    /// <summary>
    /// Each draw clipped to its rectangle, its vertices counted from its first and its texture read: a pass that draws
    /// over what the target holds.
    /// </summary>
    private void RecordDraws(
        DebugUIFrame frame,
        RenderGraph graph,
        GpuTexture target,
        in TextureDescription description,
        GpuBuffer vertices,
        GpuBuffer indices,
        GpuBuffer constants
    )
    {
        DrawPass pass = graph
            .Draw(
                "Debug UI",
                _shaders,
                new GpuRenderTargets { Colors = [new GpuTarget { Texture = target }] },
                new GpuConstants(constants.Id),
                RenderLayer.Overlay
            )
            .Reads(constants, vertices);
        foreach (GpuTexture texture in _textures.Values)
        {
            pass.Reads(texture);
        }

        IndexedDraw[] draws = ArrayPool<IndexedDraw>.Shared.Rent(frame.Draws.Count);
        int count = 0;
        foreach (DebugUIDraw draw in frame.Draws)
        {
            if (TryGetDraw(draw, constants, description, out IndexedDraw indexed))
                draws[count++] = indexed;
        }

        pass.DrawIndexed(indices, draws.AsSpan(0, count));
        ArrayPool<IndexedDraw>.Shared.Return(draws);
    }

    /// <summary>
    /// The draw clipped to the target in whole pixels rounded out; none when nothing of it is left or its texture is not
    /// made yet.
    /// </summary>
    private bool TryGetDraw(
        in DebugUIDraw draw,
        GpuBuffer constants,
        in TextureDescription target,
        out IndexedDraw indexed
    )
    {
        indexed = default;
        int left = Math.Max((int)MathF.Floor(draw.Clip.X), 0);
        int top = Math.Max((int)MathF.Floor(draw.Clip.Y), 0);
        int right = Math.Min((int)MathF.Ceiling(draw.Clip.Z), target.Width);
        int bottom = Math.Min((int)MathF.Ceiling(draw.Clip.W), target.Height);
        if (right <= left || bottom <= top || !_textures.TryGetValue(draw.Texture, out GpuTexture texture))
            return false;

        indexed = new IndexedDraw
        {
            Scissor = Rectangle.FromLTRB(left, top, right, bottom),
            Constants = new GpuConstants(constants.Id, draw.FirstVertex, texture.Id),
            IndexCount = draw.IndexCount,
            FirstIndex = draw.FirstIndex,
        };

        return true;
    }
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
