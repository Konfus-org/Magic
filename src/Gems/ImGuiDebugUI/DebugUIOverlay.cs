using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Magic.Assets;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.ImGuiDebugUI;

/// <summary>
/// Draws the newest ImGui frame that the UI published over the window's image, in the overlay layer. Render thread.
/// </summary>
/// <remarks>
/// It draws the same frame again until a newer one comes. It asks the graph for ImGui's textures every frame while the
/// UI holds them.
/// </remarks>
internal sealed class DebugUIOverlay
{
    private const int MinBufferBytes = 16 * 1024;

    private readonly Mailbox<DebugUIFrame> _frames;
    private readonly ChannelReader<DebugUITextureChange> _textureChanges;
    private readonly DrawShaders _shaders;

    /// <summary>
    /// By the id the UI gave it, each of ImGui's textures the UI holds: what it is made to and the texture made.
    /// </summary>
    private readonly Dictionary<ulong, (TextureDescription Description, GpuTexture Made)> _textures = [];

    public DebugUIOverlay(
        AssetManager assets,
        Mailbox<DebugUIFrame> frames,
        ChannelReader<DebugUITextureChange> textureChanges
    )
    {
        _frames = frames;
        _textureChanges = textureChanges;
        _shaders = new DrawShaders(
            PassShaders.GetEntry(assets, "DrawDebugUI", "DrawDebugUIVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "DrawDebugUI", "DrawDebugUIPixel", ShaderStage.Pixel)
        )
        {
            Blend = BlendMode.Alpha,
            Cull = CullMode.None,
        };
    }

    /// <summary>
    /// Takes in the texture changes up to the newest frame that the UI published, then draws that frame over the
    /// window's image.
    /// </summary>
    /// <remarks>
    /// Draws nothing while there is no window or nothing to draw.
    /// </remarks>
    public void Record(RenderGraph graph)
    {
        if (_frames.TakeNewest() is not { } frame)
            return;

        // The texture changes are taken and the textures asked for whether or not there is a window to draw over, so
        // none wait for good and none are let go of.
        TakeTextureChanges(frame.Index, graph);
        AskTextures(graph);
        GpuTexture target = graph.WindowTarget;
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
        Result<GpuBuffer> constants = graph.Buffer(new BufferDescription("Debug UI constants", DebugUIConstants.Bytes));
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        Result made = Result.FindFailure(vertices, indices, constants, sampler);
        if (!made.Ok)
        {
            Log.Error($"The debug UI is not drawn: {made.Error}");

            return;
        }

        TextureDescription description = graph.GetDescription(target);
        // A frame whose rows were not staged draws no UI; the next frame sends its own.
        bool isSent =
            graph.Upload(vertices.Value, vertexRows)
            && graph.Upload(indices.Value, indexRows)
            && graph.Upload(
                constants.Value,
                [
                    new DebugUIConstants
                    {
                        InverseSize = new Vector2(1f / description.Width, 1f / description.Height),
                        Vertices = vertices.Value.Id,
                        Sampler = sampler.Value.Id,
                    },
                ]
            );
        if (!isSent)
            return;

        RecordDraws(frame, graph, target, description, vertices.Value, indices.Value, constants.Value);
    }

    /// <summary>
    /// A buffer of the power of two that holds <paramref name="bytes"/>, so it is seldom made again as the UI changes.
    /// </summary>
    private static BufferDescription GetDescription(string name, int bytes, BufferUsages usage) =>
        new(name, BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes, MinBufferBytes)), usage);

    /// <summary>
    /// Asks for every texture that the UI holds, so the GPU provider keeps it and hands back the same texture.
    /// </summary>
    private void AskTextures(RenderGraph graph)
    {
        foreach ((TextureDescription description, GpuTexture _) in _textures.Values)
        {
            _ = graph.Texture(description);
        }
    }

    /// <summary>
    /// Takes in the texture changes queued up to <paramref name="frame"/>: a texture made, filled again or destroyed.
    /// </summary>
    /// <remarks>
    /// Later changes wait, because a texture let go of in a later frame can still draw in this one.
    /// </remarks>
    private void TakeTextureChanges(uint frame, RenderGraph graph)
    {
        while (_textureChanges.TryPeek(out DebugUITextureChange change) && change.Frame <= frame)
        {
            // A fill that was not staged waits, with the changes after it, for the next frame.
            if (change.Pixels is { } pixels && !FillTexture(change, pixels, graph))
                return;

            if (change.Pixels is null)
                _textures.Remove(change.Texture);

            _textureChanges.TryRead(out _);
        }
    }

    /// <summary>
    /// Asks for the texture at the change's size, under a name of its own, and fills it with <paramref name="pixels"/>.
    /// </summary>
    /// <returns>False while the ring has no room for the pixels.</returns>
    private bool FillTexture(in DebugUITextureChange change, byte[] pixels, RenderGraph graph)
    {
        TextureDescription description = new(
            $"Debug UI texture {change.Texture}",
            change.Width,
            change.Height,
            GpuFormat.R8G8B8A8Unorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination
        );
        Result<GpuTexture> made = graph.Texture(description);
        if (!made.Ok)
        {
            Log.Error($"A debug UI texture was not made: {made.Error}");

            return true;
        }

        _textures[change.Texture] = (description, made.Value);

        return graph.Upload(made.Value, pixels);
    }

    /// <summary>
    /// Records one pass that draws each draw over the target, clipped to its rectangle.
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
                RenderStage.Overlay
            )
            .Reads(constants, vertices);
        foreach ((TextureDescription _, GpuTexture texture) in _textures.Values)
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
    /// Gets the draw clipped to the target in whole pixels, rounded out.
    /// </summary>
    /// <returns>False when nothing of the draw is left or its texture is not made yet.</returns>
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
        if (
            right <= left
            || bottom <= top
            || !_textures.TryGetValue(draw.Texture, out (TextureDescription, GpuTexture Made) texture)
        )
            return false;

        indexed = new IndexedDraw
        {
            Scissor = Rectangle.FromLTRB(left, top, right, bottom),
            Constants = new GpuConstants(constants.Id, draw.FirstVertex, texture.Made.Id),
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
