using System.Drawing;
using System.IO;
using System.Numerics;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The counts a pass file can name, for how many times a pass runs, how many groups it dispatches and how big a buffer
/// it makes is, each by its name with its first letter small (<c>tileEntries</c>): the instances' and the view's, the
/// lights' (the world's light slots, and those a view holds), and the shadows' (their tables' entries, the pages drawn
/// a frame at most, their groups, and the pool's pages to take).
/// </summary>
internal enum PassCount
{
    /// <summary>
    /// None named: the pass runs once, or what it makes is as big as its file says.
    /// </summary>
    None,

    /// <summary>
    /// A number, which names itself.
    /// </summary>
    Number,
    Instances,
    Pieces,
    Clusters,
    Jobs,
    RasterBins,

    /// <summary>
    /// A view's tiles times its resolve bins.
    /// </summary>
    TileEntries,
    HizTexels,
    HizLevels,
    Lights,
    ZBins,
    LightTileWords,
    GiRays,
    GiProbeThreads,
    WorldLights,
    ClipmapEntries,
    LightEntries,
    ShadowViews,
    ShadowGroups,
    ShadowCandidates,
}

/// <summary>
/// What a stage records against: the renderer's resources with the frame's scope, and a view's scope, or a target's in
/// a target's stage, which is looked in first; the constants and counts of the frame or the view, the view's
/// transparent pieces and their runs, the sky's surface, the frame now, and a target's format and encoding.
/// </summary>
internal readonly record struct RecordScope(
    GpuResources Resources,
    ResourceScope? View,
    FrameConstants Constants,
    Counts Counts,
    IReadOnlyList<TransparentRun> TransparentRuns,
    GpuBuffer TransparentPieces,
    SurfaceKey Sky,
    Frame Now,
    GpuFormat TargetFormat = GpuFormat.Invalid,
    TargetEncoding TargetEncoding = TargetEncoding.Linear);

/// <summary>
/// A run of a view's transparent pieces drawn with one surface's pipeline.
/// </summary>
internal readonly record struct TransparentRun(SurfaceKey Surface, int First, int Count);

/// <summary>
/// How much of each count a pass file can name the frame holds (<see cref="PassCount"/>), from the settings and what
/// the world has.
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
    uint GiRays,
    uint GiProbeThreads,
    uint WorldLights,
    uint ClipmapEntries,
    uint LightEntries,
    uint ShadowViews,
    uint ShadowGroups,
    uint ShadowCandidates)
{
    internal uint GetCount(CountName name) => name.Count switch
    {
        PassCount.None => 0,
        PassCount.Number => name.Number,
        PassCount.Instances => Instances,
        PassCount.Pieces => Pieces,
        PassCount.Clusters => Clusters,
        PassCount.Jobs => Jobs,
        PassCount.RasterBins => RasterBins,
        PassCount.TileEntries => Tiles * Math.Max(1, ResolveBins),
        PassCount.HizTexels => HizTexels,
        PassCount.HizLevels => HizLevels,
        PassCount.Lights => Lights,
        PassCount.ZBins => ZBins,
        PassCount.LightTileWords => LightTileWords,
        PassCount.GiRays => GiRays,
        PassCount.GiProbeThreads => GiProbeThreads,
        PassCount.WorldLights => WorldLights,
        PassCount.ClipmapEntries => ClipmapEntries,
        PassCount.LightEntries => LightEntries,
        PassCount.ShadowViews => ShadowViews,
        PassCount.ShadowGroups => ShadowGroups,
        PassCount.ShadowCandidates => ShadowCandidates,
        _ => 0,
    };
}

/// <summary>
/// A count a pass file names, read as the pass loads: one the frame holds, a number, or none.
/// </summary>
internal readonly record struct CountName(PassCount Count, uint Number = 0)
{
    /// <summary>
    /// The counts by the names a pass file uses for them.
    /// </summary>
    private static readonly Dictionary<string, PassCount> _names = GetNames();

    internal bool IsNone => Count == PassCount.None;

    /// <summary>
    /// The count <paramref name="name"/> names, none for an empty name; false for a name no count has.
    /// </summary>
    internal static bool TryParse(string name, out CountName count)
    {
        count = default;

        if (name.Length == 0) return true;

        if (uint.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out uint number))
        {
            count = new CountName(PassCount.Number, number);

            return true;
        }

        if (!_names.TryGetValue(name, out PassCount named)) return false;

        count = new CountName(named);

        return true;
    }

    /// <summary>
    /// The count <paramref name="name"/> names; none for a name no count has, which fails its pass as it is listed.
    /// </summary>
    internal static CountName Of(string name) => TryParse(name, out CountName count) ? count : default;

    private static Dictionary<string, PassCount> GetNames()
    {
        Dictionary<string, PassCount> names = new(StringComparer.Ordinal);
        foreach (PassCount count in Enum.GetValues<PassCount>())
        {
            string name = count.ToString();
            if (count > PassCount.Number) names[$"{char.ToLowerInvariant(name[0])}{name[1..]}"] = count;
        }

        return names;
    }
}

/// <summary>
/// The passes a frame runs, and running them. Its members go in that order: the listing in use and the next, building
/// the next on workers, then recording a stage (<see cref="Record"/>).
/// <para>
/// The passes are the renderer's pipeline's, then the world's effects', then the overlay pipeline's, as each loads with
/// its passes, and a debug view's pass after them, put in their stages, checked, and compiled, as a
/// <see cref="Listing"/>. A script pass's script is made by <paramref name="scripts"/>, without which it does not run.
/// Whatever changes them (the world names other effects, a pass, pipeline, shader or curve is edited) builds a new
/// listing from scratch on workers, and the frame keeps drawing with the old one until every pass of the new is
/// compiled, then swaps. The listing's state is the render thread's: workers load and compile, and hand what they made
/// to it.
/// </para>
/// <para>
/// Every pass runs one way: what it makes made (or kept) and seeded, then for each of its iterations its constants
/// pushed, every name each of its stages binds bound at its slot, each looked up by name in the frame's or the view's
/// resources, and its dispatch or draws, under a debug group of its name. A draw of pieces (the visibility buffer,
/// transparent surfaces, depth) steps through the pieces a pass before it made with the one run of indices it holds; a
/// draw of the visibility buffer is one indirect draw per raster bin, and one of surfaces one per resolve bin, over the
/// tiles a pass before it classified. Recording holds nothing of a frame between calls.
/// </para>
/// <para>
/// A draw's pipelines are composed around the surfaces it draws, one per <see cref="SurfaceKey"/>: the visibility
/// buffer's raster bins, the clipping template with the surface (Templates/VisibilityClip.frag.hlsl) or the fixed one
/// without (Templates/Visibility.frag.hlsl), over the pieces' vertex stage, whole or only their place
/// (Templates/Pieces.vert.hlsl); the G-buffer's resolve bins (Templates/Resolve.frag.hlsl) over the tiles' quads
/// (Templates/Tiles.vert.hlsl); transparent surfaces forward over the pieces (Templates/ForwardPieces.frag.hlsl); and
/// the sky (Templates/Sky.frag.hlsl), its shadow on the world (Templates/SkyShadow.frag.hlsl) and the air in front
/// of a view (Templates/SkyAir.frag.hlsl) over the fullscreen triangle.
/// </para>
/// <para>
/// A shader binds what it uses: each stage's is first compiled to SPIR-V with every name its pass can see declared, a
/// probe that reads back which it used (<see cref="StageBindings"/>), then compiled for the GPU with those at the
/// registers the pass binds them at, and checked to use no others. A draw's probe is its templates over the default
/// surface, with what the surface contract offers any surface, and every surface of it is bound the same.
/// </para>
/// </summary>
internal sealed class PassGraph(
    IGpu gpu,
    IShaderCompiler compiler,
    Assets assets,
    IScripts? scripts,
    Threads threads,
    FailureHandler failureHandler,
    CancellationToken stopping) : IDisposable
{
    private const string FullscreenPath = "Shaders/Passes/Fullscreen.vert.hlsl";
    private const string DepthOnlyPath = "Shaders/Passes/DepthOnly.frag.hlsl";
    private const string PiecesPath = "Templates/Pieces.vert.hlsl";
    private const string TilesPath = "Shaders/Templates/Tiles.vert.hlsl";

    /// <summary>
    /// Bytes of one piece the pieces' vertex stage steps through: its instance, which cluster or card and piece, and
    /// its slot, which the visibility buffer names it by.
    /// </summary>
    private const uint PieceBytes = 12;

    /// <summary>
    /// Groups a dispatch runs along x at most, as every API allows; more go on in y, which a shader that may run more
    /// reads back as one number (Shaders/Include/Culling.hlsli's LinearGroup).
    /// </summary>
    private const uint MaxGroupsX = 65535;

    /// <summary>
    /// The samplers a texture is read through when the frame binds it with none of its own (<see cref="GetSampler"/>).
    /// </summary>
    private static readonly GpuSamplerDescription _nearest = new(GpuFilter.Nearest, GpuWrap.Clamp);
    private static readonly GpuSamplerDescription _linear = new(GpuFilter.Linear, GpuWrap.Clamp);
    private static readonly GpuSamplerDescription _comparison = new(
        GpuFilter.Linear,
        GpuWrap.Clamp,
        Compare: GpuCompare.GreaterOrEqual);

    /// <summary>
    /// A piece's three words, as the pieces' vertex stage reads them from the instance-rate buffer.
    /// </summary>
    private static readonly GpuVertexInput[] _pieceInputs =
    [
        new GpuVertexInput(0, 0, GpuVertexFormat.Uint1, 0),
        new GpuVertexInput(1, 0, GpuVertexFormat.Uint1, 4),
        new GpuVertexInput(2, 0, GpuVertexFormat.Uint1, 8),
    ];

    /// <summary>
    /// What the shared vertex stages can see: the renderer's own names, the frame-wide tables among them.
    /// </summary>
    private static readonly List<ResourceInfo> _engineVisible = [.. GpuContract.All.Values];

    private Listing? _next;

    /// <summary>
    /// What the latest listing, built or building, was built from, but the effects (<see cref="Effects"/>), and whether
    /// something changed since that calls for another.
    /// </summary>
    private Handle<Pass> _appended;
    private Handle<Pipeline> _overlay;
    private Handle<Pipeline> _pipeline;
    private bool _isStale = true;

    /// <summary>
    /// The surface variants the frame draws, as the latest <see cref="Update"/> was told: the raster and resolve bins
    /// <see cref="Record"/> draws, in bin order.
    /// </summary>
    private SurfaceVariants _variants = new([], [], [], default);

    /// <summary>
    /// The listing the frame draws with; none until the first is compiled.
    /// </summary>
    internal Listing? Current { get; private set; }

    /// <summary>
    /// The effects the latest listing, built or building, runs.
    /// </summary>
    internal Handle<Pipeline> Effects { get; private set; }

    public void Dispose()
    {
        Current?.Dispose();
        _next?.Dispose();
    }

    /// <summary>
    /// A pipeline (made again when one of its passes changes) or a shader changed: the listing is built again, once the
    /// build under way, if any, is swapped in, so a burst of changes (a remake of every shader, one at a time) is one
    /// further build, not one each.
    /// </summary>
    internal void Invalidate() => _isStale = true;

    /// <summary>
    /// Runs <paramref name="pass"/> after the world's effects from now on, as a debug view is; none to stop. The
    /// listing is built again when it changes.
    /// </summary>
    internal void Append(Handle<Pass> pass)
    {
        _isStale |= pass != _appended;
        _appended = pass;
    }

    /// <summary>
    /// Swaps in the listing built once it is compiled with every surface variant the frame draws now, those that first
    /// showed while it was built among them, so the swap shows no gap; packs again in place the params a game set on
    /// the pipelines the frame runs (<see cref="PassParams"/>); and starts building one when the renderer's
    /// <paramref name="pipeline"/> or the world's effects are not the ones built or something changed, a param set to a
    /// curve not yet loaded among those, its surface pipelines compiled up front for the variants the frame draws now.
    /// One listing builds at a time: while one is under way it is finished and swapped in first, then what changed
    /// meanwhile is built, so the frame draws as soon as it can (the loading domain, before the first is compiled) and
    /// a burst of changes starts one build, not one each; a build that throws (an error, said by FireAndForget) never
    /// swaps in, and nothing builds again after it. Each frame, before anything records.
    /// </summary>
    internal void Update(
        Handle<Pipeline> pipeline,
        Handle<Pipeline> effects,
        Handle<Pipeline> overlay,
        SurfaceVariants variants)
    {
        _variants = variants;
        if (_next is { IsCompiled: true } built && HasEvery(built, variants))
        {
            Current?.Dispose();
            Current = built;
            _next = null;
        }

        // A curve the listing drawn did not load asks for a build once; the build under way, if any, loads it.
        bool isFollowed = Current?.TryFollowParams() ?? true;
        _isStale |= !isFollowed && _next is null;

        if ((!_isStale && effects == Effects && pipeline == _pipeline && overlay == _overlay) || _next is not null)
            return;

        _isStale = false;
        Effects = effects;
        _pipeline = pipeline;
        _overlay = overlay;
        _next = new Listing(gpu);
        BuildAsync(
            _next,
            pipeline,
            effects,
            overlay,
            _appended,
            new SurfaceVariants(
                [.. variants.Raster],
                [.. variants.Resolve],
                [.. variants.Transparent],
                variants.Sky)).FireAndForget();
    }

    /// <summary>
    /// Whether the frame draws with a listing built for <paramref name="effects"/>, and none is being built, which the
    /// frame would swap in once it is compiled.
    /// </summary>
    internal bool IsBuiltFor(Handle<Pipeline> effects) => Current is not null && _next is null && Effects == effects;

    /// <summary>
    /// Whether the frame can draw <paramref name="drawn"/>, a material packed, every pipeline it is drawn with compiled:
    /// the raster bins' for its clusters, which clip as it fades or its cards take over, and the resolve bin's for its
    /// pixels, or the forward one for a transparent material; for a sky, the sky's, its capture's, its shadow's and its
    /// air's. Those not asked for yet start compiling, for the listing being built while there is one, as that is the
    /// one that will draw it: it is drawable once that listing is swapped in. A material waits for this
    /// (<see cref="GpuResources"/>), so nothing is drawn black, left out, or drawn without the world's effects for a
    /// while.
    /// </summary>
    internal bool IsDrawable(DrawnMaterial drawn)
    {
        if ((_next ?? Current) is not { } listing) return false;

        SurfaceKey surface = drawn.Surface;
        bool isSurface = !drawn.IsSky && drawn.Type != MaterialType.Transparent;
        bool isTransparent = !drawn.IsSky && drawn.Type == MaterialType.Transparent;
        bool isDrawable = true;
        foreach (ListedPass pass in listing.Passes)
        {
            SurfaceKey? variant = pass.Pass.Kind != PassKind.Draw ? null : pass.Pass.Draw.Drawn switch
            {
                PassDrawn.Visibility => isSurface ? surface : null,
                PassDrawn.Surfaces => isSurface ? surface with { Masked = false } : null,
                PassDrawn.Transparent => isTransparent ? surface : null,
                PassDrawn.Sky or PassDrawn.SkyShadow or PassDrawn.SkyAir => drawn.IsSky ? surface : null,
                PassDrawn.Depth => null,
                _ => null,
            };

            if (variant is not { } key) continue;

            // A pass of the listing being built is asked only once it has come back, as it then starts the variants it
            // has so far, which its listing waits for, and one asked before would be waited for though never counted.
            // One of the frame's listing that is not ready did not compile, draws nothing, and holds nothing back.
            if (!pass.IsReady)
            {
                isDrawable &= listing == Current;

                continue;
            }

            isDrawable &= IsMade(pass, key);
        }

        return isDrawable && listing == Current;
    }

    /// <summary>
    /// The passes of <paramref name="stage"/> that are ready, in order, in <paramref name="scope"/>: the frame's
    /// resources, and a view's for a view's stage. One that never runs has what it would draw into shown as the
    /// failure instead (<see cref="Fill"/>).
    /// </summary>
    internal void Record(Listing listing, PassStage stage, in RecordScope scope)
    {
        foreach (ListedPass pass in listing.Stages[(int)stage])
        {
            if (!pass.IsReady)
            {
                if (pass.HasFailed) Fill(listing, pass, scope);

                continue;
            }

            using GpuDebugGroup group = gpu.Commands.DebugGroup(pass.Name);
            Make(listing, pass, scope);
            uint iterations = GetIterations(pass, scope.Counts);
            for (uint iteration = 0; iteration < iterations; iteration++)
            {
                FrameConstants constants = scope.Constants;
                constants.Pass = pass.Params.Words;
                constants.Pass[28] = iteration;
                bool isRecorded = pass.Pass.Kind switch
                {
                    PassKind.Compute => TryRecordCompute(pass, scope, constants, iteration),
                    PassKind.Fullscreen => TryRecordFullscreen(pass, scope, constants, iteration),
                    PassKind.Draw => TryRecordDraw(pass, scope, constants, iteration),
                    PassKind.Script => TryRecordScript(pass, scope),
                    _ => false,
                };

                if (!isRecorded) break;
            }

            Mipmaps(pass, scope);
        }
    }

    private static void Keep(ListedPass pass, GpuStage stage, Result<StageBindings> bound)
    {
        if (bound.Ok) pass.SetBound(stage, bound.Value);
        else pass.ProbeError = bound.Error;
    }

    /// <summary>
    /// The pass's own shader's text, named as its errors name it.
    /// </summary>
    private static string GetPassBody(ListedPass pass) => $"#line 1 \"{pass.Name}\"\n{pass.Body}";

    private static string GetPiecesText(bool placeOnly)
        => $"#define PIECES_PLACE_ONLY {(placeOnly ? 1 : 0)}\n#include \"{PiecesPath}\"\n";

    /// <summary>
    /// The template a draw composes its surfaces into.
    /// </summary>
    private static string GetTemplate(PassDrawn drawn, bool hasSurface)
    {
        return drawn switch
        {
            PassDrawn.Visibility => hasSurface ? "VisibilityClip" : "Visibility",
            PassDrawn.Transparent => "ForwardPieces",
            PassDrawn.Sky => "Sky",
            PassDrawn.SkyShadow => "SkyShadow",
            PassDrawn.SkyAir => "SkyAir",
            PassDrawn.Surfaces or PassDrawn.Depth => "Resolve",
            _ => "Resolve",
        };
    }

    /// <summary>
    /// A draw's colours as bindings, at their first level and layer: each is a name the pass can see.
    /// </summary>
    private static List<PassBinding> GetColors(ListedPass pass)
    {
        List<PassBinding> colors = [];
        foreach (string name in pass.Pass.Draw.Colors)
        {
            if (pass.Visible.Find(seen => string.Equals(seen.Name, name, StringComparison.Ordinal)) is { } info)
                colors.Add(new PassBinding(info, 0, 0, GpuContents.Clear, false));
        }

        return colors;
    }

    /// <summary>
    /// Binds pieces to draw: their instance-rate buffer and the indices. The frame-wide tables their vertex stage reads
    /// are bound as any stage's reads are.
    /// </summary>
    private static GpuRenderPass BindPieces(GpuRenderPass render, GpuBuffer pieces, GpuResources resources)
        => render.VertexBuffers([pieces]).IndexBuffer(resources.PieceIndices, GpuIndexSize.Bits16);

    /// <summary>
    /// The sampler a texture is read through: its own, where the frame binds it with one (the materials' textures),
    /// else by its filter: nearest for depth and single-channel textures, linear for the rest, unless its filter says.
    /// </summary>
    private static GpuSampler GetSampler(ResourceInfo info, in BoundResource resource, GpuResources resources)
    {
        if (resource.Sampler != GpuSampler.None) return resource.Sampler;

        bool isNearest = info.IsDepth || info.Format is GpuFormat.R32Float or GpuFormat.R32Uint or GpuFormat.R16Float;

        return info.Filter switch
        {
            PassFilter.Nearest => resources.GetSampler(_nearest),
            PassFilter.Linear => resources.GetSampler(_linear),
            PassFilter.Comparison => resources.GetSampler(_comparison),
            PassFilter.Default => resources.GetSampler(isNearest ? _nearest : _linear),
            _ => resources.GetSampler(isNearest ? _nearest : _linear),
        };
    }

    /// <summary>
    /// How many groups a compute pass runs: enough threads for the view or a texture it writes, one per of a count
    /// (rows of <see cref="MaxGroupsX"/>), or fixed.
    /// </summary>
    private static (uint X, uint Y, uint Z) GetGroups(ListedPass pass, in RecordScope scope)
    {
        PassDispatch dispatch = pass.Pass.Dispatch;
        (uint threadsX, uint threadsY, uint threadsZ) = pass.Threads;

        if (dispatch.Groups.Count > 0)
        {
            return (
                dispatch.Groups[0],
                dispatch.Groups.Count > 1 ? dispatch.Groups[1] : 1,
                dispatch.Groups.Count > 2 ? dispatch.Groups[2] : 1);
        }

        bool isView = pass.DispatchesPerView;
        if (isView || pass.DispatchOutput.Length > 0)
        {
            Vector2 view = scope.Constants.ViewSize;
            (uint width, uint height) = ((uint)view.X, (uint)view.Y);
            if (!isView && TryGetResource(scope, pass.DispatchOutput, out BoundResource output))
                (width, height) = (output.Width, output.Height);

            return dispatch.Tile > 0
                ? (CeilingDivide(width, dispatch.Tile), CeilingDivide(height, dispatch.Tile), 1u)
                : (CeilingDivide(width, threadsX), CeilingDivide(height, threadsY), 1u);
        }

        uint groups = CeilingDivide(scope.Counts.GetCount(pass.DispatchPer), threadsX * threadsY * threadsZ);

        return (Math.Min(groups, MaxGroupsX), CeilingDivide(groups, MaxGroupsX), 1);
    }

    /// <summary>
    /// How many times a pass runs this frame: once, or once for each of the count it names.
    /// </summary>
    private static uint GetIterations(ListedPass pass, in Counts counts)
        => pass.Each.IsNone ? 1 : counts.GetCount(pass.Each);

    /// <summary>
    /// The layer a texture write goes to: its iteration's, in a texture array a pass writes for each of a count.
    /// </summary>
    private static uint GetLayer(ListedPass pass, PassBinding write, uint iteration)
    {
        return !pass.Each.IsNone && write.Info.Shape is GpuTextureKind.Texture2DArray or GpuTextureKind.Cube
            ? iteration
            : write.Layer;
    }

    /// <summary>
    /// What a pass makes is made with, as its file and this frame's sizes and counts say: a texture follows the view's
    /// size as it is drawn, or as it is shown.
    /// </summary>
    private static ResourceKey GetKey(CreatedResource created, in RecordScope scope)
    {
        PassCreate create = created.Create;
        if (create.Kind == PassResource.Buffer)
        {
            uint rows = created.Per.IsNone
                ? create.Rows
                : BitOperations.RoundUpToPowerOf2(Math.Max(1, scope.Counts.GetCount(created.Per)));
            uint copies = created.Copies.IsNone ? 1 : Math.Max(1, scope.Counts.GetCount(created.Copies));

            return new ResourceKey(
                true,
                GpuFormat.Invalid,
                GpuTextureKind.Texture2D,
                GpuTextureUsage.None,
                created.BufferUsage,
                0,
                0,
                0,
                0,
                Math.Max(rows * create.Stride, create.Stride) * copies);
        }

        Vector2 view = create.Size == PassSize.Output ? scope.Constants.OutputSize : scope.Constants.ViewSize;
        uint side = created.Per.IsNone ? 0 : Math.Max(1, scope.Counts.GetCount(created.Per));
        uint width = (side, create.Scale) switch
        {
            ( > 0, _) => side,
            (_, > 0) => (uint)Math.Max(1, MathF.Ceiling(view.X * create.Scale)),
            _ => create.Width,
        };
        uint height = (side, create.Scale) switch
        {
            ( > 0, _) => side,
            (_, > 0) => (uint)Math.Max(1, MathF.Ceiling(view.Y * create.Scale)),
            _ => create.Height,
        };
        uint layers = create switch
        {
            { Cube: true } => 6,
            { Depth: > 0 } => create.Depth,
            _ => create.Layers,
        };
        uint levels = Math.Min(create.Levels, (uint)BitOperations.Log2(Math.Max(width, height)) + 1);

        // Sampled whether or not a pass reads it: the renderer shows the view's Ldr, which no pass does. A texture kept
        // from frame to frame is drawn into once as it is made, to empty its history.
        GpuTextureUsage usage = created.TextureUsage
            | (created.Info.IsStorageRead ? GpuTextureUsage.None : GpuTextureUsage.Sampled)
            | (created.Info.IsDepth ? GpuTextureUsage.DepthTarget : GpuTextureUsage.None)
            | (create.History ? GpuTextureUsage.ColorTarget : GpuTextureUsage.None);

        return new ResourceKey(
            false,
            create.Format,
            created.Info.Shape,
            usage,
            GpuBufferUsage.None,
            width,
            height,
            layers,
            levels,
            0,
            create.History);
    }

    /// <summary>
    /// The bytes of one copy of a buffer a pass makes: as many rows as its count, not as many as it is made with, so an
    /// iteration's offset agrees with what the shader that fills it works out.
    /// </summary>
    private static uint GetSliceBytes(CreatedResource created, in Counts counts)
    {
        return (created.Per.IsNone ? created.Create.Rows : Math.Max(1, counts.GetCount(created.Per)))
            * created.Create.Stride;
    }

    private static bool TryGetResource(in RecordScope scope, string name, out BoundResource resource)
    {
        return (scope.View is { } view && view.TryGet(name, out resource))
            || scope.Resources.Frame.TryGet(name, out resource);
    }

    private static uint CeilingDivide(uint count, uint per)
        => (Math.Max(1, count) + Math.Max(1, per) - 1) / Math.Max(1, per);

    /// <summary>
    /// A draw pass's pipeline for <paramref name="variant"/>, compiling it the first time it is asked for; none while
    /// it compiles.
    /// </summary>
    private GpuPipeline GetSurfacePipeline(ListedPass pass, SurfaceKey variant)
    {
        if (pass.Surfaces.TryGetValue(variant, out GpuPipeline made)) return made;

        pass.Surfaces[variant] = GpuPipeline.None;
        CompileSurfaceAsync(pass, variant, isCounted: false).FireAndForget();

        return GpuPipeline.None;
    }

    /// <summary>
    /// Whether a listing has the pipelines of every surface variant the frame draws, asking for those it has not.
    /// </summary>
    private bool HasEvery(Listing listing, SurfaceVariants variants)
    {
        bool hasEvery = true;
        foreach (ListedPass pass in listing.Passes)
        {
            if (!pass.DrawsSurfaces) continue;

            foreach (SurfaceKey variant in pass.GetVariants(variants))
            {
                hasEvery &= IsMade(pass, variant);
            }
        }

        return hasEvery;
    }

    /// <summary>
    /// Whether a draw pass's pipeline for <paramref name="variant"/> is made, or never will be, as its surface does not
    /// compile, so nothing waits for it for ever; compiling it the first time it is asked for.
    /// </summary>
    private bool IsMade(ListedPass pass, SurfaceKey variant)
        => GetSurfacePipeline(pass, variant) != GpuPipeline.None || pass.Broken.Contains(variant);

    /// <summary>
    /// Loads the passes of the frame pipeline, the world's effects and the pipeline appended after them and their
    /// shaders, fits them on the render thread, probes what every stage of each uses, compiling each pass bound to that
    /// as soon as its own probe is done, with every draw's vertex stages and pipelines for the variants it draws, and
    /// once every pass is probed, what each thing made is used for. The listing goes live only once that is added and
    /// every pipeline is back.
    /// </summary>
    private async Task BuildAsync(
        Listing listing,
        Handle<Pipeline> pipeline,
        Handle<Pipeline> effects,
        Handle<Pipeline> overlay,
        Handle<Pass> appended,
        SurfaceVariants variants)
    {
        List<string> failures = [];
        List<Loaded> loaded = await LoadPassesAsync(listing, pipeline, effects, overlay, appended, failures)
            .ConfigureAwait(false);
        await threads.InvokeAsync(EngineThread.Render, () => listing.Fit(loaded, failures, variants), stopping)
            .ConfigureAwait(false);

        // What binds nothing compiles at once; the place-only pieces use a part of what the pieces do, and are bound as
        // the pieces are, so a draw binds once.
        Task<Result<GpuShader>> fullscreen = compiler.CompileAsync(
            FullscreenPath,
            GpuStage.Vertex,
            gpu.ShaderFormat,
            stopping);
        Task<Result<GpuShader>> tiles = compiler.CompileAsync(TilesPath, GpuStage.Vertex, gpu.ShaderFormat, stopping);
        Task<Result<StageBindings>> piecesProbe = ProbeAsync(
            _engineVisible,
            [],
            GpuStage.Vertex,
            [(GetPiecesText(placeOnly: false), "Pieces")]);
        Task<Result<GpuShader>> pieces = CompilePiecesAsync(placeOnly: false, "Pieces");
        Task<Result<GpuShader>> places = CompilePiecesAsync(placeOnly: true, "Pieces (place only)");
        List<Task> probes = [];
        foreach (ListedPass pass in listing.Passes)
        {
            probes.Add(ProbeThenCompileAsync(pass));
        }

        await Task.WhenAll(probes).ConfigureAwait(false);
        await threads.InvokeAsync(EngineThread.Render, listing.Use, stopping).ConfigureAwait(false);
        listing.Done();

        async Task<Result<GpuShader>> CompilePiecesAsync(bool placeOnly, string name)
        {
            Result<StageBindings> bound = await piecesProbe.ConfigureAwait(false);

            return await CompileBoundAsync(
                _engineVisible,
                [],
                bound,
                GpuStage.Vertex,
                GetPiecesText(placeOnly),
                name).ConfigureAwait(false);
        }

        async Task ProbeThenCompileAsync(ListedPass pass)
        {
            await ProbePassAsync(pass, piecesProbe).ConfigureAwait(false);
            CompileAsync(listing, pass, fullscreen, pieces, places, tiles).FireAndForget();
        }
    }

    /// <summary>
    /// The passes of the frame pipeline, the world's effects, the overlay pipeline and the pass appended after them,
    /// with their shaders, in that order, the pipelines held by <paramref name="listing"/>; what does not load is said
    /// and added to <paramref name="failures"/>.
    /// </summary>
    private async Task<List<Loaded>> LoadPassesAsync(
        Listing listing,
        Handle<Pipeline> pipeline,
        Handle<Pipeline> effects,
        Handle<Pipeline> overlay,
        Handle<Pass> appended,
        List<string> failures)
    {
        List<Loaded> loaded = [];
        foreach (Handle<Pipeline> handle in (Handle<Pipeline>[])[pipeline, effects, overlay])
        {
            if (handle == Handle<Pipeline>.None) continue;

            Result<Pipeline> frame = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
            if (!frame.Ok)
            {
                Debugging.Error($"A pipeline does not load, so its passes do not run: {frame.Error}");
                failures.Add($"A pipeline does not load: {Listing.GetFirstLine(frame.Error)}");

                continue;
            }

            listing.Pipelines.Add(frame.Value);
            foreach (PipelinePass pass in frame.Value.Passes)
            {
                if (await LoadPassAsync(pass, failures).ConfigureAwait(false) is { } ready) loaded.Add(ready);
            }
        }

        if (loaded.Count == 0)
        {
            Debugging.Warn("The frame runs no passes, so nothing is drawn: the renderer's pipeline and the "
                + "world's effects have none.");
        }

        if (appended != Handle<Pass>.None)
        {
            Result<Pass> view = await assets.LoadAsync(appended, stopping).ConfigureAwait(false);
            string name = Path.GetFileNameWithoutExtension(assets.GetPath(appended) ?? string.Empty);
            if (!view.Ok)
            {
                Debugging.Error($"Debug view {name} does not load, so it is not shown: {view.Error}");
            }
            else if (await LoadPassAsync(new PipelinePass(view.Value, name, [.. view.Value.Params]), failures)
                .ConfigureAwait(false) is { } ready)
            {
                loaded.Add(ready);
            }
        }

        return loaded;
    }

    /// <summary>
    /// A pass with its shader's text as HLSL takes it, its params' loader in, and its params packed, those the pipeline
    /// loader merged over the shader's defaults, with the curves they name, to pack again as a game sets them. Null,
    /// said once and added to <paramref name="failures"/>, when its shader does not load.
    /// </summary>
    private async Task<Loaded?> LoadPassAsync(PipelinePass pass, List<string> failures)
    {
        Result<string> text = pass.Pass.Shader == Handle<Shader>.None
            ? Result.Success(string.Empty)
            : await TextOfAsync(pass.Pass.Shader).ConfigureAwait(false);
        Result<ParamLayout?> layout = text.Ok
            ? ParamLayout.Parse(
                text.Value,
                "PassParams",
                allowTextures: false,
                allowCurves: true,
                ParamLayout.PassBytes)
            : Result.Failure<ParamLayout?>(text.Error);
        if (!text.Ok || !layout.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {layout.Error}");
            failures.Add($"Pass {pass.Name} does not run: {Listing.GetFirstLine(layout.Error)}");

            return null;
        }

        // The count before the values are read, so a param set while the build reads them is packed again after.
        long version = pass.Params.Version;
        string body = text.Value;
        Dictionary<Handle<Curve>, Curve> curves = [];
        if (layout.Value is { } paramLayout)
        {
            body = paramLayout.Prepare("LoadPassParams()", string.Empty, "PassRaw");
            curves = await GetCurvesAsync(paramLayout, pass).ConfigureAwait(false);
        }

        PassParams packed = new(pass.Name, pass.Params, version, layout.Value, curves, assets.Default<Curve>());

        return new Loaded(pass.Pass, pass.Name, body, packed);
    }

    /// <summary>
    /// The curves a pass's params name for its <c>Curve</c> members, the default curve for one they leave out, loaded;
    /// one that does not load is said, and packs as nothing.
    /// </summary>
    private async Task<Dictionary<Handle<Curve>, Curve>> GetCurvesAsync(ParamLayout layout, PipelinePass pass)
    {
        Dictionary<Handle<Curve>, Curve> curves = [];
        foreach (ParamField field in layout.Fields)
        {
            Handle<Curve> handle = field.Type == ParamType.Curve
                ? GetCurve(pass.Params.GetValueOrDefault(field.Name) ?? field.Default)
                : Handle<Curve>.None;

            if (handle == Handle<Curve>.None || curves.ContainsKey(handle)) continue;

            Result<Curve> loaded = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
            if (!loaded.Ok)
            {
                Debugging.Error($"Pass {pass.Name}'s {field.Name} has no curve: {loaded.Error}");

                continue;
            }

            curves[handle] = loaded.Value;
        }

        return curves;
    }

    /// <summary>
    /// The curve a param names, the default curve for none.
    /// </summary>
    private Handle<Curve> GetCurve(MaterialParam param)
        => param.Curve == Handle<Curve>.None ? assets.Default<Curve>() : param.Curve;

    /// <summary>
    /// Compiles a pass's own shaders and makes its pipeline on the render thread; a draw of surfaces, the sky, pieces
    /// or tiles keeps its vertex stages and makes its pipelines as their variants come, those it draws now first. What
    /// fails is said once, and the pass does not run.
    /// </summary>
    private async Task CompileAsync(
        Listing listing,
        ListedPass pass,
        Task<Result<GpuShader>> fullscreen,
        Task<Result<GpuShader>> pieces,
        Task<Result<GpuShader>> places,
        Task<Result<GpuShader>> tiles)
    {
        Func<Result<GpuPipeline>> make;
        if (pass.Pass.Kind == PassKind.Script)
        {
            Result<IPipelineScript> script = await ScriptAsync(pass).ConfigureAwait(false);
            make = () =>
            {
                pass.Script = script.Ok ? script.Value : null;

                return script.Ok ? Result.Success(GpuPipeline.None) : Result.Failure<GpuPipeline>(script.Error);
            };
        }
        else if (pass.Pass.Kind == PassKind.Compute)
        {
            Result<GpuShader> shader = await CompileBoundAsync(pass, GpuStage.Compute, GetPassBody(pass), pass.Name)
                .ConfigureAwait(false);
            make = () =>
            {
                if (!shader.Ok) return Result.Failure<GpuPipeline>(shader.Error);

                pass.Threads = (shader.Value.ThreadCountX, shader.Value.ThreadCountY, shader.Value.ThreadCountZ);

                return gpu.CreatePipeline(shader.Value);
            };
        }
        else if (!pass.DrawsSurfaces)
        {
            Result<GpuPipelineDescription> desc = pass.Pass.Kind == PassKind.Fullscreen
                ? await FullscreenAsync(pass, await fullscreen.ConfigureAwait(false)).ConfigureAwait(false)
                : await DepthAsync(pass).ConfigureAwait(false);
            make = () => desc.Ok ? gpu.CreatePipeline(desc.Value) : Result.Failure<GpuPipeline>(desc.Error);
        }
        else
        {
            PassDrawn drawn = pass.Pass.Draw.Drawn;
            Task<Result<GpuShader>> vertexShader = (pass.DrawsSky, drawn) switch
            {
                (true, _) => fullscreen,
                (false, PassDrawn.Surfaces) => tiles,
                _ => pieces,
            };
            Result<GpuShader> vertex = await vertexShader.ConfigureAwait(false);
            Result<GpuShader> place = await places.ConfigureAwait(false);
            make = () =>
            {
                pass.Vertex = vertex.Ok ? vertex.Value : null;
                pass.PlaceVertex = place.Ok ? place.Value : null;

                return vertex.Ok ? Result.Success(GpuPipeline.None) : Result.Failure<GpuPipeline>(vertex.Error);
            };
        }

        SurfaceKey[] variants = await threads.InvokeAsync(
            EngineThread.Render,
            () =>
            {
                listing.Take(pass, make());

                return pass.DrawsSurfaces ? [.. pass.Surfaces.Keys] : Array.Empty<SurfaceKey>();
            },
            stopping).ConfigureAwait(false);
        foreach (SurfaceKey variant in variants)
        {
            CompileSurfaceAsync(pass, variant, isCounted: true).FireAndForget();
        }
    }

    /// <summary>
    /// A script pass's script, made for this listing: the pass's script asset loaded, its class made with the services
    /// it asks for. Why not, when there is no scripting gem, the asset does not load, the class cannot be made, or it
    /// is not a pipeline script.
    /// </summary>
    private async Task<Result<IPipelineScript>> ScriptAsync(ListedPass pass)
    {
        if (scripts is null)
            return Result.Failure<IPipelineScript>("a script pass needs a scripting gem, and the project lists none.");

        Result<Script> asset = await assets.LoadAsync(pass.Pass.Script, stopping).ConfigureAwait(false);

        if (!asset.Ok) return Result.Failure<IPipelineScript>($"its script does not load: {asset.Error}");

        Result<IScript> made = scripts.Make(asset.Value);

        if (!made.Ok)
            return Result.Failure<IPipelineScript>($"its script {asset.Value.Type.Name} cannot be made: {made.Error}");

        if (made.Value is not IPipelineScript script)
        {
            (made.Value as IDisposable)?.Dispose();

            return Result.Failure<IPipelineScript>($"its script {asset.Value.Type.Name} is no IPipelineScript.");
        }

        return Result.Success(script);
    }

    private async Task<Result<GpuPipelineDescription>> FullscreenAsync(ListedPass pass, Result<GpuShader> vertex)
    {
        Result<GpuShader> fragment = await CompileBoundAsync(pass, GpuStage.Fragment, GetPassBody(pass), pass.Name)
            .ConfigureAwait(false);

        if (!vertex.Ok || !fragment.Ok)
            return Result.Failure<GpuPipelineDescription>(vertex.Ok ? fragment.Error : vertex.Error);

        return Result.Success(new GpuPipelineDescription
        {
            Vertex = vertex.Value,
            Fragment = fragment.Value,
            ColorFormats = [pass.TextureWrites[0].Info.Format],
            Blends = [pass.Pass.Writes[0].Blend],
            Cull = GpuCull.None
        });
    }

    /// <summary>
    /// A depth draw: the pass's vertex shader over the pieces a culling pass made, its reads bound to the vertex stage
    /// after the renderer's geometry, and the empty fragment stage that only writes depth. Both faces are drawn, so a
    /// mesh open on one side still casts, and depth is clamped rather than clipped.
    /// </summary>
    private async Task<Result<GpuPipelineDescription>> DepthAsync(ListedPass pass)
    {
        Result<GpuShader> vertex = await CompileBoundAsync(pass, GpuStage.Vertex, GetPassBody(pass), pass.Name)
            .ConfigureAwait(false);
        Result<GpuShader> compiled = await compiler.CompileAsync(
            DepthOnlyPath,
            GpuStage.Fragment,
            gpu.ShaderFormat,
            stopping).ConfigureAwait(false);

        // The fragment stage is written by hand to bind nothing, as what only writes depth needs nothing.
        Result<GpuShader> fragment = compiled.Ok ? new StageBindings().Check(compiled.Value, pass.Name) : compiled;

        if (!vertex.Ok || !fragment.Ok)
            return Result.Failure<GpuPipelineDescription>(vertex.Ok ? fragment.Error : vertex.Error);

        PassDraw draw = pass.Pass.Draw;

        return Result.Success(new GpuPipelineDescription
        {
            Vertex = vertex.Value,
            Fragment = fragment.Value,
            VertexBuffers = [new GpuVertexBuffer(0, PieceBytes, PerInstance: true)],
            VertexInputs = _pieceInputs,
            DepthFormat = GpuFormat.D32Float,
            DepthCompare = draw.DepthCompare,
            DepthBias = draw.DepthBias,
            DepthBiasSlope = draw.DepthBiasSlope,
            DepthBiasClamp = draw.DepthBiasClamp,
            DepthClip = false,
            Cull = GpuCull.None,
        });
    }

    /// <summary>
    /// A surface pipeline of a draw pass: the variant's surface composed between the pass's bindings and its template.
    /// A surface that does not compile is broken (<see cref="FailureHandler.Break"/>), so its materials are drawn as
    /// the failure material; one not loaded yet is asked for again next frame. <paramref name="isCounted"/> when the
    /// listing waits for it.
    /// </summary>
    private async Task CompileSurfaceAsync(ListedPass pass, SurfaceKey variant, bool isCounted)
    {
        Result<GpuPipelineDescription> desc = await SurfaceAsync(pass, variant).ConfigureAwait(false);
        await threads.InvokeAsync(
            EngineThread.Render,
            () =>
            {
                if (!desc.Ok && desc.Error.Length > 0) failureHandler.Break(variant.Shader, pass.Name, desc.Error);

                pass.Listing.TakeSurface(
                    pass,
                    variant,
                    desc.Ok ? gpu.CreatePipeline(desc.Value) : Result.Failure<GpuPipeline>(desc.Error),
                    isCounted);
            },
            stopping).ConfigureAwait(false);
    }

    private async Task<Result<GpuPipelineDescription>> SurfaceAsync(ListedPass pass, SurfaceKey variant)
    {
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        bool hasSurface = variant.Shader != Handle<Shader>.None;
        GpuShader? vertex = drawn == PassDrawn.Visibility && !hasSurface ? pass.PlaceVertex : pass.Vertex;

        if (vertex is null) return Result.Failure<GpuPipelineDescription>(string.Empty);

        Result<(string Text, string Name)> source = await ComposeAsync(pass, variant).ConfigureAwait(false);

        if (!source.Ok) return Result.Failure<GpuPipelineDescription>(source.Error);

        Result<GpuShader> fragment = await CompileBoundAsync(
            pass,
            GpuStage.Fragment,
            source.Value.Text,
            source.Value.Name).ConfigureAwait(false);

        if (!fragment.Ok) return Result.Failure<GpuPipelineDescription>(fragment.Error);

        return Result.Success(new GpuPipelineDescription
        {
            Vertex = vertex,
            Fragment = fragment.Value,
            ColorFormats = pass.ColorFormats,
            Blends = drawn == PassDrawn.Transparent ? [GpuBlend.Premultiplied] : [],
            VertexBuffers = pass.DrawsSky
                ? []
                : [new GpuVertexBuffer(0, pass.DrawsPieces ? PieceBytes : sizeof(uint), PerInstance: true)],
            VertexInputs = (pass.DrawsSky, pass.DrawsPieces) switch
            {
                (true, _) => [],
                (false, true) => _pieceInputs,
                (false, false) => [new GpuVertexInput(0, 0, GpuVertexFormat.Uint1, 0)],
            },
            DepthFormat = pass.Pass.Draw.Depth.Length > 0 ? GpuFormat.D32Float : GpuFormat.Invalid,
            DepthWrite = drawn == PassDrawn.Visibility,
            Cull = variant.DoubleSided || !pass.DrawsPieces ? GpuCull.None : GpuCull.Back,
        });
    }

    /// <summary>
    /// A surface pipeline's fragment shader, after the pass's bindings: what the surface and template are told of the
    /// variant, the variant's surface and its template, and the name it is compiled under.
    /// </summary>
    private async Task<Result<(string Text, string Name)>> ComposeAsync(ListedPass pass, SurfaceKey variant)
    {
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        bool hasSurface = variant.Shader != Handle<Shader>.None;
        Handle<Shader> shader = variant.Shader;
        string name = hasSurface ? assets.GetPath(shader) ?? "a surface" : "no surface";
        string surface = string.Empty;
        if (hasSurface)
        {
            Result<SurfaceSource> source = await SurfaceSource.ReadAsync(assets, shader, stopping)
                .ConfigureAwait(false);

            if (!source.Ok) return Result.Failure<(string Text, string Name)>(source.Error);

            surface = source.Value.Text;
        }

        StringBuilder text = new();
        text.Append("#define SURFACE_MASKED ").Append(variant.Masked ? 1 : 0).Append('\n')
            .Append("#define SURFACE_DOUBLE_SIDED ").Append(variant.DoubleSided ? 1 : 0).Append('\n')
            .Append("#define SURFACE_FORWARD ").Append(drawn == PassDrawn.Transparent ? 1 : 0).Append('\n')
            .Append("#define SKY_FALLBACK ").Append(hasSurface ? 0 : 1).Append('\n')
            .Append("#define SKY_CUBE ").Append(pass.DrawsCube ? 1 : 0).Append('\n')
            .Append("#include \"Include/SurfaceBindings.hlsli\"\n")
            .Append("#line 1 \"").Append(name).Append("\"\n").Append(surface).Append('\n')
            .Append("#include \"Templates/").Append(GetTemplate(drawn, hasSurface)).Append(".frag.hlsl\"\n");

        return Result.Success((text.ToString(), $"{pass.Name} with {name}"));
    }

    /// <summary>
    /// Finds what each stage of a pass uses, before it is compiled bound to that: its own shader's; a draw of surfaces'
    /// templates' over the default surface, with what the surface contract offers any surface; the pieces' vertex
    /// stage's for a draw of pieces, probed once for all of them; and nothing for the
    /// tiles' and the fullscreen triangle's, and for the fragment stage that only writes depth, which bind nothing. Why
    /// not is kept with the pass, and fails its pipelines.
    /// </summary>
    private async Task ProbePassAsync(ListedPass pass, Task<Result<StageBindings>> pieces)
    {
        StageBindings none = new();
        if (pass.Pass.Kind == PassKind.Script)
        {
            pass.SetBound(GpuStage.Vertex, none);
            pass.SetBound(GpuStage.Fragment, none);

            return;
        }

        if (pass.Pass.Kind == PassKind.Compute)
        {
            Keep(
                pass,
                GpuStage.Compute,
                await ProbeAsync(pass, GpuStage.Compute, [(GetPassBody(pass), pass.Name)]).ConfigureAwait(false));

            return;
        }

        if (pass.Pass.Kind == PassKind.Fullscreen)
        {
            pass.SetBound(GpuStage.Vertex, none);
            Keep(
                pass,
                GpuStage.Fragment,
                await ProbeAsync(pass, GpuStage.Fragment, [(GetPassBody(pass), pass.Name)]).ConfigureAwait(false));

            return;
        }

        if (pass.Pass.Draw.Drawn == PassDrawn.Depth)
        {
            pass.SetBound(GpuStage.Fragment, none);
            Keep(
                pass,
                GpuStage.Vertex,
                await ProbeAsync(pass, GpuStage.Vertex, [(GetPassBody(pass), pass.Name)]).ConfigureAwait(false));

            return;
        }

        Task<Result<StageBindings>> templates = ProbeTemplatesAsync(pass);
        Keep(pass, GpuStage.Vertex, pass.DrawsPieces ? await pieces.ConfigureAwait(false) : Result.Success(none));
        Keep(pass, GpuStage.Fragment, await templates.ConfigureAwait(false));
    }

    /// <summary>
    /// What a draw of surfaces binds in its fragment stage: the union over its templates, each over the default
    /// surface, whose every value comes from the materials, so no path of the template folds away as it would over a
    /// surface of constants, and the visibility buffer's also with no surface; the sky's, its shadow's and its air's
    /// with no sky alone, as a sky reads only the frame's constants and what the contract offers; and what the surface
    /// contract offers any surface or sky (Shaders/Include/SurfaceBindings.hlsli), the texture pools, the materials
    /// and, for a forward surface, the scene behind, whether the probe used them or not.
    /// </summary>
    private async Task<Result<StageBindings>> ProbeTemplatesAsync(ListedPass pass)
    {
        SurfaceKey surface = new(assets.Default<Shader>(), false, false);
        SurfaceKey none = new(Handle<Shader>.None, false, false);
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        SurfaceKey[] variants = (pass.DrawsSky, drawn) switch
        {
            (true, _) => [none],
            (false, PassDrawn.Visibility) => [surface, none],
            _ => [surface],
        };
        List<(string Text, string Name)> sources = [];
        foreach (SurfaceKey variant in variants)
        {
            Result<(string Text, string Name)> source = await ComposeAsync(pass, variant).ConfigureAwait(false);

            if (!source.Ok) return Result.Failure<StageBindings>(source.Error);

            sources.Add(source.Value);
        }

        List<GpuShaderBinding> contract = [new GpuShaderBinding(
            GpuContract.Materials.Name,
            GpuBindingKind.StorageBuffer,
            0)];
        foreach (ResourceInfo pool in GpuContract.TexturePools)
        {
            contract.Add(new GpuShaderBinding(pool.Name, GpuBindingKind.Sampled, 0));
        }

        if (drawn == PassDrawn.Transparent
            && pass.Visible.Exists(info => string.Equals(info.Name, "SceneColor", StringComparison.Ordinal)))
        {
            contract.Add(new GpuShaderBinding("SceneColor", GpuBindingKind.Sampled, 0));
        }

        return await ProbeAsync(pass, GpuStage.Fragment, sources, contract).ConfigureAwait(false);
    }

    /// <summary>
    /// What a stage's shaders use of what <paramref name="visible"/> and <paramref name="writes"/> declare: each
    /// compiled to SPIR-V with every name declared, all at once, what it bound read back, and the union resolved
    /// (<see cref="StageBindings.TryCreate"/>) with whatever is <paramref name="also"/> bound.
    /// </summary>
    private async Task<Result<StageBindings>> ProbeAsync(
        IReadOnlyList<ResourceInfo> visible,
        IReadOnlyList<PassBinding> writes,
        GpuStage stage,
        IReadOnlyList<(string Text, string Name)> sources,
        IReadOnlyList<GpuShaderBinding>? also = null)
    {
        string header = StageBindings.MakeHeader(stage, visible, writes, null);
        List<Task<Result<GpuShader>>> probes = new(sources.Count);
        foreach ((string text, string name) in sources)
        {
            probes.Add(compiler.CompileAsync(header + text, name, stage, GpuShaderFormat.Spirv, stopping));
        }

        List<GpuShaderBinding> used = [.. also ?? []];
        foreach (Result<GpuShader> probe in await Task.WhenAll(probes).ConfigureAwait(false))
        {
            if (!probe.Ok) return Result.Failure<StageBindings>(probe.Error);

            used.AddRange(probe.Value.Bindings);
        }

        return StageBindings.TryCreate(used, visible, writes);
    }

    private Task<Result<StageBindings>> ProbeAsync(
        ListedPass pass,
        GpuStage stage,
        IReadOnlyList<(string Text, string Name)> sources,
        IReadOnlyList<GpuShaderBinding>? also = null)
        => ProbeAsync(pass.Visible, pass.GetWrites(stage), stage, sources, also);

    /// <summary>
    /// A shader compiled for the GPU, bound to what its stage binds: every name declared, those it binds at their
    /// slots, and the shader checked to use no others.
    /// </summary>
    private async Task<Result<GpuShader>> CompileBoundAsync(
        IReadOnlyList<ResourceInfo> visible,
        IReadOnlyList<PassBinding> writes,
        Result<StageBindings> bound,
        GpuStage stage,
        string text,
        string name)
    {
        if (!bound.Ok) return Result.Failure<GpuShader>(bound.Error);

        Result<GpuShader> compiled = await compiler.CompileAsync(
            StageBindings.MakeHeader(stage, visible, writes, bound.Value) + text,
            name,
            stage,
            gpu.ShaderFormat,
            stopping).ConfigureAwait(false);

        return compiled.Ok ? bound.Value.Check(compiled.Value, name) : compiled;
    }

    private Task<Result<GpuShader>> CompileBoundAsync(ListedPass pass, GpuStage stage, string text, string name)
    {
        StageBindings? bound = pass.GetBindings(stage);

        return CompileBoundAsync(
            pass.Visible,
            pass.GetWrites(stage),
            bound is null ? Result.Failure<StageBindings>(pass.ProbeError) : Result.Success(bound),
            stage,
            text,
            name);
    }

    private async Task<Result<string>> TextOfAsync(Handle<Shader> shader)
    {
        Result<Shader> loaded = await assets.LoadAsync(shader, stopping).ConfigureAwait(false);

        return loaded.Ok ? Result.Success(loaded.Value.Text) : Result.Failure<string>(loaded.Error);
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
            ResourceScope set = created.Info.IsFrame ? scope.Resources.Frame : scope.View ?? scope.Resources.Frame;
            bool isMade = set.MakeIfChanged(create.Name, GetKey(created, scope));
            if (isMade && create.History
                && set.TryGet(ResourceInfo.GetHistoryName(create.Name), out BoundResource history)
                && history.Texture != GpuTexture.None)
            {
                gpu.Commands.RenderPass([new GpuTextureLevel(history.Texture)], GpuContents.Clear).Dispose();
            }

            if (create.Seed.Length == 0 || !set.TryGet(create.Name, out BoundResource made)
                || !TryGetResource(scope, create.Seed, out BoundResource seed))
            {
                continue;
            }

            uint slice = GetSliceBytes(created, scope.Counts);
            uint copies = created.Copies.IsNone ? 1 : Math.Max(1, scope.Counts.GetCount(created.Copies));
            for (uint copy = 0; copy < copies; copy++)
            {
                scope.Resources.Copy(seed.Buffer, 0, made.Buffer, copy * slice, Math.Min(seed.Bytes, slice));
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

        if (pass.Pass.Kind is not (PassKind.Fullscreen or PassKind.Draw)) return;

        using GpuDebugGroup group = gpu.Commands.DebugGroup(pass.Name);
        Contexts.Assets.Color color = FailureHandler.GetPulseColor(scope.Constants.Time);
        uint iterations = GetIterations(pass, scope.Counts);
        foreach (PassBinding write in pass.Pass.Kind == PassKind.Fullscreen ? pass.TextureWrites : GetColors(pass))
        {
            if (!TryGetResource(scope, write.Info.Name, out BoundResource target)) continue;

            for (uint iteration = 0; iteration < iterations; iteration++)
            {
                gpu.Commands.RenderPass(
                    [new GpuTextureLevel(target.Texture, write.Level, GetLayer(pass, write, iteration))],
                    GpuContents.Clear,
                    color).Dispose();
            }
        }
    }

    private bool TryRecordCompute(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        if (pass.GetBindings(GpuStage.Compute) is not { } bound) return false;

        Span<GpuTextureLevel> textureWrites = stackalloc GpuTextureLevel[StageBindings.MaxStorage];
        int textureCount = 0;
        foreach (BoundName write in bound.TextureWrites)
        {
            if (!TryGetResource(scope, write.Binding.Info.Name, out BoundResource written)) return false;

            textureWrites[textureCount++] = new GpuTextureLevel(
                written.Texture,
                write.Binding.Level,
                GetLayer(pass, write.Binding, iteration));
        }

        Span<GpuBuffer> bufferWrites = stackalloc GpuBuffer[StageBindings.MaxStorage];
        int bufferCount = 0;
        foreach (BoundName write in bound.BufferWrites)
        {
            if (!TryGetResource(scope, write.Binding.Info.Name, out BoundResource written)) return false;

            bufferWrites[bufferCount++] = written.Buffer;
        }

        Reads reads = default;

        if (!reads.TryGather(bound, scope)) return false;

        BoundResource args = default;
        bool isIndirect = pass.Pass.Dispatch.Args.Length > 0;

        if (isIndirect && !TryGetResource(scope, pass.Pass.Dispatch.Args, out args)) return false;

        using GpuComputePass compute = gpu.Commands
            .ComputePass(textureWrites[..textureCount], bufferWrites[..bufferCount]);
        compute.Constants(constants).Pipeline(pass.Pipeline);
        reads.Bind(compute, bound);
        if (isIndirect)
        {
            compute.DispatchFromBuffer(args.Buffer, (pass.Pass.Dispatch.First + iteration) * 3 * sizeof(uint));
        }
        else
        {
            (uint x, uint y, uint z) = GetGroups(pass, scope);
            compute.Dispatch(x, y, z);
        }

        return true;
    }

    private bool TryRecordFullscreen(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassBinding write = pass.TextureWrites[0];
        Reads reads = default;

        if (pass.GetBindings(GpuStage.Fragment) is not { } bound
            || !TryGetResource(scope, write.Info.Name, out BoundResource target)
            || !reads.TryGather(bound, scope))
        {
            return false;
        }

        Rectangle area = new(
            0,
            0,
            (int)Math.Max(1, target.Width >> (int)write.Level),
            (int)Math.Max(1, target.Height >> (int)write.Level));
        using GpuRenderPass render = gpu.Commands.RenderPass(
            [new GpuTextureLevel(target.Texture, write.Level, GetLayer(pass, write, iteration))],
            write.Contents);
        render.Viewport(area).Scissor(area).Pipeline(pass.Pipeline).Constants(GpuStage.Fragment, constants);
        reads.Bind(render, GpuStage.Fragment, bound);
        render.Draw(3);

        return true;
    }

    /// <summary>
    /// A script pass: its script records into the one texture it writes, the target itself in a target's stage, told
    /// the texture's format and encoding, which its pipelines are made for.
    /// </summary>
    private bool TryRecordScript(ListedPass pass, in RecordScope scope)
    {
        PassBinding write = pass.TextureWrites[0];

        if (pass.Script is not { } script || !TryGetResource(scope, write.Info.Name, out BoundResource target))
            return false;

        bool isTarget = write.Info.IsTarget;
        Rectangle area = new(0, 0, (int)Math.Max(1, target.Width), (int)Math.Max(1, target.Height));
        script.Record(new PipelineScriptContext(
            gpu.Commands,
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
    private bool TryRecordDraw(ListedPass pass, in RecordScope scope, in FrameConstants constants, uint iteration)
    {
        PassDraw draw = pass.Pass.Draw;
        uint layer = pass.Each.IsNone ? 0 : iteration;
        Span<GpuTextureLevel> colors = stackalloc GpuTextureLevel[8];
        Size size = Size.Empty;
        for (int i = 0; i < draw.Colors.Count; i++)
        {
            if (!TryGetResource(scope, draw.Colors[i], out BoundResource color)) return false;

            colors[i] = new GpuTextureLevel(color.Texture, 0, layer);
            size = new Size((int)color.Width, (int)color.Height);
        }

        BoundResource depth = default;

        if (draw.Depth.Length > 0 && !TryGetResource(scope, draw.Depth, out depth)) return false;

        BoundResource args = default;
        BoundResource instances = default;

        if (pass.DrawsIndirect
            && (!TryGetResource(scope, draw.Args, out args) || !TryGetResource(scope, draw.Instances, out instances)))
        {
            return false;
        }

        Reads vertexReads = default;
        Reads fragmentReads = default;

        if (pass.GetBindings(GpuStage.Vertex) is not { } vertexBound
            || pass.GetBindings(GpuStage.Fragment) is not { } fragmentBound
            || !vertexReads.TryGather(vertexBound, scope)
            || !fragmentReads.TryGather(fragmentBound, scope))
        {
            return false;
        }

        size = size.IsEmpty ? new Size((int)depth.Width, (int)depth.Height) : size;
        Rectangle area = new(Point.Empty, size);
        using GpuRenderPass render = gpu.Commands.RenderPass(
            colors[..draw.Colors.Count],
            draw.Contents,
            default,
            new GpuTextureLevel(depth.Texture, 0, layer));
        render.Viewport(area).Scissor(area).Constants(GpuStage.Vertex, constants)
            .Constants(GpuStage.Fragment, constants);
        vertexReads.Bind(render, GpuStage.Vertex, vertexBound);
        fragmentReads.Bind(render, GpuStage.Fragment, fragmentBound);
        switch (draw.Drawn)
        {
            case PassDrawn.Depth:
                BindPieces(render, instances.Buffer, scope.Resources)
                    .Pipeline(pass.Pipeline)
                    .DrawIndexedFromBuffer(
                        args.Buffer,
                        (uint)((draw.First + iteration) * GpuTable<GpuDrawArgs>.RowBytes),
                        1);
                break;
            case PassDrawn.Visibility:
                Visibility(render, pass, args, instances, scope.Resources);
                break;
            case PassDrawn.Surfaces:
                Resolve(render, pass, args, instances, scope.Resources);
                break;
            case PassDrawn.Transparent:
                Transparent(render, pass, scope);
                break;
            case PassDrawn.Sky or PassDrawn.SkyShadow or PassDrawn.SkyAir:
                Sky(render, pass, scope);
                break;
            default:
                break;
        }

        return true;
    }

    /// <summary>
    /// The visibility buffer: each raster bin of the frame's surface variants with its pipeline, from the arguments the
    /// bins' pass counted into. The two fixed bins draw what does not clip, single and double sided; then each
    /// surface's bin of clusters and its bin of cards, which take six indices, not a piece's 96, both with its
    /// pipeline.
    /// </summary>
    private void Visibility(
        GpuRenderPass render,
        ListedPass pass,
        BoundResource args,
        BoundResource instances,
        GpuResources resources)
    {
        BindPieces(render, instances.Buffer, resources);
        int bin = 0;
        foreach (SurfaceKey key in _variants.Raster)
        {
            DrawBin(key, bin);
            if (bin >= GpuContract.FixedRasterBins)
                DrawBin(key, GpuContract.CardBins + bin - GpuContract.FixedRasterBins);

            bin++;
        }

        void DrawBin(SurfaceKey key, int bin)
        {
            GpuPipeline pipeline = GetSurfacePipeline(pass, key);

            if (pipeline == GpuPipeline.None) return;

            render.Pipeline(pipeline).DrawIndexedFromBuffer(
                args.Buffer,
                (uint)((pass.Pass.Draw.First + bin) * GpuTable<GpuDrawArgs>.RowBytes),
                1);
        }
    }

    /// <summary>
    /// The G-buffer: each resolve bin's tiles with its surface's pipeline, from the arguments the classifying pass
    /// counted.
    /// </summary>
    private void Resolve(
        GpuRenderPass render,
        ListedPass pass,
        BoundResource args,
        BoundResource tiles,
        GpuResources resources)
    {
        BindPieces(render, tiles.Buffer, resources);
        int bin = 0;
        foreach (SurfaceKey key in _variants.Resolve)
        {
            GpuPipeline pipeline = GetSurfacePipeline(pass, key);
            if (pipeline != GpuPipeline.None)
            {
                render.Pipeline(pipeline)
                .DrawIndexedFromBuffer(args.Buffer, (uint)(bin * GpuTable<GpuDrawArgs>.RowBytes), 1);
            }

            bin++;
        }
    }

    /// <summary>
    /// The world's sky, its material's sky shader or the renderer's own, over what nothing was drawn on, or its shadow
    /// or its air over the whole target.
    /// </summary>
    private void Sky(GpuRenderPass render, ListedPass pass, in RecordScope scope)
    {
        GpuPipeline sky = GetSurfacePipeline(pass, scope.Sky);
        if (sky != GpuPipeline.None) render.Pipeline(sky).Draw(3);
    }

    /// <summary>
    /// The view's transparent pieces, farthest instance first, a run of one surface at a time.
    /// </summary>
    private void Transparent(GpuRenderPass render, ListedPass pass, in RecordScope scope)
    {
        if (scope.TransparentRuns.Count == 0) return;

        BindPieces(render, scope.TransparentPieces, scope.Resources);
        GpuPipeline bound = GpuPipeline.None;
        foreach (TransparentRun run in scope.TransparentRuns)
        {
            GpuPipeline pipeline = GetSurfacePipeline(pass, run.Surface);

            if (pipeline == GpuPipeline.None) continue;

            if (pipeline != bound)
            {
                render.Pipeline(pipeline);
                bound = pipeline;
            }

            render.DrawIndexed(GpuResources.PieceIndexCount, 0, 0, (uint)run.Count, (uint)run.First);
        }
    }

    private void Mipmaps(ListedPass pass, in RecordScope scope)
    {
        foreach (PassBinding write in pass.TextureWrites)
        {
            if (write.Mipmaps && TryGetResource(scope, write.Info.Name, out BoundResource bound))
                gpu.Commands.GenerateMipmaps(bound.Texture);
        }

        if (pass.Pass.Kind != PassKind.Draw || !pass.Pass.Draw.Mipmaps) return;

        foreach (string color in pass.Pass.Draw.Colors)
        {
            if (TryGetResource(scope, color, out BoundResource bound)) gpu.Commands.GenerateMipmaps(bound.Texture);
        }
    }

    /// <summary>
    /// What a stage reads this frame, gathered from what it binds, each at its slot: sampled textures with the sampler
    /// each is read through, textures read with none, and buffers; false when one is not there yet.
    /// </summary>
    private ref struct Reads
    {
        private Buffer16<GpuSampledTexture> _sampled;
        private Buffer8<GpuTexture> _storage;
        private Buffer8<GpuBuffer> _buffers;

        internal bool TryGather(StageBindings bound, in RecordScope scope)
        {
            foreach (BoundName read in bound.Sampled)
            {
                if (!TryGetResource(scope, read.Binding.Info.Name, out BoundResource resource)) return false;

                _sampled[(int)read.Slot] = new GpuSampledTexture(
                    resource.Texture,
                    GetSampler(read.Binding.Info, resource, scope.Resources));
            }

            foreach (BoundName read in bound.StorageReads)
            {
                if (!TryGetResource(scope, read.Binding.Info.Name, out BoundResource resource)) return false;

                _storage[(int)read.Slot] = resource.Texture;
            }

            foreach (BoundName read in bound.Buffers)
            {
                if (!TryGetResource(scope, read.Binding.Info.Name, out BoundResource resource)) return false;

                _buffers[(int)read.Slot] = resource.Buffer;
            }

            return true;
        }

        internal void Bind(GpuRenderPass render, GpuStage stage, StageBindings bound)
        {
            if (bound.Sampled.Count > 0) render.Textures(stage, _sampled[..bound.Sampled.Count]);

            if (bound.StorageReads.Count > 0) render.RawTextures(stage, _storage[..bound.StorageReads.Count]);

            if (bound.Buffers.Count > 0) render.Buffers(stage, _buffers[..bound.Buffers.Count]);
        }

        internal void Bind(GpuComputePass compute, StageBindings bound)
        {
            if (bound.Sampled.Count > 0) compute.Textures(_sampled[..bound.Sampled.Count]);

            if (bound.StorageReads.Count > 0) compute.RawTextures(_storage[..bound.StorageReads.Count]);

            if (bound.Buffers.Count > 0) compute.Buffers(_buffers[..bound.Buffers.Count]);
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
/// A pass loaded for a build: its file, its name, its shader's text as HLSL takes it, its params' loader in, and its
/// params packed.
/// </summary>
internal sealed record Loaded(Pass Pass, string Name, string Body, PassParams Params);
