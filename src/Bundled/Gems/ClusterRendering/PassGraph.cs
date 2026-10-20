using System.IO;
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
/// The passes a frame runs: the renderer's pipeline's, then the world's effects', then the overlay pipeline's, as
/// each loads with its passes, and a debug view's pass after them, put in their stages, checked, and compiled, as a
/// <see cref="Listing"/>. A script pass's script is made by <paramref name="scripts"/>, without which it does not run. Whatever changes them (the world names other
/// effects, a pass, pipeline, shader or curve is edited) builds a new listing from scratch on workers, and the frame keeps
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
/// <para>
/// A shader binds what it uses: each stage's is first compiled to SPIR-V with every name its pass can see declared, a
/// probe that reads back which it used (<see cref="StageBindings"/>), then compiled for the GPU with those at the
/// registers the pass binds them at, and checked to use no others. A draw's probe is its templates over the default
/// surface, with what the surface contract offers any surface, and every surface of it is bound the same.
/// </para>
/// </summary>
internal sealed class PassGraph(IGpu gpu, IShaderCompiler compiler, Assets assets, IScripts? scripts, Threads threads, CancellationToken stopping) : IDisposable
{
    private const string FullscreenPath = "Shaders/Passes/Fullscreen.vert.hlsl";
    private const string DepthOnlyPath = "Shaders/Passes/DepthOnly.frag.hlsl";
    private const string PiecesPath = "Templates/Pieces.vert.hlsl";
    private const string TilesPath = "Shaders/Templates/Tiles.vert.hlsl";

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
    /// What the shared vertex stages can see: the renderer's own names, the scene's tables among them.
    /// </summary>
    private static readonly List<ResourceInfo> _engineVisible = [.. ResourceInfo.Engine.Values];

    private Listing? _next;

    /// <summary>
    /// What the latest listing, built or building, was built from, but the effects (<see cref="Effects"/>), and whether
    /// something changed since that calls for another.
    /// </summary>
    private Handle<Pass> _appended;
    private Handle<Pipeline> _overlay;
    private Handle<Pipeline> _pipeline;
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
    /// The effects the latest listing, built or building, runs.
    /// </summary>
    public Handle<Pipeline> Effects { get; private set; }

    /// <summary>
    /// Whether a listing is being built, which the frame swaps in once it is compiled.
    /// </summary>
    public bool IsBuilding => _next is not null;

    /// <summary>
    /// The surfaces that do not compile for some pass, whose materials are drawn as the failure
    /// (<see cref="Materials"/>), until the shader changes.
    /// </summary>
    public HashSet<Handle<Shader>> BrokenSurfaces { get; } = [];

    /// <summary>
    /// The surfaces that failed to compile since the materials last asked: whose materials are packed anew.
    /// </summary>
    public HashSet<Handle<Shader>> Broke { get; } = [];

    /// <summary>
    /// A pipeline (made again when one of its passes changes) or a shader changed: the listing is built again, once the
    /// build under way, if any, is swapped in, so a burst of changes (a remake of every shader, one at a time) is one
    /// further build, not one each.
    /// </summary>
    public void Invalidate()
    {
        _isStale = true;
    }

    /// <summary>
    /// Runs <paramref name="pass"/> after the world's effects from now on, as a debug view is; none to stop. The listing
    /// is built again when it changes.
    /// </summary>
    public void Append(Handle<Pass> pass)
    {
        _isStale |= pass != _appended;
        _appended = pass;
    }

    /// <summary>
    /// Swaps in the listing built once it is compiled with every surface variant the frame draws now, those that first
    /// showed while it was built among them, so the swap shows no gap; and starts building one when the renderer's
    /// <paramref name="pipeline"/> or the world's effects are not the ones built or something changed, its surface pipelines compiled up front for the variants the frame
    /// draws now. One listing builds at a time: while one is under way it is finished and swapped in first, then what
    /// changed meanwhile is built, so the frame draws as soon as it can (the loading domain, before the first is
    /// compiled) and a burst of changes starts one build, not one each; a build that throws (an error, said by
    /// FireAndForget) never swaps in, and nothing builds again after it. Each frame, before anything records.
    /// </summary>
    public void Update(Handle<Pipeline> pipeline, Handle<Pipeline> effects, Handle<Pipeline> overlay, SceneVariants variants)
    {
        if (_next is { IsCompiled: true } built && HasEvery(built, variants))
        {
            Current?.Dispose();
            Current = built;
            _next = null;
        }

        if ((!_isStale && effects == Effects && pipeline == _pipeline && overlay == _overlay) || _next is not null)
        {
            return;
        }

        _isStale = false;
        Effects = effects;
        _pipeline = pipeline;
        _overlay = overlay;
        _next = new Listing(gpu);
        BuildAsync(_next, pipeline, effects, overlay, _appended, new SceneVariants([.. variants.Raster], [.. variants.Resolve], [.. variants.Transparent], variants.Sky)).FireAndForget();
    }

    /// <summary>
    /// A draw pass's pipeline for <paramref name="variant"/>, compiling it the first time it is asked for; none while it
    /// compiles.
    /// </summary>
    public GpuPipeline SurfacePipeline(ListedPass pass, SurfaceKey variant)
    {
        if (pass.Surfaces.TryGetValue(variant, out GpuPipeline made))
        {
            return made;
        }

        pass.Surfaces[variant] = GpuPipeline.None;
        CompileSurfaceAsync(pass, variant, isCounted: false).FireAndForget();
        return GpuPipeline.None;
    }

    /// <summary>
    /// Whether the frame can draw a material of <paramref name="surface"/> as <paramref name="type"/> says, every
    /// pipeline it is drawn with compiled: the raster bins' for its clusters, which clip as it fades or its cards take
    /// over, and the resolve bin's for its pixels, or the forward one for a transparent material. Those not asked for yet
    /// start compiling, for the listing being built while there is one, as that is the one that will draw it: it is
    /// drawable once that listing is swapped in. A material waits for this (<see cref="Materials"/>), so nothing is drawn
    /// black, left out, or drawn without the world's effects for a while.
    /// </summary>
    public bool IsDrawable(SurfaceKey surface, MaterialType type)
    {
        if ((_next ?? Current) is not { } listing)
        {
            return false;
        }

        bool isTransparent = type == MaterialType.Transparent;
        bool isDrawable = true;
        foreach (ListedPass pass in listing.Passes)
        {
            SurfaceKey? variant = pass.Pass.Kind != PassKind.Draw ? null : pass.Pass.Draw.Drawn switch
            {
                PassDrawn.Visibility => isTransparent ? null : surface,
                PassDrawn.Surfaces => isTransparent ? null : surface with { Masked = false },
                PassDrawn.Transparent => isTransparent ? surface : null,
                PassDrawn.Sky => null,
                PassDrawn.Depth => null,
                _ => null,
            };

            if (variant is not { } key)
            {
                continue;
            }

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
    /// Whether a listing has the pipelines of every surface variant the frame draws, asking for those it has not.
    /// </summary>
    private bool HasEvery(Listing listing, SceneVariants variants)
    {
        bool hasEvery = true;
        foreach (ListedPass pass in listing.Passes)
        {
            if (!pass.DrawsSurfaces)
            {
                continue;
            }

            foreach (SurfaceKey variant in pass.VariantsOf(variants))
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
    {
        return SurfacePipeline(pass, variant) != GpuPipeline.None || pass.Broken.Contains(variant);
    }

    /// <summary>
    /// Loads the passes of the frame pipeline, the world's effects and the pipeline appended after them and their
    /// shaders, fits them on the render thread, probes what every stage of each uses and what each thing made is used
    /// for, then compiles every pass bound to that, and every draw's vertex stages and pipelines for the variants it
    /// draws.
    /// </summary>
    private async Task BuildAsync(Listing listing, Handle<Pipeline> pipeline, Handle<Pipeline> effects, Handle<Pipeline> overlay, Handle<Pass> appended, SceneVariants variants)
    {
        List<Loaded> loaded = [];
        List<string> failures = [];
        foreach (Handle<Pipeline> handle in (Handle<Pipeline>[])[pipeline, effects, overlay])
        {
            if (handle == Handle<Pipeline>.None)
            {
                continue;
            }

            Result<Pipeline> frame = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
            if (!frame.Ok)
            {
                Debugging.Error($"A pipeline does not load, so its passes do not run: {frame.Error}");
                failures.Add($"A pipeline does not load: {Listing.FirstLine(frame.Error)}");
                continue;
            }

            foreach (PipelinePass pass in frame.Value.Passes)
            {
                if (await LoadPassAsync(pass, failures).ConfigureAwait(false) is { } ready)
                {
                    loaded.Add(ready);
                }
            }
        }

        if (loaded.Count == 0)
        {
            Debugging.Warn("The frame runs no passes, so nothing is drawn: the renderer's pipeline and the world's effects have none.");
        }

        if (appended != Handle<Pass>.None)
        {
            Result<Pass> view = await assets.LoadAsync(appended, stopping).ConfigureAwait(false);
            string name = Path.GetFileNameWithoutExtension(assets.Path(appended) ?? string.Empty);
            if (!view.Ok)
            {
                Debugging.Error($"Debug view {name} does not load, so it is not shown: {view.Error}");
            }
            else if (await LoadPassAsync(new PipelinePass(view.Value, name, view.Value.Params), failures).ConfigureAwait(false) is { } ready)
            {
                loaded.Add(ready);
            }
        }

        await threads.InvokeAsync(EngineThread.Render, () => listing.Fit(loaded, failures, variants), stopping).ConfigureAwait(false);
        Task<Result<StageBindings>> piecesProbe = ProbeAsync(_engineVisible, [], GpuStage.Vertex, [(PiecesText(placeOnly: false), "Pieces")]);
        List<Task> probes = [];
        foreach (ListedPass pass in listing.Passes)
        {
            probes.Add(ProbePassAsync(pass, piecesProbe));
        }

        await Task.WhenAll(probes).ConfigureAwait(false);
        await threads.InvokeAsync(EngineThread.Render, listing.Use, stopping).ConfigureAwait(false);

        // The place-only pieces use a part of what the pieces do, and are bound as the pieces are, so a draw binds once.
        Result<StageBindings> piecesBound = await piecesProbe.ConfigureAwait(false);
        Task<Result<GpuShader>> fullscreen = CompileAssetAsync(FullscreenPath, GpuStage.Vertex);
        Task<Result<GpuShader>> pieces = CompileBoundAsync(_engineVisible, [], piecesBound, GpuStage.Vertex, PiecesText(placeOnly: false), "Pieces");
        Task<Result<GpuShader>> places = CompileBoundAsync(_engineVisible, [], piecesBound, GpuStage.Vertex, PiecesText(placeOnly: true), "Pieces (place only)");
        Task<Result<GpuShader>> tiles = CompileAssetAsync(TilesPath, GpuStage.Vertex);
        foreach (ListedPass pass in listing.Passes)
        {
            CompileAsync(listing, pass, fullscreen, pieces, places, tiles).FireAndForget();
        }

        threads.Post(EngineThread.Render, () => listing.Pending--);
    }

    /// <summary>
    /// A pass with its shader's text as HLSL takes it, its params' loader in, and its params packed, those the pipeline
    /// loader merged over the shader's defaults. Null, said once and added to <paramref name="failures"/>, when its
    /// shader does not load.
    /// </summary>
    private async Task<Loaded?> LoadPassAsync(PipelinePass pass, List<string> failures)
    {
        Result<string> text = pass.Pass.Shader == Handle<Shader>.None ? Result.Success(string.Empty) : await TextOfAsync(pass.Pass.Shader).ConfigureAwait(false);
        Result<ParamLayout?> layout = text.Ok ? ParamLayout.Parse(text.Value, "PassParams", allowTextures: false, allowCurves: true, ParamLayout.PassBytes) : Result.Failure<ParamLayout?>(text.Error);
        if (!text.Ok || !layout.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {layout.Error}");
            failures.Add($"Pass {pass.Name} does not run: {Listing.FirstLine(layout.Error)}");
            return null;
        }

        string body = text.Value;
        PassWords words = default;
        if (layout.Value is { } paramLayout)
        {
            body = paramLayout.Prepare("LoadPassParams()", string.Empty, "PassRaw");
            foreach (string unknown in paramLayout.Unknown(pass.Params.Keys))
            {
                Debugging.Warn($"Pass {pass.Name} is given {unknown}, which its shader's PassParams does not declare.");
            }

            Dictionary<Handle<Curve>, Curve> curves = await CurvesOfAsync(paramLayout, pass).ConfigureAwait(false);
            paramLayout.Write([pass.Params], _ => TexturePools.None, param => curves.GetValueOrDefault(CurveOf(param)), MemoryMarshal.AsBytes(new Span<PassWords>(ref words)));
        }

        return new Loaded(pass.Pass, pass.Name, body, words);
    }

    /// <summary>
    /// The curves a pass's params name for its <c>Curve</c> members, the default curve for one they leave out, loaded; one
    /// that does not load is said, and packs as nothing.
    /// </summary>
    private async Task<Dictionary<Handle<Curve>, Curve>> CurvesOfAsync(ParamLayout layout, PipelinePass pass)
    {
        Dictionary<Handle<Curve>, Curve> curves = [];
        foreach (ParamField field in layout.Fields)
        {
            Handle<Curve> handle = field.Type == ParamType.Curve ? CurveOf(pass.Params.GetValueOrDefault(field.Name) ?? field.Default) : Handle<Curve>.None;
            if (handle == Handle<Curve>.None || curves.ContainsKey(handle))
            {
                continue;
            }

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
    private Handle<Curve> CurveOf(MaterialParam param)
    {
        return param.Curve == Handle<Curve>.None ? assets.Default<Curve>() : param.Curve;
    }

    /// <summary>
    /// Compiles a pass's own shaders and makes its pipeline on the render thread; a draw of surfaces, the sky, pieces or
    /// tiles keeps its vertex stages and makes its pipelines as their variants come, those it draws now first. What
    /// fails is said once, and the pass does not run.
    /// </summary>
    private async Task CompileAsync(Listing listing, ListedPass pass, Task<Result<GpuShader>> fullscreen, Task<Result<GpuShader>> pieces, Task<Result<GpuShader>> places, Task<Result<GpuShader>> tiles)
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
            Result<GpuPipelineDescription> desc = pass.Pass.Kind == PassKind.Fullscreen
                ? await FullscreenAsync(pass, await fullscreen.ConfigureAwait(false)).ConfigureAwait(false)
                : await DepthAsync(pass).ConfigureAwait(false);
            make = () => desc.Ok ? gpu.CreatePipeline(desc.Value) : Result.Failure<GpuPipeline>(desc.Error);
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
        {
            return Result.Failure<IPipelineScript>("a script pass needs a scripting gem, and the project lists none.");
        }

        Result<Script> asset = await assets.LoadAsync(pass.Pass.Script, stopping).ConfigureAwait(false);
        if (!asset.Ok)
        {
            return Result.Failure<IPipelineScript>($"its script does not load: {asset.Error}");
        }

        Result<IScript> made = scripts.Make(asset.Value);
        if (!made.Ok)
        {
            return Result.Failure<IPipelineScript>($"its script {asset.Value.Type.Name} cannot be made: {made.Error}");
        }

        if (made.Value is not IPipelineScript script)
        {
            (made.Value as IDisposable)?.Dispose();
            return Result.Failure<IPipelineScript>($"its script {asset.Value.Type.Name} is no IPipelineScript.");
        }

        return Result.Success(script);
    }

    private Task<Result<GpuShader>> ComputeAsync(ListedPass pass)
    {
        return CompileBoundAsync(pass, GpuStage.Compute, PassBody(pass), pass.Name);
    }

    private async Task<Result<GpuPipelineDescription>> FullscreenAsync(ListedPass pass, Result<GpuShader> vertex)
    {
        Result<GpuShader> fragment = await CompileBoundAsync(pass, GpuStage.Fragment, PassBody(pass), pass.Name).ConfigureAwait(false);
        if (!vertex.Ok || !fragment.Ok)
        {
            return Result.Failure<GpuPipelineDescription>(vertex.Ok ? fragment.Error : vertex.Error);
        }

        return Result.Success(new GpuPipelineDescription { Vertex = vertex.Value, Fragment = fragment.Value, ColorFormats = [pass.TextureWrites[0].Info.Format], Blends = [pass.Pass.Writes[0].Blend], Cull = GpuCull.None });
    }

    /// <summary>
    /// A depth draw: the pass's vertex shader over the pieces a culling pass made, its reads bound to the vertex stage
    /// after the renderer's geometry, and the empty fragment stage that only writes depth. Both faces are drawn, so a
    /// mesh open on one side still casts, and depth is clamped rather than clipped.
    /// </summary>
    private async Task<Result<GpuPipelineDescription>> DepthAsync(ListedPass pass)
    {
        Result<GpuShader> vertex = await CompileBoundAsync(pass, GpuStage.Vertex, PassBody(pass), pass.Name).ConfigureAwait(false);
        Result<GpuShader> compiled = await CompileAssetAsync(DepthOnlyPath, GpuStage.Fragment).ConfigureAwait(false);

        // The fragment stage is written by hand to bind nothing, as what only writes depth needs nothing.
        Result<GpuShader> fragment = compiled.Ok ? new StageBindings().Check(compiled.Value, pass.Name) : compiled;
        if (!vertex.Ok || !fragment.Ok)
        {
            return Result.Failure<GpuPipelineDescription>(vertex.Ok ? fragment.Error : vertex.Error);
        }

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
    /// A surface pipeline of a draw pass: the variant's surface composed between the pass's bindings and its template. A
    /// surface that does not compile is broken (<see cref="BrokenSurfaces"/>), so its materials are drawn as the
    /// failure material; one not loaded yet is asked for again next frame. <paramref name="isCounted"/> when the listing
    /// waits for it.
    /// </summary>
    private async Task CompileSurfaceAsync(ListedPass pass, SurfaceKey variant, bool isCounted)
    {
        Result<GpuPipelineDescription> desc = await SurfaceAsync(pass, variant).ConfigureAwait(false);
        await threads.InvokeAsync(EngineThread.Render, () =>
        {
            if (!desc.Ok && desc.Error.Length > 0)
            {
                Debugging.Error($"A surface does not compile for pass {pass.Name}, so its materials are drawn as the failure: {desc.Error}");
                if (variant.Shader != Handle<Shader>.None)
                {
                    BrokenSurfaces.Add(variant.Shader);
                    Broke.Add(variant.Shader);
                }
            }

            pass.Listing.TakeSurface(pass, variant, desc.Ok ? gpu.CreatePipeline(desc.Value) : Result.Failure<GpuPipeline>(desc.Error), isCounted);
        }, stopping).ConfigureAwait(false);
    }

    private async Task<Result<GpuPipelineDescription>> SurfaceAsync(ListedPass pass, SurfaceKey variant)
    {
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        bool hasSurface = variant.Shader != Handle<Shader>.None;
        GpuShader? vertex = drawn == PassDrawn.Visibility && !hasSurface ? pass.PlaceVertex : pass.Vertex;
        if (vertex is null)
        {
            return Result.Failure<GpuPipelineDescription>(string.Empty);
        }

        Result<(string Text, string Name)> source = await SurfaceTextAsync(pass, variant).ConfigureAwait(false);
        if (!source.Ok)
        {
            return Result.Failure<GpuPipelineDescription>(source.Error);
        }

        Result<GpuShader> fragment = await CompileBoundAsync(pass, GpuStage.Fragment, source.Value.Text, source.Value.Name).ConfigureAwait(false);
        if (!fragment.Ok)
        {
            return Result.Failure<GpuPipelineDescription>(fragment.Error);
        }

        bool isPieces = drawn is PassDrawn.Visibility or PassDrawn.Transparent;
        return Result.Success(new GpuPipelineDescription
        {
            Vertex = vertex,
            Fragment = fragment.Value,
            ColorFormats = pass.ColorFormats,
            Blends = drawn == PassDrawn.Transparent ? [GpuBlend.Premultiplied] : [],
            VertexBuffers = drawn == PassDrawn.Sky ? [] : [new GpuVertexBuffer(0, isPieces ? PieceBytes : sizeof(uint), PerInstance: true)],
            VertexInputs = drawn == PassDrawn.Sky ? [] : isPieces ? _pieceInputs : [new GpuVertexInput(0, 0, GpuVertexFormat.Uint1, 0)],
            DepthFormat = pass.Pass.Draw.Depth.Length > 0 ? GpuFormat.D32Float : GpuFormat.Invalid,
            DepthWrite = drawn == PassDrawn.Visibility,
            Cull = variant.DoubleSided || !isPieces ? GpuCull.None : GpuCull.Back,
        });
    }

    /// <summary>
    /// A surface pipeline's fragment shader, after the pass's bindings: what the surface and template are told of the
    /// variant, the variant's surface and its template, and the name it is compiled under.
    /// </summary>
    private async Task<Result<(string Text, string Name)>> SurfaceTextAsync(ListedPass pass, SurfaceKey variant)
    {
        PassDrawn drawn = pass.Pass.Draw.Drawn;
        bool hasSurface = variant.Shader != Handle<Shader>.None;
        Handle<Shader> shader = variant.Shader;
        string name = hasSurface ? assets.Path(shader) ?? "a surface" : "no surface";
        string surface = string.Empty;
        if (hasSurface)
        {
            Result<SurfaceSource> source = await SurfaceSource.ReadAsync(assets, shader, stopping).ConfigureAwait(false);
            if (!source.Ok)
            {
                return Result.Failure<(string Text, string Name)>(source.Error);
            }

            surface = source.Value.Text;
        }

        StringBuilder text = new();
        text.Append("#define SURFACE_MASKED ").Append(variant.Masked ? 1 : 0).Append('\n');
        text.Append("#define SURFACE_DOUBLE_SIDED ").Append(variant.DoubleSided ? 1 : 0).Append('\n');
        text.Append("#define SURFACE_FORWARD ").Append(drawn == PassDrawn.Transparent ? 1 : 0).Append('\n');
        text.Append("#define SKY_FALLBACK ").Append(hasSurface ? 0 : 1).Append('\n');
        text.Append("#define SKY_CUBE ").Append(pass.DrawsCube ? 1 : 0).Append('\n');
        text.Append("#include \"Include/SurfaceBindings.hlsli\"\n");
        text.Append("#line 1 \"").Append(name).Append("\"\n").Append(surface).Append('\n');
        text.Append("#include \"Templates/").Append(TemplateOf(drawn, hasSurface)).Append(".frag.hlsl\"\n");
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
            Keep(pass, GpuStage.Compute, await ProbeAsync(pass, GpuStage.Compute, [(PassBody(pass), pass.Name)]).ConfigureAwait(false));
            return;
        }

        if (pass.Pass.Kind == PassKind.Fullscreen)
        {
            pass.SetBound(GpuStage.Vertex, none);
            Keep(pass, GpuStage.Fragment, await ProbeAsync(pass, GpuStage.Fragment, [(PassBody(pass), pass.Name)]).ConfigureAwait(false));
            return;
        }

        if (pass.Pass.Draw.Drawn == PassDrawn.Depth)
        {
            pass.SetBound(GpuStage.Fragment, none);
            Keep(pass, GpuStage.Vertex, await ProbeAsync(pass, GpuStage.Vertex, [(PassBody(pass), pass.Name)]).ConfigureAwait(false));
            return;
        }

        bool isPieces = pass.Pass.Draw.Drawn is PassDrawn.Visibility or PassDrawn.Transparent;
        Keep(pass, GpuStage.Vertex, isPieces ? await pieces.ConfigureAwait(false) : Result.Success(none));
        Keep(pass, GpuStage.Fragment, await ProbeTemplatesAsync(pass).ConfigureAwait(false));
    }

    /// <summary>
    /// What a draw of surfaces binds in its fragment stage: the union over its templates, each over the default surface,
    /// whose every value comes from the materials, so no path of the template folds away as it would over a surface of
    /// constants, and the visibility buffer's and the sky's also with no surface; and what the surface contract offers
    /// any surface (Shaders/Include/SurfaceBindings.hlsli), the texture pools, the materials and, for a forward surface,
    /// the scene behind, whether the probe used them or not.
    /// </summary>
    private async Task<Result<StageBindings>> ProbeTemplatesAsync(ListedPass pass)
    {
        SurfaceKey surface = new(assets.Default<Shader>(), false, false);
        SurfaceKey none = new(Handle<Shader>.None, false, false);
        SurfaceKey[] variants = pass.Pass.Draw.Drawn is PassDrawn.Visibility or PassDrawn.Sky ? [surface, none] : [surface];
        List<(string Text, string Name)> sources = [];
        foreach (SurfaceKey variant in variants)
        {
            Result<(string Text, string Name)> source = await SurfaceTextAsync(pass, variant).ConfigureAwait(false);
            if (!source.Ok)
            {
                return Result.Failure<StageBindings>(source.Error);
            }

            sources.Add(source.Value);
        }

        List<GpuShaderBinding> contract = [new GpuShaderBinding("Materials", GpuBindingKind.StorageBuffer, 0)];
        foreach (string pool in TexturePools.Names)
        {
            contract.Add(new GpuShaderBinding(pool, GpuBindingKind.Sampled, 0));
        }

        if (pass.Pass.Draw.Drawn == PassDrawn.Transparent && pass.Visible.Exists(info => string.Equals(info.Name, "SceneColor", StringComparison.Ordinal)))
        {
            contract.Add(new GpuShaderBinding("SceneColor", GpuBindingKind.Sampled, 0));
        }

        return await ProbeAsync(pass, GpuStage.Fragment, sources, contract).ConfigureAwait(false);
    }

    private static void Keep(ListedPass pass, GpuStage stage, Result<StageBindings> bound)
    {
        if (bound.Ok)
        {
            pass.SetBound(stage, bound.Value);
        }
        else
        {
            pass.ProbeError = bound.Error;
        }
    }

    /// <summary>
    /// What a stage's shaders use of what <paramref name="visible"/> and <paramref name="writes"/> declare: each compiled
    /// to SPIR-V with every name declared, what it bound read back, and the union resolved (<see cref="StageBindings.Of"/>)
    /// with whatever is <paramref name="also"/> bound.
    /// </summary>
    private async Task<Result<StageBindings>> ProbeAsync(IReadOnlyList<ResourceInfo> visible, IReadOnlyList<PassBinding> writes, GpuStage stage, IReadOnlyList<(string Text, string Name)> sources, IReadOnlyList<GpuShaderBinding>? also = null)
    {
        string header = StageBindings.Header(stage, visible, writes, null);
        List<GpuShaderBinding> used = [.. also ?? []];
        foreach ((string text, string name) in sources)
        {
            Result<GpuShader> probe = await compiler.CompileAsync(header + text, name, stage, GpuShaderFormat.Spirv, stopping).ConfigureAwait(false);
            if (!probe.Ok)
            {
                return Result.Failure<StageBindings>(probe.Error);
            }

            used.AddRange(probe.Value.Bindings);
        }

        return StageBindings.Of(used, visible, writes);
    }

    private Task<Result<StageBindings>> ProbeAsync(ListedPass pass, GpuStage stage, IReadOnlyList<(string Text, string Name)> sources, IReadOnlyList<GpuShaderBinding>? also = null)
    {
        return ProbeAsync(pass.Visible, pass.WritesOf(stage), stage, sources, also);
    }

    /// <summary>
    /// A shader compiled for the GPU, bound to what its stage binds: every name declared, those it binds at their slots,
    /// and the shader checked to use no others.
    /// </summary>
    private async Task<Result<GpuShader>> CompileBoundAsync(IReadOnlyList<ResourceInfo> visible, IReadOnlyList<PassBinding> writes, Result<StageBindings> bound, GpuStage stage, string text, string name)
    {
        if (!bound.Ok)
        {
            return Result.Failure<GpuShader>(bound.Error);
        }

        Result<GpuShader> compiled = await compiler.CompileAsync(StageBindings.Header(stage, visible, writes, bound.Value) + text, name, stage, gpu.ShaderFormat, stopping).ConfigureAwait(false);
        return compiled.Ok ? bound.Value.Check(compiled.Value, name) : compiled;
    }

    private Task<Result<GpuShader>> CompileBoundAsync(ListedPass pass, GpuStage stage, string text, string name)
    {
        StageBindings? bound = pass.Bound(stage);
        return CompileBoundAsync(pass.Visible, pass.WritesOf(stage), bound is null ? Result.Failure<StageBindings>(pass.ProbeError) : Result.Success(bound), stage, text, name);
    }

    /// <summary>
    /// The pass's own shader's text, named as its errors name it.
    /// </summary>
    private static string PassBody(ListedPass pass)
    {
        return $"#line 1 \"{pass.Name}\"\n{pass.Body}";
    }

    private static string PiecesText(bool placeOnly)
    {
        return $"#define PIECES_PLACE_ONLY {(placeOnly ? 1 : 0)}\n#include \"{PiecesPath}\"\n";
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
/// A pass loaded for a build: its file, its name, its shader's text as HLSL takes it, its params' loader in, and its
/// params packed.
/// </summary>
internal sealed record Loaded(Pass Pass, string Name, string Body, PassWords Params);
