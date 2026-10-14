using System.Runtime.InteropServices;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The passes a frame runs: <paramref name="pipeline"/>'s, then the world's effects', as each loads with its passes, put
/// in their stages, checked, and compiled, as a <see cref="Listing"/>. Whatever changes them (the world names other
/// effects, a pass, pipeline or shader is edited) builds a new listing from scratch on workers, and the frame keeps
/// drawing with the old one until every pass of the new is compiled, then swaps. The listing's state is the render
/// thread's: workers load and compile, and hand what they made to it.
/// <para>
/// A draw's pipelines are composed around the surfaces it draws, one per <see cref="SurfaceKey"/>: the visibility
/// buffer's raster bins, the clipping template with the surface (Templates/VisibilityClip.frag.hlsl) or the fixed one
/// without (Templates/Visibility.frag.hlsl), over the pieces' vertex stage, whole or only their place
/// (Templates/Pieces.vert.hlsl); the G-buffer's resolve bins (Templates/Resolve.frag.hlsl) over the tiles' quads
/// (Templates/Tiles.vert.hlsl); transparent surfaces forward over the pieces (Templates/ForwardPieces.frag.hlsl); and
/// the sky over the fullscreen triangle.
/// </para>
/// </summary>
internal sealed class PassGraph(IGpu gpu, IShaderCompiler compiler, Assets assets, Handle<Pipeline> pipeline, Threads threads, CancellationToken stopping) : IDisposable
{
    private const string FullscreenPath = "Shaders/Passes/Fullscreen.vert.hlsl";
    private const string DepthOnlyPath = "Shaders/Passes/DepthOnly.frag.hlsl";
    private const string TileClearPath = "Shaders/Passes/TileClear.frag.hlsl";
    private const string PiecesPath = "Templates/Pieces.vert.hlsl";
    private const string TilesPath = "Shaders/Templates/Tiles.vert.hlsl";

    /// <summary>
    /// The storage buffers the pieces' vertex stage binds first: the instances, models, clusters, cards and geometry.
    /// </summary>
    public const int PiecesBuffers = 5;

    /// <summary>
    /// Bytes of one piece the pieces' vertex stage steps through: its instance, which cluster or card and piece, and its
    /// slot, which the visibility buffer names it by.
    /// </summary>
    private const uint PieceBytes = 12;

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
    /// What the pieces' vertex stage reads, which a depth draw's own vertex stage is bound to first.
    /// </summary>
    private static readonly string[] _piecesReads = ["ByteAddressBuffer Instances", "ByteAddressBuffer Models", "ByteAddressBuffer Clusters", "ByteAddressBuffer Cards", "ByteAddressBuffer Geometry"];

    private Listing? _next;
    private Handle<Pipeline> _effects;
    private bool _isStale = true;

    public void Dispose()
    {
        Current?.Dispose();
        _next?.Dispose();
    }

    /// <summary>
    /// The listing the frame draws with; none until the first is compiled.
    /// </summary>
    public Listing? Current { get; private set; }

    /// <summary>
    /// A pipeline (made again when one of its passes changes) or a shader changed: the listing is built again.
    /// </summary>
    public void Invalidate()
    {
        _isStale = true;
    }

    /// <summary>
    /// Swaps in the listing built once it is compiled, and starts building one when the world's effects are not the ones
    /// built or something changed, its surface pipelines compiled up front for the variants the frame draws now, so the
    /// swap shows no gap. While nothing is drawn yet, as the loading domain shows, the listing building is finished
    /// first rather than started again, so the frame draws as soon as it can, then what changed is built. Each frame,
    /// before anything records.
    /// </summary>
    public void Update(Handle<Pipeline> effects, SceneVariants variants)
    {
        if (_next is { IsCompiled: true } built)
        {
            Current?.Dispose();
            Current = built;
            _next = null;
        }

        if ((!_isStale && effects == _effects) || (Current is null && _next is not null))
        {
            return;
        }

        _isStale = false;
        _effects = effects;
        _next?.Dispose();
        _next = new Listing(gpu);
        BuildAsync(_next, effects, new SceneVariants([.. variants.Raster], [.. variants.Resolve], [.. variants.Transparent], variants.Sky)).FireAndForget();
    }

    /// <summary>
    /// A draw pass's pipeline for <paramref name="variant"/>, compiling it the first time it is asked for; none while it
    /// compiles.
    /// </summary>
    public GpuPipeline SurfacePipeline(Listing listing, ListedPass pass, SurfaceKey variant)
    {
        if (pass.Surfaces.TryGetValue(variant, out GpuPipeline made))
        {
            return made;
        }

        pass.Surfaces[variant] = GpuPipeline.None;
        CompileSurfaceAsync(listing, pass, variant, isFailure: false, isCounted: false).FireAndForget();
        return GpuPipeline.None;
    }

    /// <summary>
    /// Loads the passes of the frame pipeline and the world's effects and their shaders, fits them on the render thread,
    /// then compiles every pass, and every draw's vertex stages and pipelines for the variants it draws.
    /// </summary>
    private async Task BuildAsync(Listing listing, Handle<Pipeline> effects, SceneVariants variants)
    {
        List<Loaded> loaded = [];
        foreach (Handle<Pipeline> handle in (Handle<Pipeline>[])[pipeline, effects])
        {
            if (handle == Handle<Pipeline>.None)
            {
                continue;
            }

            Result<Pipeline> frame = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
            if (!frame.Ok)
            {
                Debugging.Error($"A pipeline does not load, so its passes do not run: {frame.Error}");
                continue;
            }

            foreach (PipelinePass pass in frame.Value.Passes)
            {
                if (await LoadPassAsync(pass).ConfigureAwait(false) is { } ready)
                {
                    loaded.Add(ready);
                }
            }
        }

        if (loaded.Count == 0)
        {
            Debugging.Warn("The frame runs no passes, so nothing is drawn: the renderer's pipeline and the world's effects have none.");
        }

        await threads.InvokeAsync(EngineThread.Render, () => listing.Fit(loaded, variants), stopping).ConfigureAwait(false);
        Task<Result<GpuShader>> fullscreen = CompileAssetAsync(FullscreenPath, GpuStage.Vertex);
        Task<Result<GpuShader>> pieces = CompileTextAsync($"#define PIECES_PLACE_ONLY 0\n#include \"{PiecesPath}\"\n", "Pieces", GpuStage.Vertex);
        Task<Result<GpuShader>> places = CompileTextAsync($"#define PIECES_PLACE_ONLY 1\n#include \"{PiecesPath}\"\n", "Pieces (place only)", GpuStage.Vertex);
        Task<Result<GpuShader>> tiles = CompileAssetAsync(TilesPath, GpuStage.Vertex);
        foreach (ListedPass pass in listing.Passes)
        {
            CompileAsync(listing, pass, fullscreen, pieces, places, tiles).FireAndForget();
        }

        threads.Post(EngineThread.Render, () => listing.Pending--);
    }

    /// <summary>
    /// A pass with its shader's text and its params packed, those the pipeline loader merged over the shader's defaults.
    /// Null, said once, when its shader does not load.
    /// </summary>
    private async Task<Loaded?> LoadPassAsync(PipelinePass pass)
    {
        Result<string> text = pass.Pass.Shader == Handle<Shader>.None ? Result.Success(string.Empty) : await TextOfAsync(pass.Pass.Shader).ConfigureAwait(false);
        if (!text.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {text.Error}");
            return null;
        }

        Result<ParamLayout?> layout = ParamLayout.Parse(text.Value, "PassParams", allowTextures: false, ParamLayout.PassBytes);
        if (!layout.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {layout.Error}");
            return null;
        }

        PassWords words = default;
        if (layout.Value is { } paramLayout)
        {
            foreach (string unknown in paramLayout.Unknown(pass.Params.Keys))
            {
                Debugging.Warn($"Pass {pass.Name} is given {unknown}, which its shader's PassParams does not declare.");
            }

            paramLayout.Write([pass.Params], _ => TexturePools.None, MemoryMarshal.AsBytes(new Span<PassWords>(ref words)));
        }

        return new Loaded(pass.Pass, pass.Name, text.Value, layout.Value, words);
    }

    /// <summary>
    /// Compiles a pass's own shaders and makes its pipeline on the render thread; a draw of surfaces, the sky, pieces or
    /// tiles keeps its vertex stages and makes its pipelines as their variants come, those it draws now first. What
    /// fails is said once, and the pass does not run.
    /// </summary>
    private async Task CompileAsync(Listing listing, ListedPass pass, Task<Result<GpuShader>> fullscreen, Task<Result<GpuShader>> pieces, Task<Result<GpuShader>> places, Task<Result<GpuShader>> tiles)
    {
        Func<Result<GpuPipeline>> make;
        if (pass.Pass.Kind == PassKind.Compute)
        {
            Result<GpuShader> shader = await ComputeAsync(pass).ConfigureAwait(false);
            make = () =>
            {
                if (!shader.Ok)
                {
                    return Result.Failure<GpuPipeline>(shader.Error);
                }

                pass.Threads = (shader.Value.ThreadCountX, shader.Value.ThreadCountY, shader.Value.ThreadCountZ);
                return gpu.CreatePipeline(shader.Value);
            };
        }
        else if (!pass.DrawsSurfaces)
        {
            Result<GpuPipelineDesc> desc = pass.Pass.Kind == PassKind.Fullscreen
                ? await FullscreenAsync(pass, await fullscreen.ConfigureAwait(false)).ConfigureAwait(false)
                : await DepthAsync(pass).ConfigureAwait(false);
            Result<GpuPipelineDesc>? clear = pass.Pass.Draw.Tiled ? await ClearAsync(await fullscreen.ConfigureAwait(false)).ConfigureAwait(false) : null;
            make = () =>
            {
                if (!desc.Ok)
                {
                    return Result.Failure<GpuPipeline>(desc.Error);
                }

                if (clear is { } tile)
                {
                    Result<GpuPipeline> cleared = tile.Ok ? gpu.CreatePipeline(tile.Value) : Result.Failure<GpuPipeline>(tile.Error);
                    if (!cleared.Ok)
                    {
                        return cleared;
                    }

                    pass.Clear = cleared.Value;
                }

                return gpu.CreatePipeline(desc.Value);
            };
        }
        else
        {
            PassDrawn drawn = pass.Pass.Draw.Drawn;
            Result<GpuShader> vertex = await (drawn == PassDrawn.Sky ? fullscreen : drawn == PassDrawn.Surfaces ? tiles : pieces).ConfigureAwait(false);
            Result<GpuShader> place = await places.ConfigureAwait(false);
            make = () =>
            {
                pass.Vertex = vertex.Ok ? vertex.Value : null;
                pass.PlaceVertex = place.Ok ? place.Value : null;
                return vertex.Ok ? Result.Success(GpuPipeline.None) : Result.Failure<GpuPipeline>(vertex.Error);
            };
        }

        SurfaceKey[] variants = await threads.InvokeAsync(EngineThread.Render, () =>
        {
            listing.Take(pass, make());
            return pass.DrawsSurfaces ? [.. pass.Surfaces.Keys] : Array.Empty<SurfaceKey>();
        }, stopping).ConfigureAwait(false);
        foreach (SurfaceKey variant in variants)
        {
            CompileSurfaceAsync(listing, pass, variant, isFailure: false, isCounted: true).FireAndForget();
        }
    }

    private async Task<Result<GpuShader>> ComputeAsync(ListedPass pass)
    {
        Result<GpuShader> shader = await CompilePassShaderAsync(pass, GpuStage.Compute, pass.Header(GpuStage.Compute, 0, [])).ConfigureAwait(false);
        if (!shader.Ok)
        {
            return shader;
        }

        GpuShader compiled = shader.Value;
        return compiled.Samplers != pass.Sampled.Count || compiled.StorageBuffers != pass.Buffers.Count || compiled.StorageTextures != pass.StorageReads.Count
            || compiled.ReadWriteStorageTextures != pass.TextureWrites.Count || compiled.ReadWriteStorageBuffers != pass.BufferWrites.Count
            ? Result.Failure<GpuShader>(pass.Mismatch(compiled.Samplers + compiled.StorageTextures, compiled.StorageBuffers, compiled.ReadWriteStorageTextures + compiled.ReadWriteStorageBuffers))
            : shader;
    }

    private async Task<Result<GpuPipelineDesc>> FullscreenAsync(ListedPass pass, Result<GpuShader> vertex)
    {
        Result<GpuShader> fragment = await CompilePassShaderAsync(pass, GpuStage.Fragment, pass.Header(GpuStage.Fragment, 0, [])).ConfigureAwait(false);
        if (!vertex.Ok || !fragment.Ok)
        {
            return Result.Failure<GpuPipelineDesc>(vertex.Ok ? fragment.Error : vertex.Error);
        }

        if (fragment.Value.Samplers != pass.Sampled.Count || fragment.Value.StorageTextures != pass.StorageReads.Count || fragment.Value.StorageBuffers != pass.Buffers.Count)
        {
            return Result.Failure<GpuPipelineDesc>(pass.Mismatch(fragment.Value.Samplers + fragment.Value.StorageTextures, fragment.Value.StorageBuffers, 1));
        }

        return Result.Success(new GpuPipelineDesc { Vertex = vertex.Value, Fragment = fragment.Value, Colors = [pass.TextureWrites[0].Info.Format], Blends = [pass.Pass.Writes[0].Blend], Cull = GpuCull.None });
    }

    /// <summary>
    /// What empties a tile of a depth atlas before a tiled draw draws into it: the fullscreen triangle, at depth 0 over
    /// whatever was there, inside the tile's viewport.
    /// </summary>
    private async Task<Result<GpuPipelineDesc>> ClearAsync(Result<GpuShader> fullscreen)
    {
        Result<GpuShader> fragment = await CompileAssetAsync(TileClearPath, GpuStage.Fragment).ConfigureAwait(false);
        return !fullscreen.Ok || !fragment.Ok
            ? Result.Failure<GpuPipelineDesc>(fullscreen.Ok ? fragment.Error : fullscreen.Error)
            : Result.Success(new GpuPipelineDesc { Vertex = fullscreen.Value, Fragment = fragment.Value, Depth = GpuFormat.D32Float, DepthCompare = GpuCompare.Always, Cull = GpuCull.None });
    }

    /// <summary>
    /// A depth draw: the pass's vertex shader over the pieces a culling pass made, its reads bound to the vertex stage
    /// after the renderer's geometry, and the empty fragment stage that only writes depth unless it names its own. Both
    /// faces are drawn, so a mesh open on one side still casts, and depth is clamped rather than clipped.
    /// </summary>
    private async Task<Result<GpuPipelineDesc>> DepthAsync(ListedPass pass)
    {
        Result<GpuShader> vertex = await CompilePassShaderAsync(pass, GpuStage.Vertex, pass.Header(GpuStage.Vertex, 0, _piecesReads)).ConfigureAwait(false);
        Result<GpuShader> fragment = pass.Pass.Fragment != Handle<Shader>.None
            ? await CompileAssetAsync(assets.Path(pass.Pass.Fragment) ?? DepthOnlyPath, GpuStage.Fragment).ConfigureAwait(false)
            : await CompileAssetAsync(DepthOnlyPath, GpuStage.Fragment).ConfigureAwait(false);
        if (!vertex.Ok || !fragment.Ok)
        {
            return Result.Failure<GpuPipelineDesc>(vertex.Ok ? fragment.Error : vertex.Error);
        }

        if (vertex.Value.StorageBuffers != PiecesBuffers + pass.Buffers.Count || vertex.Value.Samplers != pass.Sampled.Count)
        {
            return Result.Failure<GpuPipelineDesc>(pass.Mismatch(vertex.Value.Samplers, vertex.Value.StorageBuffers - PiecesBuffers, 0));
        }

        PassDraw draw = pass.Pass.Draw;
        return Result.Success(new GpuPipelineDesc
        {
            Vertex = vertex.Value,
            Fragment = fragment.Value,
            Buffers = [new GpuVertexBuffer(0, PieceBytes, PerInstance: true)],
            Inputs = _pieceInputs,
            Depth = GpuFormat.D32Float,
            DepthCompare = draw.DepthCompare,
            DepthBias = draw.DepthBias,
            DepthBiasSlope = draw.DepthBiasSlope,
            DepthBiasClamp = draw.DepthBiasClamp,
            DepthClip = false,
            Cull = GpuCull.None,
        });
    }

    /// <summary>
    /// A surface pipeline of a draw pass: the variant's surface composed between the pass's bindings and its template. A
    /// surface that does not compile is drawn with the failure surface in its place, glowing; one not loaded yet is asked
    /// for again next frame. <paramref name="isCounted"/> when the listing waits for it.
    /// </summary>
    private async Task CompileSurfaceAsync(Listing listing, ListedPass pass, SurfaceKey variant, bool isFailure, bool isCounted)
    {
        Result<GpuPipelineDesc> desc = await SurfaceAsync(pass, variant, isFailure).ConfigureAwait(false);
        await threads.InvokeAsync(EngineThread.Render, () =>
        {
            if (!desc.Ok && !isFailure && desc.Error.Length > 0)
            {
                Debugging.Error($"A surface does not compile for pass {pass.Name}, so it glows the failure: {desc.Error}");
                CompileSurfaceAsync(listing, pass, variant, isFailure: true, isCounted).FireAndForget();
                return;
            }

            if (!desc.Ok && desc.Error.Length > 0)
            {
                Debugging.Error($"The failure surface does not compile for pass {pass.Name}: {desc.Error}");
            }

            listing.TakeSurface(pass, variant, desc.Ok ? gpu.CreatePipeline(desc.Value) : Result.Failure<GpuPipeline>(desc.Error), isCounted);
        }, stopping).ConfigureAwait(false);
    }

    private async Task<Result<GpuPipelineDesc>> SurfaceAsync(ListedPass pass, SurfaceKey variant, bool isFailure)
    {
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        bool hasSurface = variant.Shader != Handle<Shader>.None;
        GpuShader? vertex = drawn == PassDrawn.Visibility && !hasSurface ? pass.PlaceVertex : pass.Vertex;
        if (vertex is null)
        {
            return Result.Failure<GpuPipelineDesc>(string.Empty);
        }

        Handle<Shader> shader = isFailure ? assets.Find<Shader>(Materials.FailurePath) : variant.Shader;
        string name = hasSurface ? assets.Path(shader) ?? "a surface" : "no surface";
        Result<string> surface = Result.Success(string.Empty);
        if (hasSurface)
        {
            Result<SurfaceSource> source = await SurfaceSource.ReadAsync(assets, shader, stopping).ConfigureAwait(false);
            surface = source.Ok ? Result.Success(source.Value.Text) : Result.Failure<string>(source.Error);
        }

        if (!surface.Ok)
        {
            return Result.Failure<GpuPipelineDesc>(surface.Error);
        }

        StringBuilder text = new();
        text.Append("#define SURFACE_MASKED ").Append(variant.Masked ? 1 : 0).Append('\n');
        text.Append("#define SURFACE_DOUBLE_SIDED ").Append(variant.DoubleSided ? 1 : 0).Append('\n');
        text.Append("#define SURFACE_FORWARD ").Append(drawn == PassDrawn.Transparent ? 1 : 0).Append('\n');
        text.Append("#define FAILURE_FORCE ").Append(isFailure ? 1 : 0).Append('\n');
        text.Append("#define SKY_FALLBACK ").Append(hasSurface ? 0 : 1).Append('\n');
        text.Append("#define SKY_CUBE ").Append(pass.DrawsCube ? 1 : 0).Append('\n');
        text.Append(pass.Header(GpuStage.Fragment, TexturePools.Count, ["ByteAddressBuffer Materials"]));
        text.Append("#include \"Include/SurfaceBindings.hlsli\"\n");
        text.Append("#line 1 \"").Append(name).Append("\"\n").Append(surface.Value).Append('\n');
        text.Append("#include \"Templates/").Append(TemplateOf(drawn, hasSurface)).Append(".frag.hlsl\"\n");
        Result<GpuShader> fragment = await compiler.CompileAsync(text.ToString(), $"{pass.Name} with {name}", GpuStage.Fragment, gpu.ShaderFormat, stopping).ConfigureAwait(false);
        if (!fragment.Ok)
        {
            return Result.Failure<GpuPipelineDesc>(fragment.Error);
        }

        if (fragment.Value.Samplers != TexturePools.Count + pass.Sampled.Count || fragment.Value.StorageTextures != pass.StorageReads.Count || fragment.Value.StorageBuffers != 1 + pass.Buffers.Count)
        {
            return Result.Failure<GpuPipelineDesc>(pass.Mismatch(fragment.Value.Samplers - TexturePools.Count + fragment.Value.StorageTextures, fragment.Value.StorageBuffers - 1, 0));
        }

        bool isPieces = drawn is PassDrawn.Visibility or PassDrawn.Transparent;
        return Result.Success(new GpuPipelineDesc
        {
            Vertex = vertex,
            Fragment = fragment.Value,
            Colors = pass.ColorFormats,
            Blends = drawn == PassDrawn.Transparent ? [GpuBlend.Premultiplied] : [],
            Buffers = drawn == PassDrawn.Sky ? [] : [new GpuVertexBuffer(0, isPieces ? PieceBytes : sizeof(uint), PerInstance: true)],
            Inputs = drawn == PassDrawn.Sky ? [] : isPieces ? _pieceInputs : [new GpuVertexInput(0, 0, GpuVertexFormat.Uint1, 0)],
            Depth = pass.Pass.Draw.Depth.Length > 0 ? GpuFormat.D32Float : GpuFormat.Invalid,
            DepthWrite = drawn == PassDrawn.Visibility,
            Cull = variant.DoubleSided || !isPieces ? GpuCull.None : GpuCull.Back,
        });
    }

    /// <summary>
    /// The pass's own shader, its bindings written ahead of it and its params' loader put in after their struct.
    /// </summary>
    private Task<Result<GpuShader>> CompilePassShaderAsync(ListedPass pass, GpuStage stage, string header)
    {
        string body = pass.Layout is { } layout ? layout.Prepare(pass.Text, layout.Loader("LoadPassParams()", string.Empty, "PassRaw")) : pass.Text;
        return compiler.CompileAsync($"{header}#line 1 \"{pass.Name}\"\n{body}", pass.Name, stage, gpu.ShaderFormat, stopping);
    }

    /// <summary>
    /// The template a draw composes its surfaces into.
    /// </summary>
    private static string TemplateOf(PassDrawn drawn, bool hasSurface)
    {
        return drawn switch
        {
            PassDrawn.Visibility => hasSurface ? "VisibilityClip" : "Visibility",
            PassDrawn.Transparent => "ForwardPieces",
            PassDrawn.Sky => "Sky",
            PassDrawn.Surfaces or PassDrawn.Depth => "Resolve",
            _ => "Resolve",
        };
    }

    private Task<Result<GpuShader>> CompileTextAsync(string text, string name, GpuStage stage)
    {
        return compiler.CompileAsync(text, name, stage, gpu.ShaderFormat, stopping);
    }

    private async Task<Result<GpuShader>> CompileAssetAsync(string path, GpuStage stage)
    {
        Result<string> text = await TextOfAsync(assets.Find<Shader>(path)).ConfigureAwait(false);
        return text.Ok ? await compiler.CompileAsync(text.Value, path, stage, gpu.ShaderFormat, stopping).ConfigureAwait(false) : Result.Failure<GpuShader>(text.Error);
    }

    private async Task<Result<string>> TextOfAsync(Handle<Shader> shader)
    {
        Result<Shader> loaded = await assets.LoadAsync(shader, stopping).ConfigureAwait(false);
        return loaded.Ok ? Result.Success(loaded.Value.Text) : Result.Failure<string>(loaded.Error);
    }
}

/// <summary>
/// A pass loaded for a build: its file, its name, its shader's text and params' layout, and its params packed.
/// </summary>
internal sealed record Loaded(Pass Pass, string Name, string Text, ParamLayout? Layout, PassWords Params);
