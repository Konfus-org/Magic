using System.IO;
using System.Text;
using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// What a draw of the world's materials is for: the visibility buffer, each pixel the triangle in front, or the
/// G-buffer, each pixel's surface resolved.
/// </summary>
internal enum MaterialPass : byte
{
    Visibility,
    Resolve,
}

/// <summary>
/// The targets a draw of the world's materials draws into, which its pipelines are made for: up to five colour formats,
/// <see cref="GpuFormat.Invalid"/> past the last, and the depth format, or none.
/// </summary>
internal readonly record struct TargetFormats(
    GpuFormat Color0,
    GpuFormat Color1,
    GpuFormat Color2,
    GpuFormat Color3,
    GpuFormat Color4,
    GpuFormat Depth)
{
    internal const int MaxColors = 5;

    internal IReadOnlyList<GpuFormat> Colors
    {
        get
        {
            List<GpuFormat> colors = [];
            foreach (GpuFormat format in (ReadOnlySpan<GpuFormat>)[Color0, Color1, Color2, Color3, Color4])
            {
                if (format != GpuFormat.Invalid) colors.Add(format);
            }

            return colors;
        }
    }
}

/// <summary>
/// Every pipeline the frame draws with, each compiled on a worker the first time it is asked for and made on the render
/// thread as <see cref="Take"/> runs, none until then: compute passes, by their shader; the sky's fullscreen passes,
/// by their shader composed with the world's sky's, and their target's format; and the world's materials' pipelines,
/// one for each kind of surface and what it is drawn for (<see cref="MaterialPass"/>), each the renderer's shaders for
/// the purpose (<c>Shaders/Rendering/Materials/</c>) composed with the surface. A frame that asks for one not made yet
/// is incomplete (<see cref="IsFrameComplete"/>). An edited shader makes every pipeline again: each keeps drawing as it
/// was until its new one is made, then the new one takes its place. A surface that does not compile is broken
/// (<see cref="FailureHandler.Break"/>), and its materials drawn as the failure; a pass whose shader does not compile
/// is said on the banner and left out. Render thread, but for the compiles.
/// </summary>
internal sealed class GpuPipelines : IDisposable
{
    private const string FullscreenVertex = "Shaders/Passes/Fullscreen.vert.hlsl";
    private const string PiecesVertex = "Rendering/Materials/Pieces.vert.hlsl";
    private const string TilesVertex = "Shaders/Rendering/Materials/Tiles.vert.hlsl";

    private readonly IGpuDriver _gpu;
    private readonly IShaderCompiler _compiler;
    private readonly Assets _assets;
    private readonly FailureHandler _failures;
    private readonly CancellationToken _stopping;
    private readonly Dictionary<Handle<Shader>, Entry> _compute = [];
    private readonly Dictionary<FullscreenKey, Entry> _fullscreen = [];
    private readonly Dictionary<MaterialKey, Entry> _materials = [];

    /// <summary>
    /// The formats each kind of material draw last drew into, which a material's pipelines are made for before it is
    /// first drawn (<see cref="IsDrawable"/>).
    /// </summary>
    private readonly Dictionary<MaterialPass, TargetFormats> _drawnInto = [];

    /// <summary>
    /// What workers compiled, for the render thread to make: the entry it is for, and its description or compute
    /// shader, or why not.
    /// </summary>
    private readonly Channel<Compiled> _compiled = Channel.CreateUnbounded<Compiled>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The compute passes whose shaders do not compile, said on the banner.
    /// </summary>
    private readonly List<string> _failed = [];

    internal GpuPipelines(
        IGpuDriver gpu,
        IShaderCompiler compiler,
        Assets assets,
        FailureHandler failures,
        CancellationToken stopping)
    {
        _gpu = gpu;
        _compiler = compiler;
        _assets = assets;
        _failures = failures;
        _stopping = stopping;
    }

    /// <summary>
    /// Why each pass that does not compile is left out, one line each, for the banner.
    /// </summary>
    internal IReadOnlyList<string> Failed => _failed;

    /// <summary>
    /// Whether every pipeline the frame asked for since <see cref="BeginFrame"/> is made.
    /// </summary>
    internal bool IsFrameComplete { get; private set; }

    public void Dispose()
    {
        foreach (Entry entry in _compute.Values)
        {
            entry.Release(_gpu);
        }

        foreach (Entry entry in _fullscreen.Values)
        {
            entry.Release(_gpu);
        }

        foreach (Entry entry in _materials.Values)
        {
            entry.Release(_gpu);
        }
    }

    /// <summary>
    /// Starts a frame, complete until it asks for a pipeline not made yet.
    /// </summary>
    internal void BeginFrame() => IsFrameComplete = true;

    /// <summary>
    /// The compute pipeline of <paramref name="shader"/>; none while it compiles, its compile started the first time,
    /// or when it does not.
    /// </summary>
    internal GpuPipeline GetCompute(Handle<Shader> shader)
    {
        if (!_compute.TryGetValue(shader, out Entry? entry))
        {
            entry = new Entry();
            _compute[shader] = entry;
        }

        if (entry.IsWanted) CompileAsync(entry, shader).FireAndForget();

        return Asked(entry.Pipeline);
    }

    /// <summary>
    /// The pipeline of <paramref name="shader"/>, composed with the world's sky, <paramref name="sky"/>, drawn over a
    /// target of <paramref name="format"/>; none while it compiles, its compile started the first time, or when it does
    /// not.
    /// </summary>
    internal GpuPipeline GetFullscreen(Handle<Shader> shader, SurfaceKey sky, GpuFormat format)
    {
        FullscreenKey key = new(shader, sky.Shader, format);
        if (!_fullscreen.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry();
            _fullscreen[key] = entry;
        }

        if (entry.IsWanted) CompileAsync(entry, key).FireAndForget();

        return Asked(entry.Pipeline);
    }

    /// <summary>
    /// The pipeline <paramref name="surface"/> is drawn with for <paramref name="pass"/> into targets of
    /// <paramref name="formats"/>: the visibility buffer's for a raster bin that clips, or that does not when
    /// <paramref name="surface"/> has no shader; the G-buffer's for a resolve bin. None while it compiles.
    /// </summary>
    internal GpuPipeline GetMaterial(MaterialPass pass, SurfaceKey surface, TargetFormats formats)
    {
        _drawnInto[pass] = formats;

        return Asked(GetMaterialPipeline(new MaterialKey(pass, surface, formats)));
    }

    /// <summary>
    /// Whether every pipeline <paramref name="surface"/> is drawn with is made, for each kind of draw as it last drew,
    /// their compiles started the first time; a kind not drawn yet waits for none, as nothing draws it.
    /// </summary>
    internal bool IsDrawable(SurfaceKey surface)
    {
        bool isClipped = !_drawnInto.TryGetValue(MaterialPass.Visibility, out TargetFormats visibility)
            || GetMaterialPipeline(new MaterialKey(MaterialPass.Visibility, surface, visibility)) != GpuPipeline.None;
        bool isResolved = !_drawnInto.TryGetValue(MaterialPass.Resolve, out TargetFormats resolve)
            || GetMaterialPipeline(new MaterialKey(MaterialPass.Resolve, surface with { IsMasked = false }, resolve))
                != GpuPipeline.None;

        return isClipped && isResolved;
    }

    /// <summary>
    /// Makes what workers compiled since the last frame, each in place of the pipeline its entry had, which is let go.
    /// </summary>
    internal void Take()
    {
        while (_compiled.Reader.TryRead(out Compiled? compiled))
        {
            Result<GpuPipeline> made = compiled.Error is { } error
                ? Result.Failure<GpuPipeline>(error)
                : compiled.Description is { } description
                    ? _gpu.CreatePipeline(description)
                    : _gpu.CreatePipeline(compiled.Compute ?? throw new InvalidOperationException("Nothing compiled."));
            compiled.Entry.Take(_gpu, made);
            if (!made.Ok) Fail(compiled, made.Error);
        }
    }

    /// <summary>
    /// A shader changed: every pipeline is compiled again the next time it is asked for, each drawing as it was until
    /// then, and nothing is said failed until it fails again.
    /// </summary>
    internal void Reload()
    {
        foreach (Entry entry in _compute.Values)
        {
            entry.MarkStale();
        }

        foreach (Entry entry in _fullscreen.Values)
        {
            entry.MarkStale();
        }

        foreach (Entry entry in _materials.Values)
        {
            entry.MarkStale();
        }

        _failed.Clear();
    }

    private static string TemplateOf(MaterialPass pass, bool hasSurface) => pass switch
    {
        MaterialPass.Visibility when hasSurface => "Rendering/Materials/VisibilityClip.frag.hlsl",
        MaterialPass.Visibility => "Rendering/Materials/Visibility.frag.hlsl",
        MaterialPass.Resolve => "Rendering/Materials/Resolve.frag.hlsl",
        _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, "There is no such material pass."),
    };

    /// <summary>
    /// A pipeline the frame asked for, which leaves the frame incomplete when it is none.
    /// </summary>
    private GpuPipeline Asked(GpuPipeline pipeline)
    {
        IsFrameComplete &= pipeline != GpuPipeline.None;

        return pipeline;
    }

    private GpuPipeline GetMaterialPipeline(MaterialKey key)
    {
        if (!_materials.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry();
            _materials[key] = entry;
        }

        if (entry.IsWanted) CompileAsync(entry, key).FireAndForget();

        return entry.Pipeline;
    }

    /// <summary>
    /// Says what does not compile: a surface broken, its materials drawn as the failure; a pass on the banner.
    /// </summary>
    private void Fail(Compiled compiled, string why)
    {
        if (compiled.Surface is { } surface)
        {
            _failures.Break(surface, compiled.Name, why);

            return;
        }

        Debugging.Error($"Pass {compiled.Name} is left out: its shader does not compile: {why}");
        _failed.Add($"Pass {compiled.Name} is left out: its shader does not compile");
    }

    private async Task CompileAsync(Entry entry, Handle<Shader> shader)
    {
        entry.IsCompiling = true;
        entry.IsStale = false;
        string path = _assets.GetPath(shader) ?? $"shader {shader.Id}";
        string name = Path.GetFileName(path);
        Result<GpuShader> compute = shader == Handle<Shader>.None
            ? Result.Failure<GpuShader>("no shader is named.")
            : await _compiler.CompileAsync(path, GpuStage.Compute, GpuShaderFormat.Spirv, _stopping)
                .ConfigureAwait(false);
        _compiled.Writer.TryWrite(compute.Ok
            ? new Compiled(entry, name, null, compute.Value, null, null)
            : new Compiled(entry, name, null, null, compute.Error, null));
    }

    private async Task CompileAsync(Entry entry, FullscreenKey key)
    {
        entry.IsCompiling = true;
        entry.IsStale = false;
        string path = _assets.GetPath(key.Shader) ?? $"shader {key.Shader.Id}";
        string name = Path.GetFileName(path);
        Task<Result<GpuShader>> vertex = _compiler.CompileAsync(
            FullscreenVertex,
            GpuStage.Vertex,
            GpuShaderFormat.Spirv,
            _stopping);
        Result<GpuShader> fragment = await _compiler.CompileAsync(
            await ComposeSkyAsync(path, key.Sky).ConfigureAwait(false),
            name,
            GpuStage.Fragment,
            GpuShaderFormat.Spirv,
            _stopping).ConfigureAwait(false);
        Result<GpuShader> compiledVertex = await vertex.ConfigureAwait(false);
        GpuPipelineDescription? description = compiledVertex.Ok && fragment.Ok
            ? new GpuPipelineDescription
            {
                Vertex = compiledVertex.Value,
                Fragment = fragment.Value,
                ColorFormats = [key.Format],
                Cull = GpuCull.None,
            }
            : null;
        string? error = description is not null ? null : compiledVertex.Ok ? fragment.Error : compiledVertex.Error;
        _compiled.Writer.TryWrite(new Compiled(entry, name, description, null, error, null));
    }

    private async Task CompileAsync(Entry entry, MaterialKey key)
    {
        entry.IsCompiling = true;
        entry.IsStale = false;
        bool hasSurface = key.Surface.Shader != Handle<Shader>.None;
        string name = hasSurface ? _assets.GetPath(key.Surface.Shader) ?? "a surface" : "no surface";
        Handle<Shader>? surface = hasSurface ? key.Surface.Shader : null;
        Result<string> fragmentText = await ComposeSurfaceAsync(key, name).ConfigureAwait(false);
        Task<Result<GpuShader>> vertex = key.Pass == MaterialPass.Resolve
            ? _compiler.CompileAsync(TilesVertex, GpuStage.Vertex, GpuShaderFormat.Spirv, _stopping)
            : _compiler.CompileAsync(
                $"#define PIECES_PLACE_ONLY {(hasSurface ? 0 : 1)}\n#include \"{PiecesVertex}\"\n",
                "Pieces.vert.hlsl",
                GpuStage.Vertex,
                GpuShaderFormat.Spirv,
                _stopping);
        Result<GpuShader> fragment = fragmentText.Ok
            ? await _compiler.CompileAsync(
                fragmentText.Value,
                $"{key.Pass} with {name}",
                GpuStage.Fragment,
                GpuShaderFormat.Spirv,
                _stopping).ConfigureAwait(false)
            : Result.Failure<GpuShader>(fragmentText.Error);
        Result<GpuShader> compiledVertex = await vertex.ConfigureAwait(false);
        GpuPipelineDescription? description = compiledVertex.Ok && fragment.Ok
            ? new GpuPipelineDescription
            {
                Vertex = compiledVertex.Value,
                Fragment = fragment.Value,
                ColorFormats = key.Formats.Colors,
                DepthFormat = key.Formats.Depth,
                IsDepthWritten = key.Pass == MaterialPass.Visibility,
                Cull = key.Surface.IsDoubleSided || key.Pass == MaterialPass.Resolve ? GpuCull.None : GpuCull.Back,
            }
            : null;
        string? error = description is not null ? null : compiledVertex.Ok ? fragment.Error : compiledVertex.Error;
        _compiled.Writer.TryWrite(new Compiled(entry, $"{key.Pass}", description, null, error, surface));
    }

    /// <summary>
    /// A material pipeline's fragment shader: what the surface and template are told of the variant, how the renderer
    /// binds a surface (Rendering/Include/Surfaces.hlsli), the surface itself, and its template.
    /// </summary>
    private async Task<Result<string>> ComposeSurfaceAsync(MaterialKey key, string name)
    {
        string surface = string.Empty;
        if (key.Surface.Shader != Handle<Shader>.None)
        {
            Result<SurfaceSource> source = await SurfaceSource.ReadAsync(_assets, key.Surface.Shader, _stopping)
                .ConfigureAwait(false);

            if (!source.Ok) return Result.Failure<string>(source.Error);

            surface = source.Value.Text;
        }

        StringBuilder text = new();
        text.Append("#define SURFACE_MASKED ").Append(key.Surface.IsMasked ? 1 : 0).Append('\n')
            .Append("#define SURFACE_DOUBLE_SIDED ").Append(key.Surface.IsDoubleSided ? 1 : 0).Append('\n')
            .Append("#define SURFACE_FORWARD 0\n")
            .Append("#include \"Rendering/Include/Surfaces.hlsli\"\n")
            .Append("#line 1 \"").Append(name).Append("\"\n").Append(surface).Append('\n')
            .Append("#include \"").Append(TemplateOf(key.Pass, key.Surface.Shader != Handle<Shader>.None))
            .Append("\"\n");

        return Result.Success(text.ToString());
    }

    /// <summary>
    /// A shader that composes the sky: how the renderer binds a sky (Rendering/Include/Skies.hlsli), the world's sky
    /// shader <paramref name="skyShader"/>, or the renderer's own fallback without one, and the shader at
    /// <paramref name="path"/>.
    /// </summary>
    private async Task<string> ComposeSkyAsync(string path, Handle<Shader> skyShader)
    {
        string sky = string.Empty;
        string name = "the fallback sky";
        if (skyShader != Handle<Shader>.None)
        {
            Result<SurfaceSource> source = await SurfaceSource.ReadAsync(_assets, skyShader, _stopping)
                .ConfigureAwait(false);
            sky = source.Ok ? source.Value.Text : string.Empty;
            name = source.Ok ? source.Value.Name : name;
        }

        string included = path.StartsWith("Shaders/", StringComparison.Ordinal) ? path[8..] : path;

        return new StringBuilder()
            .Append("#define SKY_FALLBACK ").Append(sky.Length == 0 ? 1 : 0).Append('\n')
            .Append("#include \"Rendering/Include/Skies.hlsli\"\n")
            .Append("#line 1 \"").Append(name).Append("\"\n").Append(sky).Append('\n')
            .Append("#include \"").Append(included).Append("\"\n")
            .ToString();
    }

    private readonly record struct FullscreenKey(Handle<Shader> Shader, Handle<Shader> Sky, GpuFormat Format);

    private readonly record struct MaterialKey(MaterialPass Pass, SurfaceKey Surface, TargetFormats Formats);

    /// <summary>
    /// What a worker compiled for an entry: the pipeline's description, or its compute shader, or why it does not
    /// compile; the surface it was composed with, which is broken when it does not.
    /// </summary>
    private sealed record Compiled(
        Entry Entry,
        string Name,
        GpuPipelineDescription? Description,
        GpuShader? Compute,
        string? Error,
        Handle<Shader>? Surface);

    /// <summary>
    /// One pipeline: the one made, none until then; whether one is compiling; and whether a shader changed since its
    /// compile started, so it is compiled again.
    /// </summary>
    private sealed class Entry
    {
        internal GpuPipeline Pipeline { get; private set; }

        internal bool IsCompiling { get; set; }

        internal bool IsStale { get; set; }

        /// <summary>
        /// Whether a compile should start: none made or compiling yet, or the one made is stale.
        /// </summary>
        internal bool IsWanted => !IsCompiling && (Pipeline == GpuPipeline.None ? !HasFailed : IsStale);

        private bool HasFailed { get; set; }

        /// <summary>
        /// Takes what was made in place of the pipeline it had, which is let go; on failure, the one it had stays.
        /// </summary>
        internal void Take(IGpuDriver gpu, Result<GpuPipeline> made)
        {
            IsCompiling = false;
            HasFailed = !made.Ok && Pipeline == GpuPipeline.None;

            if (!made.Ok) return;

            gpu.Release(Pipeline);
            Pipeline = made.Value;
        }

        internal void Release(IGpuDriver gpu) => gpu.Release(Pipeline);

        /// <summary>
        /// A shader changed: compiled again the next time it is asked for, even when it failed.
        /// </summary>
        internal void MarkStale()
        {
            IsStale = true;
            HasFailed = false;
        }
    }
}
