using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.ClusteredGeometry;

/// <summary>
/// Draws the depth of the clusters that cast into each of a list of shadow views into their atlas.
/// </summary>
/// <remarks>
/// The GPU culls each instance's clusters against each view and the view's texel, and lists each that passes with its
/// view. Then one indirect draw a pipeline draws the whole list into the atlas, each cluster into its view's
/// rectangle. The cull reads the impostor handoff from the frame it is given, and the pools from the cluster constants
/// in that frame's buffer. The sun's cascades are drawn for each view, from the view's frame; the local lights' tiles
/// once a frame, from the world's.
/// </remarks>
internal sealed class ShadowPasses
{
    /// <summary>
    /// The rows of a shadow list: 12 MB.
    /// </summary>
    private const int MaxShadowClusters = 1 << 20;

    /// <summary>
    /// The draws each region of the shadow draws holds.
    /// </summary>
    private const int MaxShadowDraws = 1 << 19;

    private const int ShadowClusterBytes = 12;

    private const int DrawArgumentsBytes = 16;

    private const int CullGroupsPerRow = 65535;

    private const BufferUsages IndirectUsage = BufferUsages.Storage | BufferUsages.Indirect;

    /// <summary>
    /// The counts buffer's words: a draw count for each region, then the rows of the shadow list.
    /// </summary>
    private const int CountWords = (int)ShadowDrawRegion.Impostors + 2;

    private readonly IGraphicsApi _gpu;

    private readonly bool _isReflective;

    private readonly ShaderEntry _cullShader;

    private readonly DrawShaders _singleSidedShaders;

    private readonly DrawShaders _doubleSidedShaders;

    private readonly DrawShaders _fadingShaders;

    private readonly DrawShaders _fadingDoubleSidedShaders;

    private readonly DrawShaders _impostorShaders;

    /// <param name="assets">Where the passes' shaders are loaded from.</param>
    /// <param name="gpu">What builds the shaders and holds the buffers.</param>
    /// <param name="isReflective">
    /// Whether the passes draw a reflective shadow map: each texel's diffuse colour and normal beside its depth, and no
    /// impostors.
    /// </param>
    internal ShadowPasses(AssetManager assets, IGraphicsApi gpu, bool isReflective = false)
    {
        _gpu = gpu;
        _isReflective = isReflective;
        _cullShader = PassShaders.GetEntry(assets, "CullShadowClusters", "CullShadowClusters");
        string prefix = isReflective ? "DrawReflective" : "DrawShadow";
        ShaderEntry vertex = PassShaders.GetEntry(assets, "DrawShadows", $"{prefix}Vertex", ShaderStage.Vertex);
        _singleSidedShaders = new(
            vertex,
            PassShaders.GetEntry(assets, "DrawShadows", $"{prefix}Pixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
        };
        _doubleSidedShaders = _singleSidedShaders with { Cull = CullMode.None };
        _fadingShaders = _singleSidedShaders with
        {
            Pixel = PassShaders.GetEntry(assets, "DrawShadows", $"{prefix}FadingPixel", ShaderStage.Pixel),
        };
        _fadingDoubleSidedShaders = _fadingShaders with { Cull = CullMode.None };
        _impostorShaders = new(
            PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowImpostorVertex", ShaderStage.Vertex),
            PassShaders.GetEntry(assets, "DrawShadows", "DrawShadowImpostorPixel", ShaderStage.Pixel)
        )
        {
            Depth = DepthMode.TestAndWrite,
            Cull = CullMode.None,
        };
        Shaders =
        [
            _cullShader,
            vertex,
            _singleSidedShaders.Pixel,
            _fadingShaders.Pixel,
            _impostorShaders.Vertex,
            _impostorShaders.Pixel,
        ];
    }

    internal ImmutableArray<ShaderEntry> Shaders { get; }

    /// <summary>
    /// Culls and draws the clusters that cast into the views of <paramref name="target"/> into its atlas.
    /// </summary>
    /// <remarks>
    /// The first draw clears the atlas when the target asks for it; else every draw keeps what the atlas holds.
    /// </remarks>
    internal Result Record(RenderGraph graph, ShadowTarget target, ClusterPools pools)
    {
        ShadowPassNames names = target.Names;
        int? view = target.View;
        Result<GpuBuffer> constants = GetBuffer(new(names.Constants, Unsafe.SizeOf<ShadowCullConstants>()));
        Result<GpuBuffer> counts = GetBuffer(new(names.Counts, CountWords * sizeof(uint), IndirectUsage));
        Result<GpuBuffer> shadowClusters = GetBuffer(new(names.Clusters, (long)MaxShadowClusters * ShadowClusterBytes));
        Result<GpuBuffer> drawArguments = GetBuffer(
            new(
                names.DrawArguments,
                (long)MaxShadowDraws * ((int)ShadowDrawRegion.Impostors + 1) * DrawArgumentsBytes,
                IndirectUsage
            )
        );
        Result asked = Result.FindFailure(constants, counts, shadowClusters, drawArguments);
        if (!asked.Ok)
            return asked;

        ShadowCullConstants cull = new()
        {
            Views = target.Views.Id,
            ViewCount = (uint)target.ViewCount,
            ShadowClusters = shadowClusters.Value.Id,
            DrawArguments = drawArguments.Value.Id,
            Counts = counts.Value.Id,
            MaxShadowClusters = MaxShadowClusters,
            MaxShadowDraws = MaxShadowDraws,
        };
        Span<uint> zeros = stackalloc uint[CountWords];
        zeros.Clear();
        if (!graph.Upload(constants.Value, [cull]) || !graph.Upload(counts.Value, zeros))
            return Result.Failure("the shadow cull's constants were not staged.");

        // Cull: one group an instance, over every view. A reflective draw reads the table of diffuse colours.
        GpuBuffer frameBuffer = target.Frame;
        GpuConstants header = new(frameBuffer.Id, constants.Value.Id, target.Albedos?.Id ?? 0);
        uint across = (uint)Math.Min(target.InstanceCount, CullGroupsPerRow);
        uint down = (uint)Math.Max((target.InstanceCount + CullGroupsPerRow - 1) / CullGroupsPerRow, 1);
        graph
            .Compute(names.Cull, _cullShader, header, RenderStages.Geometry)
            .Reads(
                frameBuffer,
                constants.Value,
                target.Views,
                target.Instances,
                target.Materials,
                pools.Clusters,
                pools.Models
            )
            .Writes(counts.Value, shadowClusters.Value, drawArguments.Value)
            .Dispatch(across, down);

        // Draw: the first pass clears the atlas where the target asks, each cluster draws into its view's rectangle.
        TargetLoad first = target.IsCleared ? TargetLoad.Clear : TargetLoad.Keep;
        GpuTexture atlas = target.Atlas;
        Draw(names.SingleSided, _singleSidedShaders, first, ShadowDrawRegion.SingleSided);
        Draw(names.DoubleSided, _doubleSidedShaders, TargetLoad.Keep, ShadowDrawRegion.DoubleSided);
        Draw(names.Fading, _fadingShaders, TargetLoad.Keep, ShadowDrawRegion.Fading);
        Draw(names.FadingDoubleSided, _fadingDoubleSidedShaders, TargetLoad.Keep, ShadowDrawRegion.FadingDoubleSided);
        if (_isReflective)
            return Result.Success();

        DrawIndirect(
            graph
                .Draw(names.Impostors, _impostorShaders, GetTargets(TargetLoad.Keep), header, RenderStages.Geometry)
                .Reads(frameBuffer, constants.Value, target.Views, shadowClusters.Value, target.Instances)
                .Reads(pools.Impostors, pools.Models, target.Materials)
                .Reads(pools.Atlases.AsSpan()),
            ShadowDrawRegion.Impostors
        );

        return Result.Success();

        Result<GpuBuffer> GetBuffer(in BufferDescription description) =>
            view is { } index ? _gpu.GetBuffer(description, index) : _gpu.GetBuffer(description);

        GpuRenderTargets GetTargets(TargetLoad load) =>
            new()
            {
                Colors = target.Colors.IsDefaultOrEmpty
                    ? []
                    : ImmutableArray.CreateRange(
                        target.Colors,
                        static (color, load) => new GpuTarget { Texture = color, Load = load },
                        load
                    ),
                Depth = new GpuTarget { Texture = atlas, Load = load },
            };

        void Draw(string name, in DrawShaders shaders, TargetLoad load, ShadowDrawRegion region) =>
            DrawIndirect(
                graph
                    .Draw(name, shaders, GetTargets(load), header, RenderStages.Geometry)
                    .Reads(target.Albedos ?? GpuBuffer.None)
                    .Reads(
                        frameBuffer,
                        constants.Value,
                        target.Views,
                        shadowClusters.Value,
                        target.Instances,
                        target.Vertices,
                        pools.Vertices,
                        pools.Clusters,
                        pools.Models,
                        pools.Indices,
                        pools.Impostors
                    ),
                region
            );

        // One indirect draw of each that the cull left in the region, as many as it counted at the region's word.
        void DrawIndirect(DrawPass draw, ShadowDrawRegion region) =>
            draw.Indirect(drawArguments.Value, counts.Value)
                .DrawIndirectCount(
                    drawArguments.Value,
                    (ulong)region * MaxShadowDraws * DrawArgumentsBytes,
                    counts.Value,
                    (ulong)region * sizeof(uint),
                    MaxShadowDraws
                );
    }

    /// <summary>
    /// The regions of the shadow draws, the twin of <c>Scene/ShadowDraws.slang</c>'s: a pipeline each.
    /// </summary>
    private enum ShadowDrawRegion
    {
        SingleSided,
        DoubleSided,
        Fading,
        FadingDoubleSided,
        Impostors,
    }
}

/// <summary>
/// What one set of shadow passes draws: the views, the frame whose camera and pools the cull reads, the instances, and
/// the atlas, cleared first or kept.
/// </summary>
/// <remarks>
/// A target of a view keeps its buffers for that <see cref="View"/>; one of the whole frame, with no view, keeps one
/// set.
/// </remarks>
internal readonly record struct ShadowTarget
{
    public required ShadowPassNames Names { get; init; }

    public required GpuBuffer Frame { get; init; }

    public required int? View { get; init; }

    public required GpuBuffer Views { get; init; }

    public required int ViewCount { get; init; }

    public required GpuBuffer Instances { get; init; }

    public required int InstanceCount { get; init; }

    public required GpuBuffer Materials { get; init; }

    public required GpuBuffer Vertices { get; init; }

    public required GpuTexture Atlas { get; init; }

    public required bool IsCleared { get; init; }

    /// <summary>
    /// The colour targets of a reflective shadow map, drawn beside the atlas's depth, and the table of each material
    /// row's diffuse colour that it draws them from; none, or default, for a plain one.
    /// </summary>
    public ImmutableArray<GpuTexture> Colors { get; init; }

    public GpuBuffer? Albedos { get; init; }
}

/// <summary>
/// The names of a set of shadow passes and of their buffers. GPU timings and resources are kept by name, so each set
/// has its own.
/// </summary>
/// <param name="prefix">The set's name at the start of a name, such as "Shadow cache".</param>
/// <param name="inner">The set's name inside a name, such as "shadow cache".</param>
internal sealed class ShadowPassNames(string prefix, string inner)
{
    public string Constants { get; } = $"{prefix} cull constants";

    public string Counts { get; } = $"{prefix} counts";

    public string Clusters { get; } = $"{prefix} clusters";

    public string DrawArguments { get; } = $"{prefix} draw arguments";

    public string Cull { get; } = $"Cull {inner} clusters";

    public string SingleSided { get; } = $"Draw {inner} depth";

    public string DoubleSided { get; } = $"Draw {inner} depth double-sided";

    public string Fading { get; } = $"Draw {inner} depth fading";

    public string FadingDoubleSided { get; } = $"Draw {inner} depth fading double-sided";

    public string Impostors { get; } = $"Draw {inner} impostors";
}
