using System.Drawing;
using System.Numerics;
using Magic.Contexts.Frames;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Records a stage's passes, the one way every pass runs: what it makes made (or kept) and seeded, then for each of its
/// iterations its constants pushed, every name each of its stages binds bound at its slot (<see cref="StageBindings"/>),
/// each looked up by name in the frame's or the view's resources, and its dispatch or draws, under a debug group of its
/// name. A draw of pieces (the visibility buffer, transparent surfaces, depth) steps through the pieces a pass before
/// it made with the static run of indices <paramref name="pieceIndices"/> holds; a draw of the visibility buffer is
/// one indirect draw per raster bin, and one of surfaces one per resolve bin, over the tiles a pass before it
/// classified. Holds nothing of a frame between calls. Render thread.
/// </summary>
internal sealed class PassRecorder(IGpu gpu, PassGraph graph, GpuBuffer pieceIndices) : IDisposable
{
    /// <summary>
    /// Groups a dispatch runs along x at most, as every API allows; more go on in y, which a shader that may run more
    /// reads back as one number (Shaders/Include/Culling.hlsli's LinearGroup).
    /// </summary>
    private const uint MaxGroupsX = 65535;

    /// <summary>
    /// Indices a piece is drawn with, up to 32 triangles; a tile's quad, or a card, takes the first six.
    /// </summary>
    public const uint PieceIndexCount = 96;

    private readonly GpuSampler _nearest = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Nearest, GpuWrap.Clamp));
    private readonly GpuSampler _linear = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp));
    private readonly GpuSampler _comparison = gpu.CreateSampler(new GpuSamplerDescription(GpuFilter.Linear, GpuWrap.Clamp, Compare: GpuCompare.GreaterOrEqual));

    public void Dispose()
    {
        gpu.Release(_nearest);
        gpu.Release(_linear);
        gpu.Release(_comparison);
    }

    /// <summary>
    /// The passes of <paramref name="stage"/> that are ready, in order, in <paramref name="scope"/>: the frame's
    /// resources, and a view's for a view's stage. One that never runs has what it would draw into shown as the
    /// failure instead (<see cref="Fill"/>).
    /// </summary>
    public void Record(Listing listing, PassStage stage, in RecordScope scope)
    {
        foreach (ListedPass pass in listing.Stages[(int)stage])
        {
            if (!pass.IsReady)
            {
                if (pass.HasFailed)
                {
                    Fill(listing, pass, scope);
                }

                continue;
            }

            using GpuDebugGroup group = gpu.Commands.DebugGroup(pass.Name);
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
                    PassKind.Script => Script(pass, scope),
                    _ => false,
                };
                if (!isRecorded)
                {
                    break;
                }
            }

            Mipmaps(pass, scope);
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
                gpu.Commands.RenderPass([new GpuTextureLevel(history.Texture)], GpuContents.Clear).Dispose();
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

    /// <summary>
    /// What a pass that never runs would draw into, made as it would have been and cleared to the failure's colour,
    /// breathing: a fullscreen pass's target, or a draw's colours, at every layer its iterations would draw, so what it
    /// would have drawn shows as broken and the passes after it read that. What a compute pass writes is left as it is
    /// made; the words say why (Include/FailureBanner.hlsli).
    /// </summary>
    private void Fill(Listing listing, ListedPass pass, in RecordScope scope)
    {
        Make(listing, pass, scope);
        if (pass.Pass.Kind is not (PassKind.Fullscreen or PassKind.Draw))
        {
            return;
        }

        using GpuDebugGroup group = gpu.Commands.DebugGroup(pass.Name);
        Magic.Contexts.Assets.Color color = Failures.PulseColor(scope.Constants.Time);
        uint iterations = pass.Pass.Each.Length == 0 ? 1 : scope.Counts.Of(pass.Pass.Each);
        foreach (PassBinding write in pass.Pass.Kind == PassKind.Fullscreen ? pass.TextureWrites : ColorsOf(pass))
        {
            if (!Find(scope, write.Info.Name, out BoundResource target))
            {
                continue;
            }

            for (uint iteration = 0; iteration < iterations; iteration++)
            {
                gpu.Commands.RenderPass([new GpuTextureLevel(target.Texture, write.Level, LayerOf(pass, write, iteration))], GpuContents.Clear, color).Dispose();
            }
        }
    }

    /// <summary>
    /// A draw's colours as bindings, at their first level and layer: each is a name the pass can see.
    /// </summary>
    private static List<PassBinding> ColorsOf(ListedPass pass)
    {
        List<PassBinding> colors = [];
        foreach (string name in pass.Pass.Draw.Colors)
        {
            if (pass.Visible.Find(seen => string.Equals(seen.Name, name, StringComparison.Ordinal)) is { } info)
            {
                colors.Add(new PassBinding(info, 0, 0, GpuContents.Clear, false));
            }
        }

        return colors;
    }

    private bool Compute(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        if (pass.Bound(GpuStage.Compute) is not { } bound)
        {
            return false;
        }

        Span<GpuTextureLevel> textureWrites = stackalloc GpuTextureLevel[StageBindings.MaxStorage];
        int textureCount = 0;
        foreach (BoundName write in bound.TextureWrites)
        {
            if (!Find(scope, write.Binding.Info.Name, out BoundResource written))
            {
                return false;
            }

            textureWrites[textureCount++] = new GpuTextureLevel(written.Texture, write.Binding.Level, LayerOf(pass, write.Binding, iteration));
        }

        Span<GpuBuffer> bufferWrites = stackalloc GpuBuffer[StageBindings.MaxStorage];
        int bufferCount = 0;
        foreach (BoundName write in bound.BufferWrites)
        {
            if (!Find(scope, write.Binding.Info.Name, out BoundResource written))
            {
                return false;
            }

            bufferWrites[bufferCount++] = written.Buffer;
        }

        Reads reads = default;
        if (!reads.Gather(bound, scope, this))
        {
            return false;
        }

        BoundResource args = default;
        bool isIndirect = pass.Pass.Dispatch.Args.Length > 0;
        if (isIndirect && !Find(scope, pass.Pass.Dispatch.Args, out args))
        {
            return false;
        }

        using GpuComputePass compute = gpu.Commands.ComputePass(textureWrites[..textureCount], bufferWrites[..bufferCount]);
        compute.Constants(constants).Pipeline(pass.Pipeline);
        reads.Bind(compute, bound);
        if (isIndirect)
        {
            compute.DispatchFromBuffer(args.Buffer, (pass.Pass.Dispatch.First + iteration) * 3 * sizeof(uint));
        }
        else
        {
            (uint x, uint y, uint z) = Groups(pass, scope);
            compute.Dispatch(x, y, z);
        }

        return true;
    }

    private bool Fullscreen(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassBinding write = pass.TextureWrites[0];
        Reads reads = default;
        if (pass.Bound(GpuStage.Fragment) is not { } bound || !Find(scope, write.Info.Name, out BoundResource target) || !reads.Gather(bound, scope, this))
        {
            return false;
        }

        Rectangle area = new(0, 0, (int)Math.Max(1, target.Width >> (int)write.Level), (int)Math.Max(1, target.Height >> (int)write.Level));
        using GpuRenderPass render = gpu.Commands.RenderPass([new GpuTextureLevel(target.Texture, write.Level, LayerOf(pass, write, iteration))], write.Contents);
        render.Viewport(area).Scissor(area).Pipeline(pass.Pipeline).Constants(GpuStage.Fragment, constants);
        reads.Bind(render, GpuStage.Fragment, bound);
        render.Draw(3);
        return true;
    }

    /// <summary>
    /// A script pass: its script records into the one texture it writes, the target itself in a target's stage, told
    /// the texture's format and encoding, which its pipelines are made for.
    /// </summary>
    private static bool Script(ListedPass pass, in RecordScope scope)
    {
        PassBinding write = pass.TextureWrites[0];
        if (pass.Script is not { } script || !Find(scope, write.Info.Name, out BoundResource target))
        {
            return false;
        }

        bool isTarget = write.Info.IsTarget;
        Rectangle area = new(0, 0, (int)Math.Max(1, target.Width), (int)Math.Max(1, target.Height));
        script.Record(new PipelineScriptContext(
            scope.Commands,
            target.Texture,
            area,
            isTarget ? scope.TargetFormat : write.Info.Format,
            isTarget ? scope.TargetEncoding : TargetEncoding.Linear,
            write.Contents,
            scope.Now));
        return true;
    }

    /// <summary>
    /// A draw pass: one render pass into its targets, at the layer of its iteration when it runs for each of a count;
    /// both stages' bindings bound once, every pipeline of the draw being bound alike; then what it draws: the
    /// visibility buffer's pieces, a bin at a time, the G-buffer's tiles, a resolve bin at a time, transparent pieces
    /// farthest first, depth for shadows (an iteration's arguments each), or the sky.
    /// </summary>
    private bool Draw(Listing listing, ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassDraw draw = pass.Pass.Draw;
        uint layer = pass.Pass.Each.Length > 0 ? iteration : 0;
        Span<GpuTextureLevel> colors = stackalloc GpuTextureLevel[8];
        Size size = Size.Empty;
        for (int i = 0; i < draw.Colors.Count; i++)
        {
            if (!Find(scope, draw.Colors[i], out BoundResource color))
            {
                return false;
            }

            colors[i] = new GpuTextureLevel(color.Texture, 0, layer);
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

        Reads vertexReads = default;
        Reads fragmentReads = default;
        if (pass.Bound(GpuStage.Vertex) is not { } vertexBound || pass.Bound(GpuStage.Fragment) is not { } fragmentBound
            || !vertexReads.Gather(vertexBound, scope, this) || !fragmentReads.Gather(fragmentBound, scope, this))
        {
            return false;
        }

        size = size.IsEmpty ? new Size((int)depth.Width, (int)depth.Height) : size;
        Rectangle area = new(Point.Empty, size);
        using GpuRenderPass render = gpu.Commands.RenderPass(colors[..draw.Colors.Count], draw.Contents, default, new GpuTextureLevel(depth.Texture, 0, layer));
        render.Viewport(area).Scissor(area).Constants(GpuStage.Vertex, constants).Constants(GpuStage.Fragment, constants);
        vertexReads.Bind(render, GpuStage.Vertex, vertexBound);
        fragmentReads.Bind(render, GpuStage.Fragment, fragmentBound);
        switch (draw.Drawn)
        {
            case PassDrawn.Depth:
                BindPieces(render, instances.Buffer)
                    .Pipeline(pass.Pipeline)
                    .DrawIndexedFromBuffer(args.Buffer, (uint)((draw.First + iteration) * GpuArray<GpuDrawArgs>.RowBytes), 1);
                break;
            case PassDrawn.Visibility:
                Visibility(render, listing, pass, scope, args, instances);
                break;
            case PassDrawn.Surfaces:
                Resolve(render, listing, pass, scope, args, instances);
                break;
            case PassDrawn.Transparent:
                Transparent(render, listing, pass, scope);
                break;
            case PassDrawn.Sky:
                Sky(render, listing, pass, scope);
                break;
            default:
                break;
        }

        return true;
    }

    /// <summary>
    /// The visibility buffer: each raster bin with its pipeline, from the arguments the bins' pass counted into. The two
    /// fixed bins draw what does not clip, single and double sided; then each surface's bin of clusters and its bin of
    /// cards, which take six indices, not a piece's 96, both with its pipeline.
    /// </summary>
    private void Visibility(GpuRenderPass render, Listing listing, ListedPass pass, in RecordScope scope, BoundResource args, BoundResource instances)
    {
        BindPieces(render, instances.Buffer);
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

            render.Pipeline(pipeline).DrawIndexedFromBuffer(args.Buffer, (uint)((pass.Pass.Draw.First + bin) * GpuArray<GpuDrawArgs>.RowBytes), 1);
        }
    }

    /// <summary>
    /// The G-buffer: each resolve bin's tiles with its surface's pipeline, from the arguments the classifying pass counted.
    /// </summary>
    private void Resolve(GpuRenderPass render, Listing listing, ListedPass pass, in RecordScope scope, BoundResource args, BoundResource tiles)
    {
        BindPieces(render, tiles.Buffer);
        for (int bin = 0; bin < scope.ResolveBins.Count; bin++)
        {
            GpuPipeline pipeline = graph.SurfacePipeline(listing, pass, scope.ResolveBins[bin]);
            if (pipeline == GpuPipeline.None)
            {
                continue;
            }

            render.Pipeline(pipeline).DrawIndexedFromBuffer(args.Buffer, (uint)(bin * GpuArray<GpuDrawArgs>.RowBytes), 1);
        }
    }

    /// <summary>
    /// The world's sky, its material's surface or the renderer's own, over what nothing was drawn on.
    /// </summary>
    private void Sky(GpuRenderPass render, Listing listing, ListedPass pass, in RecordScope scope)
    {
        GpuPipeline sky = graph.SurfacePipeline(listing, pass, scope.Sky);
        if (sky != GpuPipeline.None)
        {
            render.Pipeline(sky).Draw(3);
        }
    }

    /// <summary>
    /// The view's transparent pieces, farthest instance first, a run of one surface at a time.
    /// </summary>
    private void Transparent(GpuRenderPass render, Listing listing, ListedPass pass, in RecordScope scope)
    {
        if (scope.TransparentRuns.Count == 0)
        {
            return;
        }

        BindPieces(render, scope.TransparentPieces);
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
                render.Pipeline(pipeline);
                bound = pipeline;
            }

            render.DrawIndexed(PieceIndexCount, 0, 0, (uint)run.Count, (uint)run.First);
        }
    }

    /// <summary>
    /// Binds pieces to draw: their instance-rate buffer and the indices. The scene's tables their vertex stage reads are
    /// bound as any stage's reads are.
    /// </summary>
    private GpuRenderPass BindPieces(GpuRenderPass render, GpuBuffer pieces) => render.VertexBuffers([pieces]).IndexBuffer(pieceIndices, GpuIndexSize.Bits16);

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

    /// <summary>
    /// The sampler a texture is read through: its own, where the frame binds it with one (the materials' textures), else
    /// by its filter: nearest for depth and single-channel textures, linear for the rest, unless its filter says.
    /// </summary>
    private GpuSampler SamplerOf(ResourceInfo info, in BoundResource resource)
    {
        if (resource.Sampler != GpuSampler.None)
        {
            return resource.Sampler;
        }

        bool isNearest = info.IsDepth || info.Format is GpuFormat.R32Float or GpuFormat.R32Uint or GpuFormat.R16Float;
        return info.Filter switch
        {
            PassFilter.Nearest => _nearest,
            PassFilter.Linear => _linear,
            PassFilter.Comparison => _comparison,
            PassFilter.Default => isNearest ? _nearest : _linear,
            _ => isNearest ? _nearest : _linear,
        };
    }

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
        if (isView || pass.DispatchOutput.Length > 0)
        {
            Vector2 view = scope.Constants.ViewSize;
            (uint width, uint height) = ((uint)view.X, (uint)view.Y);
            if (!isView && Find(scope, pass.DispatchOutput, out BoundResource output))
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
        GpuTextureUsage usage = created.TextureUsage | (created.Info.IsStorageRead ? GpuTextureUsage.None : GpuTextureUsage.Sampled) | (created.Info.IsDepth ? GpuTextureUsage.DepthTarget : GpuTextureUsage.None)
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

    /// <summary>
    /// What a stage reads this frame, gathered from what it binds, each at its slot: sampled textures with the sampler
    /// each is read through, textures read with none, and buffers; false when one is not there yet.
    /// </summary>
    private ref struct Reads
    {
        private Buffer16<GpuSampledTexture> _sampled;
        private Buffer8<GpuTexture> _storage;
        private Buffer8<GpuBuffer> _buffers;

        public bool Gather(StageBindings bound, in RecordScope scope, PassRecorder recorder)
        {
            foreach (BoundName read in bound.Sampled)
            {
                if (!Find(scope, read.Binding.Info.Name, out BoundResource resource))
                {
                    return false;
                }

                _sampled[(int)read.Slot] = new GpuSampledTexture(resource.Texture, recorder.SamplerOf(read.Binding.Info, resource));
            }

            foreach (BoundName read in bound.StorageReads)
            {
                if (!Find(scope, read.Binding.Info.Name, out BoundResource resource))
                {
                    return false;
                }

                _storage[(int)read.Slot] = resource.Texture;
            }

            foreach (BoundName read in bound.Buffers)
            {
                if (!Find(scope, read.Binding.Info.Name, out BoundResource resource))
                {
                    return false;
                }

                _buffers[(int)read.Slot] = resource.Buffer;
            }

            return true;
        }

        public void Bind(GpuRenderPass render, GpuStage stage, StageBindings bound)
        {
            if (bound.Sampled.Count > 0)
            {
                render.Textures(stage, _sampled[..bound.Sampled.Count]);
            }

            if (bound.StorageReads.Count > 0)
            {
                render.RawTextures(stage, _storage[..bound.StorageReads.Count]);
            }

            if (bound.Buffers.Count > 0)
            {
                render.Buffers(stage, _buffers[..bound.Buffers.Count]);
            }
        }

        public void Bind(GpuComputePass compute, StageBindings bound)
        {
            if (bound.Sampled.Count > 0)
            {
                compute.Textures(_sampled[..bound.Sampled.Count]);
            }

            if (bound.StorageReads.Count > 0)
            {
                compute.RawTextures(_storage[..bound.StorageReads.Count]);
            }

            if (bound.Buffers.Count > 0)
            {
                compute.Buffers(_buffers[..bound.Buffers.Count]);
            }
        }
    }

    [System.Runtime.CompilerServices.InlineArray(StageBindings.MaxSampled)]
    private struct Buffer16<T>
    {
        private T _first;
    }

    [System.Runtime.CompilerServices.InlineArray(StageBindings.MaxStorage)]
    private struct Buffer8<T>
    {
        private T _first;
    }
}

/// <summary>
/// What a stage records against: the frame's resources and a view's, or a target's in a target's stage, the constants
/// and counts of the frame or the view, the surfaces of the raster and resolve bins, the view's transparent pieces and
/// their runs, the sky's surface, and what a script is told: the commands, the frame now, and the target's format and
/// encoding.
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
    GpuCommands Commands,
    Frame Now,
    GpuFormat TargetFormat = GpuFormat.Invalid,
    TargetEncoding TargetEncoding = TargetEncoding.Linear);

/// <summary>
/// A run of a view's transparent pieces drawn with one surface's pipeline.
/// </summary>
internal readonly record struct TransparentRun(SurfaceKey Surface, int First, int Count);

/// <summary>
/// The counts a pass file can name, for how many times a pass runs, how many groups it dispatches and how big a buffer it
/// makes is: the scene's and the view's (its tiles times its resolve bins, <c>tileEntries</c>, among them), the lights'
/// (the world's light slots, and those a view holds), and the shadows' (their tables' entries, the pages drawn a frame
/// at most, their groups, and the pool's pages to take). A number names itself.
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
    uint LightTileWords,
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
        "tileEntries" => Tiles * Math.Max(1, ResolveBins),
        "hizTexels" => HizTexels,
        "hizLevels" => HizLevels,
        "lights" => Lights,
        "zBins" => ZBins,
        "lightTileWords" => LightTileWords,
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
