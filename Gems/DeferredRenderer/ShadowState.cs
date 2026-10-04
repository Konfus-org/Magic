using Magic.Contexts.Rendering;
using Magic.Contexts.Settings;
using Magic.Extensions;
using Magic.Interfaces;
using System.Drawing;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// One shadow view to draw this frame: a cascade of the sun or a face of a local light, its cull constants (whose frame
/// block its draw pushes), its tile of the atlas and the index of the draw buffers it culls into.
/// </summary>
internal readonly record struct ShadowView(ShadowCullConstants Cull, Rectangle Tile, int Buffers);

/// <summary>
/// A spot or point light holding pages of the atlas: how it is told apart from frame to frame, its faces, their pages
/// and when they were last drawn.
/// </summary>
internal sealed class LocalShadow
{
    public required LocalKey Key { get; init; }

    public required int[] Pages { get; init; }

    public long LastDrawn { get; set; } = -1;

    /// <summary>
    /// Set while the light is among this frame's shadow casters; a light that is not is let go.
    /// </summary>
    public bool Used { get; set; }
}

/// <summary>
/// What tells one local light from another between frames: its kind, where it is and points, and its reach. A light
/// that moves gets a new key, so its pages are drawn again at once.
/// </summary>
internal readonly record struct LocalKey(LightKind Kind, Vector3 Position, Vector3 Forward, float Range, float OuterAngle)
{
    public static LocalKey Of(in LightInstance light)
    {
        return new LocalKey(light.Kind, Quantize(light.World.Translation, 1000f), Quantize(Vector3.Normalize(light.World.Forward), 1000f), light.Range, light.OuterAngle);
    }

    private static Vector3 Quantize(Vector3 value, float steps)
    {
        return new Vector3(MathF.Round(value.X * steps), MathF.Round(value.Y * steps), MathF.Round(value.Z * steps));
    }
}

/// <summary>
/// The shadow maps' state: the one depth atlas every shadow view draws its tile into (the sun's cascades in a row per
/// perspective view, then the pages of the local lights), its pipelines and comparison sampler, the record buffer the
/// lighting finds a local light's faces through, the local lights holding pages, and the views drawn this frame with
/// the buffers they cull into. Built with the render context; the atlas is made again when the settings resize it.
/// </summary>
internal sealed class ShadowState
{
    public const string CullShader = "Cull/CullShadow.comp.hlsl";
    public const string VertexShader = "Shadows/Shadow.vert.hlsl";
    public const string FragmentShader = "Shadows/Shadow.frag.hlsl";
    public const string ClearShader = "Shadows/ClearDepth.vert.hlsl";

    public ShadowState(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        Comparison = gpu.CreateSampler(new SamplerDesc(GpuFilter.Linear, GpuAddress.Clamp, Compare: GpuCompare.GreaterOrEqual));
        Stub = gpu.CreateTexture(new TextureDesc(gpu.DepthFormat, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler, 1, 1));
        Records = new GrowableBuffer(gpu, GpuBufferUsage.ComputeRead, 16 * GpuShadowRecord.Size);
        Pipelines = ShadowPipelines.Create(ctx);
    }

    public GpuSampler Comparison { get; }

    /// <summary>
    /// A 1 x 1 depth texture, cleared once, bound in place of the atlas while nothing casts: the lighting's binding is
    /// always there.
    /// </summary>
    public GpuTexture Stub { get; }

    public bool StubCleared { get; set; }

    public GpuTexture Atlas { get; set; }

    public int AtlasWidth { get; set; }

    public int AtlasHeight { get; set; }

    /// <summary>
    /// Rows of cascade tiles at the top of the atlas (one per perspective view) and the page grid under them.
    /// </summary>
    public int CascadeRows { get; set; }

    public int CascadeResolution { get; set; }

    public int PageSize { get; set; }

    public int PageColumns { get; set; }

    public int PageCount { get; set; }

    public GrowableBuffer Records { get; }

    public List<GpuShadowRecord> RecordRows { get; } = [];

    public ShadowPipelines Pipelines { get; set; }

    /// <summary>
    /// Per light of this frame's light list, the index of its first record, or <see cref="None"/>.
    /// </summary>
    public List<uint> LightRecords { get; } = [];

    public List<uint> LightFaces { get; } = [];

    public Dictionary<LocalKey, LocalShadow> Locals { get; } = [];

    public Stack<int> FreePages { get; } = [];

    public List<ShadowView> Views { get; } = [];

    /// <summary>
    /// The draw args and visible-id buffers the shadow views cull into, one pair per view this frame, kept between frames.
    /// </summary>
    public List<(GrowableBuffer DrawArgs, GrowableBuffer VisibleIds)> Buffers { get; } = [];

    /// <summary>
    /// How many perspective views were given a cascade row this frame.
    /// </summary>
    public int RowsUsed { get; set; }

    /// <summary>
    /// Set when some tile of the atlas keeps last frame's depth: the atlas is then loaded and the drawn tiles cleared one by one.
    /// </summary>
    public bool KeptAny { get; set; }

    public long Frame { get; set; }

    public const uint None = uint.MaxValue;

    public Rectangle Page(int page)
    {
        return new Rectangle((page % PageColumns) * PageSize, (CascadeRows * CascadeResolution) + ((page / PageColumns) * PageSize), PageSize, PageSize);
    }

    public Vector4 RectUv(Rectangle rect)
    {
        return new Vector4((float)rect.X / AtlasWidth, (float)rect.Y / AtlasHeight, (float)rect.Width / AtlasWidth, (float)rect.Height / AtlasHeight);
    }

    /// <summary>
    /// The atlas for these settings and this many perspective views; made again when its layout changed, which drops
    /// every local light's pages (drawn again next frame).
    /// </summary>
    public void EnsureAtlas(IRendering gpu, ShadowSettings settings, int perspectiveViews)
    {
        int rows = Math.Max(1, perspectiveViews);
        int pageCount = settings.MaxLocalLights * LocalShadows.PointFaces;
        int width = Math.Max(settings.Cascades * settings.CascadeResolution, settings.LocalResolution);
        int columns = Math.Max(1, width / settings.LocalResolution);
        int pageRows = (pageCount + columns - 1) / columns;
        int height = (rows * settings.CascadeResolution) + (pageRows * settings.LocalResolution);
        if (Atlas.IsValid && width == AtlasWidth && height == AtlasHeight && rows == CascadeRows && settings.CascadeResolution == CascadeResolution && settings.LocalResolution == PageSize)
            return;

        gpu.Release(Atlas);
        Atlas = gpu.CreateTexture(new TextureDesc(gpu.DepthFormat, GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler, (uint)width, (uint)height));
        AtlasWidth = width;
        AtlasHeight = height;
        CascadeRows = rows;
        CascadeResolution = settings.CascadeResolution;
        PageSize = settings.LocalResolution;
        PageColumns = columns;
        PageCount = pageCount;

        Locals.Clear();
        FreePages.Clear();
        for (int page = pageCount - 1; page >= 0; page--)
            FreePages.Push(page);
    }

    public void Release(IRendering gpu)
    {
        gpu.Release(Atlas);
        gpu.Release(Stub);
        gpu.Release(Comparison);
        gpu.Release(Records.Handle);
        Pipelines.Release(gpu);
        foreach ((GrowableBuffer drawArgs, GrowableBuffer visibleIds) in Buffers)
        {
            gpu.Release(drawArgs.Handle);
            gpu.Release(visibleIds.Handle);
        }
    }
}

/// <summary>
/// The shadow pipelines: the compute cull of one shadow view, the one depth-only pipeline every opaque class is drawn
/// with, and the one that clears a tile. Built again when their shaders change or the bias settings do.
/// </summary>
internal sealed record ShadowPipelines(GpuPipeline Cull, GpuPipeline Depth, GpuPipeline Clear, float SlopeBias)
{
    // The vertex position from the fixed vertex layout in slot 0, the instance slot from the instance-rate buffer in slot 1.
    private static readonly VertexBufferLayout[] VertexBuffers = [new(0, Magic.Contexts.Assets.Vertex.Size), new(1, GpuVisible.Size, PerInstance: true)];

    private static readonly VertexAttribute[] VertexAttributes = [new(0, 0, GpuVertexFormat.Float3, 0), new(1, 1, GpuVertexFormat.Uint, 0)];

    public static ShadowPipelines Create(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        ShadowSettings settings = ctx.Settings.Shadows;
        string lodBlend = FormattableString.Invariant($"#define LOD_BLEND {Culling.LodBlend:0.0#####}\n");
        GpuPipeline cull = gpu.CreateComputePipeline(Shaders.CompileBuiltIn(ctx, ShadowState.CullShader, GpuStage.Compute, lodBlend));
        CompiledShader fragment = Shaders.CompileBuiltIn(ctx, ShadowState.FragmentShader, GpuStage.Fragment);
        CompiledShader vertex = Shaders.CompileBuiltIn(ctx, ShadowState.VertexShader, GpuStage.Vertex);
        CompiledShader clear = Shaders.CompileBuiltIn(ctx, ShadowState.ClearShader, GpuStage.Vertex);

        // Reverse-Z: a negative slope bias pushes the stored depth away from the light where the surface slopes. Depth
        // clip off clamps casters behind the near plane onto it instead of losing them.
        GpuPipeline depth = gpu.CreatePipeline(new PipelineDesc(vertex, fragment)
        {
            Buffers = VertexBuffers,
            Attributes = VertexAttributes,
            Depth = gpu.DepthFormat,
            Cull = GpuCull.None,
            DepthBiasSlope = -settings.SlopeBias,
            DepthClip = false,
        });
        GpuPipeline clearDepth = gpu.CreatePipeline(new PipelineDesc(clear, fragment) { Depth = gpu.DepthFormat, Cull = GpuCull.None, DepthCompare = GpuCompare.Always });
        return new ShadowPipelines(cull, depth, clearDepth, settings.SlopeBias);
    }

    public void Release(IRendering gpu)
    {
        gpu.Release(Cull);
        gpu.Release(Depth);
        gpu.Release(Clear);
    }
}
