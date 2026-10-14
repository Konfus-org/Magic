using System.Drawing;
using System.Numerics;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Records a stage's passes, the one way every pass runs: what it makes made (or kept) and seeded, then for each of its
/// iterations its constants pushed, every name it binds bound in the order its shader declares them, and its dispatch or
/// draws, under a debug group of its name. A draw of pieces (the visibility buffer, transparent surfaces, depth) steps
/// through the pieces a pass before it made with the static run of indices <paramref name="pieceIndices"/> holds, its
/// vertex stage reading the scene's tables; a draw of the visibility buffer is one indirect draw per raster bin, and one
/// of surfaces one per resolve bin, over the tiles a pass before it classified. Holds nothing of a frame between calls.
/// Render thread.
/// </summary>
internal sealed class PassRecorder(IGpu gpu, PassGraph graph, Scene scene, ClusterPool models, Materials materials, TexturePools textures, GpuBuffer pieceIndices) : IDisposable
{
    private const int MaxBindings = 24;

    /// <summary>
    /// Groups a dispatch runs along x at most, as every API allows; more go on in y, which a shader that may run more
    /// reads back as one number (Shaders/Include/Culling.hlsli's LinearGroup).
    /// </summary>
    private const uint MaxGroupsX = 65535;

    /// <summary>
    /// Indices a piece is drawn with, up to 32 triangles; a tile's quad, or a card, takes the first six.
    /// </summary>
    public const uint PieceIndexCount = 96;

    private readonly GpuSampler _nearest = gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Nearest, GpuAddress.Clamp));
    private readonly GpuSampler _linear = gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddress.Clamp));
    private readonly GpuSampler _comparison = gpu.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddress.Clamp, Compare: GpuCompare.GreaterOrEqual));

    public void Dispose()
    {
        gpu.Release(_nearest);
        gpu.Release(_linear);
        gpu.Release(_comparison);
    }

    /// <summary>
    /// The passes of <paramref name="stage"/> that are ready, in order, in <paramref name="scope"/>: the frame's
    /// resources, and a view's for a view's stage.
    /// </summary>
    public void Record(Listing listing, PassStage stage, in RecordScope scope)
    {
        foreach (ListedPass pass in listing.Stages[(int)stage])
        {
            if (!pass.IsReady)
            {
                continue;
            }

            gpu.Commands.PushDebugGroup(pass.Name);
            Make(listing, pass, scope);
            uint iterations = pass.Pass.Each.Length == 0 ? 1 : scope.Counts.Of(pass.Pass.Each);
            for (uint iteration = 0; iteration < iterations; iteration++)
            {
                FrameConstants constants = scope.Constants;
                constants.Pass = pass.Params;
                constants.Pass[28] = iteration;
                bool isRecorded = pass.Pass.Kind switch
                {
                    PassKind.Compute => Compute(pass, scope, constants, iteration),
                    PassKind.Fullscreen => Fullscreen(pass, scope, constants, iteration),
                    PassKind.Draw => Draw(listing, pass, scope, constants, iteration),
                    _ => false,
                };
                if (!isRecorded)
                {
                    break;
                }
            }

            Mipmaps(pass, scope);
            gpu.Commands.PopDebugGroup();
        }
    }

    /// <summary>
    /// Makes what the pass makes, unless it was made with the same key, and copies each seed into each copy. A texture
    /// kept from frame to frame starts with its history empty.
    /// </summary>
    private void Make(Listing listing, ListedPass pass, in RecordScope scope)
    {
        foreach (PassCreate create in pass.Pass.Creates)
        {
            CreatedResource created = listing.Created[create.Name];
            ResourceSet set = created.Info.IsFrame ? scope.Frame : scope.View ?? scope.Frame;
            bool isMade = set.Make(create.Name, KeyOf(created, scope));
            if (isMade && create.History && set.TryGet(ResourceInfo.HistoryOf(create.Name), out BoundResource history) && history.Texture != GpuTexture.None)
            {
                gpu.Commands.BeginRenderPass([new GpuBinding(Texture: history.Texture)], GpuLoad.Clear);
                gpu.Commands.EndRenderPass();
            }

            if (create.Seed.Length == 0 || !set.TryGet(create.Name, out BoundResource made) || !Find(scope, create.Seed, out BoundResource seed))
            {
                continue;
            }

            uint slice = SliceOf(create, scope.Counts);
            uint copies = create.Copies.Length == 0 ? 1 : Math.Max(1, scope.Counts.Of(create.Copies));
            for (uint copy = 0; copy < copies; copy++)
            {
                gpu.Commands.CopyBuffer(seed.Buffer, 0, made.Buffer, copy * slice, Math.Min(seed.Bytes, slice));
            }
        }
    }

    private bool Compute(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        Span<GpuBinding> writes = stackalloc GpuBinding[MaxBindings];
        int writeCount = 0;
        foreach (PassBinding write in pass.TextureWrites)
        {
            if (!Find(scope, write.Info.Name, out BoundResource bound))
            {
                return false;
            }

            writes[writeCount++] = new GpuBinding(Texture: bound.Texture, Level: write.Level, Layer: LayerOf(pass, write, iteration));
        }

        foreach (PassBinding write in pass.BufferWrites)
        {
            if (!Find(scope, write.Info.Name, out BoundResource bound))
            {
                return false;
            }

            writes[writeCount++] = new GpuBinding(bound.Buffer);
        }

        Span<GpuBinding> sampled = stackalloc GpuBinding[MaxBindings];
        Span<GpuTexture> storage = stackalloc GpuTexture[MaxBindings];
        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        if (!Reads(pass, scope, sampled, storage, buffers))
        {
            return false;
        }

        BoundResource args = default;
        bool isIndirect = pass.Pass.Dispatch.Args.Length > 0;
        if (isIndirect && !Find(scope, pass.Pass.Dispatch.Args, out args))
        {
            return false;
        }

        GpuCommands commands = gpu.Commands;
        commands.Push(GpuStage.Compute, constants);
        commands.BeginComputePass(writes[..writeCount]);
        commands.BindPipeline(pass.Pipeline);
        Bind(GpuStage.Compute, sampled[..pass.Sampled.Count], storage[..pass.StorageReads.Count], buffers[..pass.Buffers.Count]);
        if (isIndirect)
        {
            commands.DispatchIndirect(args.Buffer, (pass.Pass.Dispatch.First + iteration) * 3 * sizeof(uint));
        }
        else
        {
            (uint x, uint y, uint z) = Groups(pass, scope);
            commands.Dispatch(x, y, z);
        }

        commands.EndComputePass();
        return true;
    }

    private bool Fullscreen(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassBinding write = pass.TextureWrites[0];
        Span<GpuBinding> sampled = stackalloc GpuBinding[MaxBindings];
        Span<GpuTexture> storage = stackalloc GpuTexture[MaxBindings];
        Span<GpuBuffer> buffers = stackalloc GpuBuffer[MaxBindings];
        if (!Find(scope, write.Info.Name, out BoundResource target) || !Reads(pass, scope, sampled, storage, buffers))
        {
            return false;
        }

        Rectangle area = new(0, 0, (int)Math.Max(1, target.Width >> (int)write.Level), (int)Math.Max(1, target.Height >> (int)write.Level));
        GpuCommands commands = gpu.Commands;
        commands.Push(GpuStage.Fragment, constants);
        commands.BeginRenderPass([new GpuBinding(Texture: target.Texture, Level: write.Level, Layer: LayerOf(pass, write, iteration))], write.Load);
        commands.SetViewport(area);
        commands.SetScissor(area);
        commands.BindPipeline(pass.Pipeline);
        Bind(GpuStage.Fragment, sampled[..pass.Sampled.Count], storage[..pass.StorageReads.Count], buffers[..pass.Buffers.Count]);
        commands.Draw(3);
        commands.EndRenderPass();
        return true;
    }

    /// <summary>
    /// A draw pass: one render pass into its targets, at the layer of its iteration when it runs for each of a count, or,
    /// when it is tiled, over the whole target with each of the frame's tiles emptied first, and nothing at all when
    /// there are none; then what it draws: the visibility buffer's pieces, a bin at a time, the G-buffer's tiles, a
    /// resolve bin at a time, transparent pieces farthest first, depth for shadows (an iteration's arguments each), or
    /// the sky.
    /// </summary>
    private bool Draw(Listing listing, ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassDraw draw = pass.Pass.Draw;
        Span<GpuBinding> colors = stackalloc GpuBinding[8];
        Size size = Size.Empty;
        for (int i = 0; i < draw.Colors.Count; i++)
        {
            if (!Find(scope, draw.Colors[i], out BoundResource color))
            {
                return false;
            }

            colors[i] = new GpuBinding(Texture: color.Texture, Layer: pass.Pass.Each.Length > 0 && !draw.Tiled ? iteration : 0);
            size = new Size((int)color.Width, (int)color.Height);
        }

        BoundResource depth = default;
        if (draw.Depth.Length > 0 && !Find(scope, draw.Depth, out depth))
        {
            return false;
        }

        BoundResource args = default;
        BoundResource instances = default;
        if (draw.Drawn is PassDrawn.Visibility or PassDrawn.Surfaces or PassDrawn.Depth && (!Find(scope, draw.Args, out args) || !Find(scope, draw.Instances, out instances)))
        {
            return false;
        }

        size = size.IsEmpty ? new Size((int)depth.Width, (int)depth.Height) : size;
        Span<GpuBinding> sampled = stackalloc GpuBinding[TexturePools.Count + MaxBindings];
        Span<GpuTexture> storage = stackalloc GpuTexture[MaxBindings];
        Span<GpuBuffer> buffers = stackalloc GpuBuffer[PassGraph.PiecesBuffers + MaxBindings];
        if (!Reads(pass, scope, sampled[TexturePools.Count..], storage, buffers[PassGraph.PiecesBuffers..]))
        {
            return false;
        }

        if (draw.Tiled && scope.Tiles.Count == 0)
        {
            return true;
        }

        Rectangle area = new(Point.Empty, size);
        GpuCommands commands = gpu.Commands;
        commands.Push(GpuStage.Vertex, constants);
        commands.Push(GpuStage.Fragment, constants);
        commands.BeginRenderPass(colors[..draw.Colors.Count], draw.Load, default, depth.Texture, pass.Pass.Each.Length > 0 && !draw.Tiled ? iteration : 0);
        if (draw.Tiled)
        {
            commands.BindPipeline(pass.Clear);
            foreach (Rectangle tile in scope.Tiles)
            {
                commands.SetViewport(tile);
                commands.SetScissor(tile);
                commands.Draw(3);
            }
        }

        commands.SetViewport(area);
        commands.SetScissor(area);
        if (draw.Drawn == PassDrawn.Depth)
        {
            BindPieces(instances.Buffer, buffers, pass.Buffers.Count);
            commands.BindPipeline(pass.Pipeline);
            commands.DrawIndexedIndirect(args.Buffer, (uint)((draw.First + iteration) * GpuArray<GpuDrawArgs>.RowBytes), 1);
            commands.EndRenderPass();
            return true;
        }

        textures.Bind(sampled);
        buffers[0] = materials.Records.Buffer;
        Bind(GpuStage.Fragment, sampled[..(TexturePools.Count + pass.Sampled.Count)], storage[..pass.StorageReads.Count], buffers[..1]);
        if (pass.Buffers.Count > 0)
        {
            gpu.Commands.BindStorageBuffers(GpuStage.Fragment, 1, buffers[PassGraph.PiecesBuffers..(PassGraph.PiecesBuffers + pass.Buffers.Count)]);
        }

        switch (draw.Drawn)
        {
            case PassDrawn.Visibility:
                Visibility(listing, pass, scope, args, instances);
                break;
            case PassDrawn.Surfaces:
                Resolve(listing, pass, scope, args, instances);
                break;
            case PassDrawn.Transparent:
                Transparent(listing, pass, scope);
                break;
            case PassDrawn.Sky:
                Sky(listing, pass, scope);
                break;
            case PassDrawn.Depth:
            default:
                break;
        }

        commands.EndRenderPass();
        return true;
    }

    /// <summary>
    /// The visibility buffer: each raster bin with its pipeline, from the arguments the bins' pass counted into. The two
    /// fixed bins draw what does not clip, single and double sided; then each surface's bin of clusters and its bin of
    /// cards, which take six indices, not a piece's 96, both with its pipeline.
    /// </summary>
    private void Visibility(Listing listing, ListedPass pass, in RecordScope scope, BoundResource args, BoundResource instances)
    {
        BindPieces(instances.Buffer, [], 0);
        for (int bin = 0; bin < Materials.FixedRasterBins; bin++)
        {
            DrawBin(new SurfaceKey(Handle<Shader>.None, false, bin == 1), bin);
        }

        for (int surface = 0; surface < scope.RasterBins.Count; surface++)
        {
            DrawBin(scope.RasterBins[surface], Materials.FixedRasterBins + surface);
            DrawBin(scope.RasterBins[surface], Materials.CardBins + surface);
        }

        void DrawBin(SurfaceKey key, int bin)
        {
            GpuPipeline pipeline = graph.SurfacePipeline(listing, pass, key);
            if (pipeline == GpuPipeline.None)
            {
                return;
            }

            gpu.Commands.BindPipeline(pipeline);
            gpu.Commands.DrawIndexedIndirect(args.Buffer, (uint)((pass.Pass.Draw.First + bin) * GpuArray<GpuDrawArgs>.RowBytes), 1);
        }
    }

    /// <summary>
    /// The G-buffer: each resolve bin's tiles with its surface's pipeline, from the arguments the classifying pass counted.
    /// </summary>
    private void Resolve(Listing listing, ListedPass pass, in RecordScope scope, BoundResource args, BoundResource tiles)
    {
        gpu.Commands.BindVertexBuffers(0, [tiles.Buffer]);
        gpu.Commands.BindIndexBuffer(pieceIndices, wide: false);
        for (int bin = 0; bin < scope.ResolveBins.Count; bin++)
        {
            GpuPipeline pipeline = graph.SurfacePipeline(listing, pass, scope.ResolveBins[bin]);
            if (pipeline == GpuPipeline.None)
            {
                continue;
            }

            gpu.Commands.BindPipeline(pipeline);
            gpu.Commands.DrawIndexedIndirect(args.Buffer, (uint)(bin * GpuArray<GpuDrawArgs>.RowBytes), 1);
        }
    }

    /// <summary>
    /// The world's sky, its material's surface or the renderer's own, over what nothing was drawn on.
    /// </summary>
    private void Sky(Listing listing, ListedPass pass, in RecordScope scope)
    {
        GpuPipeline sky = graph.SurfacePipeline(listing, pass, scope.Sky);
        if (sky != GpuPipeline.None)
        {
            gpu.Commands.BindPipeline(sky);
            gpu.Commands.Draw(3);
        }
    }

    /// <summary>
    /// The view's transparent pieces, farthest instance first, a run of one surface at a time.
    /// </summary>
    private void Transparent(Listing listing, ListedPass pass, in RecordScope scope)
    {
        if (scope.TransparentRuns.Count == 0)
        {
            return;
        }

        BindPieces(scope.TransparentPieces, [], 0);
        GpuPipeline bound = GpuPipeline.None;
        foreach (TransparentRun run in scope.TransparentRuns)
        {
            GpuPipeline pipeline = graph.SurfacePipeline(listing, pass, run.Surface);
            if (pipeline == GpuPipeline.None)
            {
                continue;
            }

            if (pipeline != bound)
            {
                gpu.Commands.BindPipeline(pipeline);
                bound = pipeline;
            }

            gpu.Commands.DrawIndexed(PieceIndexCount, 0, 0, (uint)run.Count, (uint)run.First);
        }
    }

    /// <summary>
    /// Binds pieces to draw: their instance-rate buffer, the indices, and the scene's tables the pieces' vertex stage
    /// reads, then <paramref name="reads"/> of a pass's own after them.
    /// </summary>
    private void BindPieces(GpuBuffer pieces, Span<GpuBuffer> reads, int readCount)
    {
        GpuCommands commands = gpu.Commands;
        commands.BindVertexBuffers(0, [pieces]);
        commands.BindIndexBuffer(pieceIndices, wide: false);
        commands.BindStorageBuffers(GpuStage.Vertex, 0, [scene.Instances.Buffer, models.Models.Buffer, models.Clusters.Buffer, models.Cards.Buffer, models.Geometry]);
        if (readCount > 0)
        {
            commands.BindStorageBuffers(GpuStage.Vertex, PassGraph.PiecesBuffers, reads[PassGraph.PiecesBuffers..(PassGraph.PiecesBuffers + readCount)]);
        }
    }

    private void Bind(GpuStage stage, ReadOnlySpan<GpuBinding> sampled, ReadOnlySpan<GpuTexture> storage, ReadOnlySpan<GpuBuffer> buffers)
    {
        if (!sampled.IsEmpty)
        {
            gpu.Commands.BindTextures(stage, 0, sampled);
        }

        if (!storage.IsEmpty)
        {
            gpu.Commands.BindStorageTextures(stage, 0, storage);
        }

        if (!buffers.IsEmpty)
        {
            gpu.Commands.BindStorageBuffers(stage, 0, buffers);
        }
    }

    /// <summary>
    /// What the pass reads, sampled textures with the sampler their filter asks for, textures read with none, and buffers;
    /// false when one is not there yet.
    /// </summary>
    private bool Reads(ListedPass pass, in RecordScope scope, Span<GpuBinding> sampled, Span<GpuTexture> storage, Span<GpuBuffer> buffers)
    {
        for (int i = 0; i < pass.Sampled.Count; i++)
        {
            PassBinding read = pass.Sampled[i];
            if (!Find(scope, read.Info.Name, out BoundResource bound))
            {
                return false;
            }

            sampled[i] = new GpuBinding(Texture: bound.Texture, Sampler: SamplerOf(read));
        }

        for (int i = 0; i < pass.StorageReads.Count; i++)
        {
            if (!Find(scope, pass.StorageReads[i].Info.Name, out BoundResource bound))
            {
                return false;
            }

            storage[i] = bound.Texture;
        }

        for (int i = 0; i < pass.Buffers.Count; i++)
        {
            if (!Find(scope, pass.Buffers[i].Info.Name, out BoundResource bound))
            {
                return false;
            }

            buffers[i] = bound.Buffer;
        }

        return true;
    }

    private void Mipmaps(ListedPass pass, in RecordScope scope)
    {
        foreach (PassBinding write in pass.TextureWrites)
        {
            if (write.Mipmaps && Find(scope, write.Info.Name, out BoundResource bound))
            {
                gpu.Commands.GenerateMipmaps(bound.Texture);
            }
        }

        if (pass.Pass.Kind != PassKind.Draw || !pass.Pass.Draw.Mipmaps)
        {
            return;
        }

        foreach (string color in pass.Pass.Draw.Colors)
        {
            if (Find(scope, color, out BoundResource bound))
            {
                gpu.Commands.GenerateMipmaps(bound.Texture);
            }
        }
    }

    private GpuSampler SamplerOf(PassBinding read) => read.Filter switch
    {
        PassFilter.Nearest => _nearest,
        PassFilter.Linear => _linear,
        PassFilter.Comparison => _comparison,
        PassFilter.Default => read.Info.IsDepth || read.Info.Format is GpuFormat.R32Float or GpuFormat.R32Uint or GpuFormat.R16Float ? _nearest : _linear,
        _ => read.Info.IsDepth || read.Info.Format is GpuFormat.R32Float or GpuFormat.R32Uint or GpuFormat.R16Float ? _nearest : _linear,
    };

    /// <summary>
    /// How many groups a compute pass runs: enough threads for the view or a texture it writes, one per of a count (rows
    /// of <see cref="MaxGroupsX"/>), or fixed.
    /// </summary>
    private static (uint X, uint Y, uint Z) Groups(ListedPass pass, in RecordScope scope)
    {
        PassDispatch dispatch = pass.Pass.Dispatch;
        (uint threadsX, uint threadsY, uint threadsZ) = pass.Threads;
        if (dispatch.Groups.Count > 0)
        {
            return (dispatch.Groups[0], dispatch.Groups.Count > 1 ? dispatch.Groups[1] : 1, dispatch.Groups.Count > 2 ? dispatch.Groups[2] : 1);
        }

        bool isView = string.Equals(dispatch.Per, "view", StringComparison.Ordinal);
        if (isView || dispatch.Per.StartsWith("output:", StringComparison.Ordinal))
        {
            Vector2 view = scope.Constants.ViewSize;
            (uint width, uint height) = ((uint)view.X, (uint)view.Y);
            if (!isView && Find(scope, dispatch.Per["output:".Length..], out BoundResource output))
            {
                (width, height) = (output.Width, output.Height);
            }

            return dispatch.Tile > 0 ? (Ceiling(width, dispatch.Tile), Ceiling(height, dispatch.Tile), 1u) : (Ceiling(width, threadsX), Ceiling(height, threadsY), 1u);
        }

        uint groups = Ceiling(scope.Counts.Of(dispatch.Per), threadsX * threadsY * threadsZ);
        return (Math.Min(groups, MaxGroupsX), Ceiling(groups, MaxGroupsX), 1);
    }

    /// <summary>
    /// The layer a texture write goes to: its iteration's, in a texture array a pass writes for each of a count.
    /// </summary>
    private static uint LayerOf(ListedPass pass, PassBinding write, uint iteration)
    {
        return pass.Pass.Each.Length > 0 && write.Info.Shape is GpuTextureKind.Texture2DArray or GpuTextureKind.Cube ? iteration : write.Layer;
    }

    /// <summary>
    /// What a pass makes is made with, as its file and this frame's sizes and counts say: a texture follows the view's
    /// size as it is drawn, or as it is shown.
    /// </summary>
    private static ResourceKey KeyOf(CreatedResource created, in RecordScope scope)
    {
        PassCreate create = created.Create;
        if (create.Kind == PassResource.Buffer)
        {
            uint rows = create.Per.Length == 0 ? create.Rows : BitOperations.RoundUpToPowerOf2(Math.Max(1, scope.Counts.Of(create.Per)));
            uint copies = create.Copies.Length == 0 ? 1 : Math.Max(1, scope.Counts.Of(create.Copies));
            return new ResourceKey(true, GpuFormat.Invalid, GpuTextureKind.Texture2D, GpuTextureUsage.None, created.BufferUsage, 0, 0, 0, 0, Math.Max(rows * create.Stride, create.Stride) * copies);
        }

        Vector2 view = create.Size == PassSize.Output ? scope.Constants.OutputSize : scope.Constants.ViewSize;
        uint side = create.Per.Length > 0 ? Math.Max(1, scope.Counts.Of(create.Per)) : 0;
        uint width = side > 0 ? side : create.Scale > 0 ? (uint)Math.Max(1, MathF.Ceiling(view.X * create.Scale)) : create.Width;
        uint height = side > 0 ? side : create.Scale > 0 ? (uint)Math.Max(1, MathF.Ceiling(view.Y * create.Scale)) : create.Height;
        uint layers = create.Cube ? 6 : create.Depth > 0 ? create.Depth : create.Layers;
        uint levels = Math.Min(create.Levels, (uint)BitOperations.Log2(Math.Max(width, height)) + 1);

        // Sampled whether or not a pass reads it: the renderer shows the view's Ldr, which no pass does. A texture kept
        // from frame to frame is drawn into once as it is made, to empty its history.
        GpuTextureUsage usage = created.TextureUsage | (created.Info.IsStorageRead ? GpuTextureUsage.None : GpuTextureUsage.Sampler) | (created.Info.IsDepth ? GpuTextureUsage.DepthTarget : GpuTextureUsage.None)
            | (create.History ? GpuTextureUsage.ColorTarget : GpuTextureUsage.None);
        return new ResourceKey(false, create.Format, created.Info.Shape, usage, GpuBufferUsage.None, width, height, layers, levels, 0, create.History);
    }

    /// <summary>
    /// The bytes of one copy of a buffer a pass makes: as many rows as its count, not as many as it is made with, so an
    /// iteration's offset agrees with what the shader that fills it works out.
    /// </summary>
    private static uint SliceOf(PassCreate create, in Counts counts)
    {
        return (create.Per.Length == 0 ? create.Rows : Math.Max(1, counts.Of(create.Per))) * create.Stride;
    }

    private static bool Find(in RecordScope scope, string name, out BoundResource resource)
    {
        return (scope.View is { } view && view.TryGet(name, out resource)) || scope.Frame.TryGet(name, out resource);
    }

    private static uint Ceiling(uint count, uint per) => (Math.Max(1, count) + Math.Max(1, per) - 1) / Math.Max(1, per);
}

/// <summary>
/// What a stage records against: the frame's resources and a view's, the constants and counts of the frame or the view,
/// the surfaces of the raster and resolve bins, the view's transparent pieces and their runs, the sky's surface, and the
/// tile each run of a tiled draw draws into.
/// </summary>
internal readonly record struct RecordScope(
    ResourceSet Frame,
    ResourceSet? View,
    FrameConstants Constants,
    Counts Counts,
    IReadOnlyList<SurfaceKey> RasterBins,
    IReadOnlyList<SurfaceKey> ResolveBins,
    IReadOnlyList<TransparentRun> TransparentRuns,
    GpuBuffer TransparentPieces,
    SurfaceKey Sky,
    IReadOnlyList<Rectangle> Tiles);

/// <summary>
/// A run of a view's transparent pieces drawn with one surface's pipeline.
/// </summary>
internal readonly record struct TransparentRun(SurfaceKey Surface, int First, int Count);

/// <summary>
/// The counts a pass file can name, for how many times a pass runs, how many groups it dispatches and how big a buffer it
/// makes is: the scene's and the view's, the lights' (the world's light slots, and those a view holds), and the shadows'
/// (their tables' entries, the pages drawn a frame at most, their groups, and the pool's pages to take). A number names
/// itself.
/// </summary>
internal readonly record struct Counts(
    uint Instances,
    uint Pieces,
    uint Clusters,
    uint Jobs,
    uint RasterBins,
    uint ResolveBins,
    uint Tiles,
    uint HizTexels,
    uint HizLevels,
    uint Lights,
    uint ZBins,
    uint LightTiles,
    uint LightTileWords,
    uint Moved,
    uint GiRays = 0,
    uint GiProbeThreads = 0,
    uint SceneLights = 0,
    uint SunEntries = 0,
    uint LightEntries = 0,
    uint ShadowViews = 0,
    uint ShadowGroups = 0,
    uint ShadowCandidates = 0)
{
    public uint Of(string name) => name switch
    {
        "instances" => Instances,
        "pieces" => Pieces,
        "clusters" => Clusters,
        "jobs" => Jobs,
        "rasterBins" => RasterBins,
        "resolveBins" => ResolveBins,
        "tiles" => Tiles,
        "tileEntries" => Tiles * Math.Max(1, ResolveBins),
        "hizTexels" => HizTexels,
        "hizLevels" => HizLevels,
        "lights" => Lights,
        "zBins" => ZBins,
        "lightTiles" => LightTiles,
        "lightTileWords" => LightTileWords,
        "moved" => Moved,
        "giRays" => GiRays,
        "giProbeThreads" => GiProbeThreads,
        "sceneLights" => SceneLights,
        "sunEntries" => SunEntries,
        "lightEntries" => LightEntries,
        "shadowViews" => ShadowViews,
        "shadowGroups" => ShadowGroups,
        "shadowCandidates" => ShadowCandidates,
        _ => uint.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out uint number) ? number : 0,
    };
}
