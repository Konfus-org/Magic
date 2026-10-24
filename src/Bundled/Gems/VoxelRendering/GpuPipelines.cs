using System.IO;
using System.Linq;
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
/// thread as <see cref="Take"/> runs, none until then: compute passes, by their entry point and what it is specialized
/// by; and the world's materials' pipelines, one for each kind of surface and what it is drawn for
/// (<see cref="MaterialPass"/>), each the renderer's entry points for the purpose (<c>Shaders/Renderer/Materials.slang</c>)
/// specialized with the surface's struct. A frame that asks for one not made yet is incomplete
/// (<see cref="IsFrameComplete"/>). An edited shader makes every pipeline
/// again: each keeps drawing as it was until its new one is made, then the new one takes its place. A surface that does
/// not compile is broken (<see cref="FailureHandler.Break"/>), and its materials drawn as the failure; a pass whose
/// shader does not compile is said on the banner and left out. Render thread, but for the compiles.
/// </summary>
internal sealed class GpuPipelines : IDisposable
{
    private const string MaterialsModule = "Shaders/Renderer/Materials.slang";

    private readonly IGpuDriver _gpu;
    private readonly IShaderCompiler _compiler;
    private readonly Assets _assets;
    private readonly FailureHandler _failures;
    private readonly CancellationToken _stopping;
    private readonly Handle<Shader> _materialsModule;
    private readonly Dictionary<ShaderEntry, Entry> _compute = new(ShaderEntryComparer.Instance);
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
        _materialsModule = assets.Find<Shader>(MaterialsModule);
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
    internal GpuPipeline GetCompute(ShaderEntry shader)
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

        foreach (Entry entry in _materials.Values)
        {
            entry.MarkStale();
        }

        _failed.Clear();
    }

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

    private async Task CompileAsync(Entry entry, ShaderEntry shader)
    {
        entry.IsCompiling = true;
        entry.IsStale = false;
        string name = shader.EntryPoint
            ?? Path.GetFileName(_assets.GetPath(shader.Module) ?? $"shader {shader.Module.Id}");
        Result<GpuShader> compute = shader.Module == Handle<Shader>.None
            ? Result.Failure<GpuShader>("no shader is named.")
            : await _compiler.CompileAsync(shader, GpuShaderFormat.Spirv, _stopping).ConfigureAwait(false);
        _compiled.Writer.TryWrite(compute.Ok
            ? new Compiled(entry, name, null, compute.Value, null, null)
            : new Compiled(entry, name, null, null, compute.Error, null));
    }

    private async Task CompileAsync(Entry entry, MaterialKey key)
    {
        entry.IsCompiling = true;
        entry.IsStale = false;
        Handle<Shader>? surface = key.Surface.Shader != Handle<Shader>.None ? key.Surface.Shader : null;
        (ShaderEntry vertexEntry, ShaderEntry fragmentEntry) = GetMaterialEntries(key.Pass, key.Surface.Shader);
        Task<Result<GpuShader>> vertex = _compiler.CompileAsync(vertexEntry, GpuShaderFormat.Spirv, _stopping);
        Result<GpuShader> fragment = await _compiler.CompileAsync(fragmentEntry, GpuShaderFormat.Spirv, _stopping)
            .ConfigureAwait(false);
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
    /// The entry points a material pipeline draws with: for the visibility buffer, with a surface its clipped ones,
    /// specialized with it, and without one the pieces' places alone; for the G-buffer, the tiles and the surface.
    /// </summary>
    private (ShaderEntry Vertex, ShaderEntry Fragment) GetMaterialEntries(MaterialPass pass, Handle<Shader> surface)
    {
        if (pass == MaterialPass.Resolve)
        {
            return (
                new ShaderEntry(_materialsModule, "DrawTiles"),
                new ShaderEntry(_materialsModule, "ResolveSurfaces") { Types = [GetType(surface)] });
        }

        return surface == Handle<Shader>.None
            ? (new ShaderEntry(_materialsModule, "DrawPiecePlaces"),
                new ShaderEntry(_materialsModule, "WriteVisibility"))
            : (new ShaderEntry(_materialsModule, "DrawPieces"),
                new ShaderEntry(_materialsModule, "WriteClippedVisibility") { Types = [GetType(surface)] });
    }

    /// <summary>
    /// The struct a surface's module declares, named like its file.
    /// </summary>
    private ShaderType GetType(Handle<Shader> module) => ShaderType.Of(module, _assets.GetPath(module) ?? string.Empty);

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
    /// Shader entries as the same pipeline: the same module, entry point, types and modules beside it, the lists
    /// compared item by item.
    /// </summary>
    private sealed class ShaderEntryComparer : IEqualityComparer<ShaderEntry>
    {
        internal static ShaderEntryComparer Instance { get; } = new();

        public bool Equals(ShaderEntry x, ShaderEntry y)
            => x.Module == y.Module
                && string.Equals(x.EntryPoint, y.EntryPoint, StringComparison.Ordinal)
                && x.Types.SequenceEqual(y.Types)
                && x.Modules.SequenceEqual(y.Modules);

        public int GetHashCode(ShaderEntry obj)
        {
            HashCode hash = default;
            hash.Add(obj.Module);
            hash.Add(obj.EntryPoint, StringComparer.Ordinal);
            foreach (ShaderType type in obj.Types)
            {
                hash.Add(type);
            }

            foreach (Handle<Shader> module in obj.Modules)
            {
                hash.Add(module);
            }

            return hash.ToHashCode();
        }
    }

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
