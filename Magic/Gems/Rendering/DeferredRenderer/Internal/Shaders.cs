using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Contexts.Threading;
using Magic.Services;
using Magic.Utils;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace DeferredRendererGem;

/// <summary>
/// Shader text and shader bytecode. Text: the <see cref="Shader"/> assets in use, by id, each with its include closure and,
/// for a surface shader, its parsed <see cref="SurfaceSource"/>; the asset manager keeps nothing and the compiler reads
/// includes from disk on its own. A change to a shader or an include drops every entry built on it (<see cref="Invalidate"/>)
/// and whoever used them asks again. The text itself is loaded ahead of this thread and handed over (<see cref="Preloads"/>). Bytecode: the compiler gem compiles (<see cref="Magic.Interfaces.IShaderCompiler.Compile"/>), and the
/// result is cached on disk by a hash of everything that went in, so a second start never compiles. Compiling is safe from
/// any thread; the text cache is main thread only. Compile errors come back as text with the shader's name; only
/// <see cref="CompileBuiltIn"/> throws.
/// </summary>
internal static class Shaders
{
    /// <summary>
    /// A cache entry starts with the shader's nine counts (samplers to thread counts), then its code.
    /// </summary>
    private const int CountsSize = 9 * sizeof(uint);

    public static Shader? Get(RenderContext ctx, Handle<Shader> handle)
    {
        return Cached(ctx, handle.Id)?.Shader;
    }

    /// <summary>
    /// <paramref name="path"/> relative to the shader root, like an <c>#include</c> writes it.
    /// </summary>
    public static Shader? GetByPath(RenderContext ctx, string path)
    {
        ulong id = IdOf(ctx, path);
        if (id == 0)
        {
            Log.Warn($"Shader Shaders/{path} is not an indexed asset.");
            return null;
        }

        return Get(ctx, new Handle<Shader>(id));
    }

    /// <summary>
    /// The id of a shader by path, or 0 when it is not an indexed asset.
    /// </summary>
    public static ulong IdOf(RenderContext ctx, string path)
    {
        return ctx.Assets.Find<Shader>("Shaders/" + path).Id;
    }

    /// <summary>
    /// A hash over the shader's transitive includes' text, to salt the compile cache key with.
    /// </summary>
    public static string ClosureHash(RenderContext ctx, Shader shader)
    {
        return Cached(ctx, shader.Id)?.ClosureHash ?? "";
    }

    /// <summary>
    /// The parsed surface of a <c>.surf.hlsl</c>, or null when it is missing or was rejected (logged when it was read).
    /// </summary>
    public static SurfaceSource? Surface(RenderContext ctx, ulong id)
    {
        return Cached(ctx, id)?.Surface;
    }

    /// <summary>
    /// Forgets a changed shader and every cached shader whose closure has it; returns them all (itself included), since
    /// whatever was built on any of them must be built again.
    /// </summary>
    public static HashSet<ulong> Invalidate(RenderContext ctx, ulong id)
    {
        HashSet<ulong> affected = Affected(ctx, id);
        foreach (ulong shader in affected)
            ctx.Shaders.Entries.Remove(shader);

        return affected;
    }

    /// <summary>
    /// A shader and every cached shader whose closure has it: what a change to it reaches.
    /// </summary>
    public static HashSet<ulong> Affected(RenderContext ctx, ulong id)
    {
        HashSet<ulong> affected = [id];
        foreach (CachedShader cached in ctx.Shaders.Entries.Values)
        {
            if (Array.IndexOf(cached.Closure, id) >= 0)
                affected.Add(cached.Shader.Id);
        }

        return affected;
    }

    /// <summary>
    /// Whether a change to the shader reaches anything held here: it is cached itself, or in a cached shader's closure (a
    /// header nothing asks for by path, such as Include/Math.hlsli).
    /// </summary>
    public static bool IsUsed(RenderContext ctx, ulong id)
    {
        return ctx.Shaders.Entries.ContainsKey(id) || Affected(ctx, id).Count > 1;
    }

    /// <summary>
    /// Compiles one of the engine's own shaders by path, on this thread. Without them nothing is drawn, so a failure throws:
    /// the renderer is not used (or a reload of it fails) rather than a frame.
    /// </summary>
    public static CompiledShader CompileBuiltIn(RenderContext ctx, string path, GpuStage stage)
    {
        Shader shader = GetByPath(ctx, path) ?? throw new InvalidOperationException($"Shaders/{path} is not an indexed asset.");
        Result<CompiledShader> compiled = Compile(ctx, shader.Text, shader.Path, stage, ClosureHash(ctx, shader), CancellationToken.None);
        return compiled.Ok ? compiled.Payload : throw new InvalidOperationException(compiled.Message);
    }

    /// <summary>
    /// From the disk cache, or compiled by the renderer and stored. Any thread, reading only what never changes: the
    /// pipelines and passes run it on a worker (<see cref="CompileAsync"/>) and collect the result through their <see cref="Pending{TKey, TValue}"/>.
    /// </summary>
    /// <param name="salt">Anything else the result depends on (the hash of the includes' text), for the cache key.</param>
    /// <param name="cancel">Throws <see cref="OperationCanceledException"/> before the compile itself, which cannot be stopped once begun.</param>
    public static Result<CompiledShader> Compile(RenderContext ctx, string source, string name, GpuStage stage, string salt, CancellationToken cancel)
    {
        ShaderCache cache = ctx.Shaders;
        string key = CacheKey(cache, ShaderCache.Header + source, stage, salt); // the generated header's text, not where it is
        source = ShaderCache.Preamble(name) + Hlsl.Splice(source, name, include => ctx.Assets.Load(ctx.Assets.Find<Shader>("Shaders/" + include))?.Text);
        if (TryLoad(ctx, key, stage, out CompiledShader? cached))
            return Result<CompiledShader>.Success(cached);

        cancel.ThrowIfCancellationRequested();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        Result<CompiledShader> result = cache.Compiler.Compile(source, name, stage, cache.Format);
        if (!result.Ok)
            return result;

        Store(ctx, key, result.Payload);

        CompiledShader compiled = result.Payload;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Log.Verbose(
            $"Compiled {name} ({stage}, {cache.Format}) in {ms:F0} ms: samplers {compiled.Samplers}, storage textures {compiled.StorageTextures}, " +
            $"storage buffers {compiled.StorageBuffers}, uniforms {compiled.UniformBuffers}, rw {compiled.ReadWriteStorageTextures}/{compiled.ReadWriteStorageBuffers}.");

        return result;
    }

    /// <summary>
    /// <see cref="Compile"/> on a worker; one that has not begun when <paramref name="cancel"/> is cancelled never does.
    /// </summary>
    public static Task<Result<CompiledShader>> CompileAsync(RenderContext ctx, string source, string name, GpuStage stage, string salt, CancellationToken cancel)
    {
        return ctx.Threads.InvokeAsync(ThreadId.Worker, cancel => Compile(ctx, source, name, stage, salt, cancel), cancel);
    }

    /// <summary>
    /// What a finished compile came to: its result, or what it threw as a failure.
    /// </summary>
    public static Result<CompiledShader> Outcome(Task<Result<CompiledShader>> job)
    {
        return job.IsCompletedSuccessfully
            ? job.Result
            : Result<CompiledShader>.Failure(job.Exception?.GetBaseException().Message ?? "compile faulted");
    }

    /// <summary>
    /// The cache entry of a shader, loading it, its closure and its surface the first time; null when it is not an asset.
    /// </summary>
    private static CachedShader? Cached(RenderContext ctx, ulong id)
    {
        if (id == 0)
            return null;
        if (ctx.Shaders.Entries.TryGetValue(id, out CachedShader? cached))
            return cached;

        Shader? shader = Preloads.Get<Shader>(ctx, id);
        if (shader is null)
            return null;

        (string hash, ulong[] closure) = IncludeClosure(ctx, shader);
        SurfaceSource? surface = null;
        if (shader.Stage == ShaderStage.Surface)
        {
            Result<SurfaceSource> read = ReadSurface(id, shader.Path, shader.Text);
            if (read.Failed)
                Log.Error($"Surface shader rejected: {read.Message}");
            else
                surface = read.Payload;
        }

        cached = new CachedShader(shader, hash, closure, surface);
        ctx.Shaders.Entries[id] = cached;
        return cached;
    }

    /// <summary>
    /// Parses a surface shader's text: its <c>MaterialParams</c> struct becomes the layout materials are packed with, and
    /// the declarations DXC must not see (the defaults and annotations of that struct) are blanked out of the text the
    /// pipelines compile. Refused, with why, when the struct does not parse or there is no <c>EvaluateSurface</c>.
    /// </summary>
    private static Result<SurfaceSource> ReadSurface(ulong id, string path, string text)
    {
        Result<ParamLayout> layout = ParamLayout.Parse(text, "MaterialParams", allowTextures: true);
        if (layout.Failed)
            return Result<SurfaceSource>.Failure($"{path}: {layout.Message}");
        if (!text.Contains("EvaluateSurface", StringComparison.Ordinal))
            return Result<SurfaceSource>.Failure($"{path}: no EvaluateSurface(SurfaceInputs, MaterialParams) function.");

        string stripped = ParamLayout.BlankDefaults(text, layout.Payload.StructSpan);
        return Result<SurfaceSource>.Success(new SurfaceSource(id, path, stripped, layout.Payload));
    }

    /// <summary>
    /// Every include the shader reaches, transitively, and a hash over their paths and text.
    /// </summary>
    private static (string Hash, ulong[] Closure) IncludeClosure(RenderContext ctx, Shader shader)
    {
        XxHash128 hash = new();
        List<ulong> seen = [];
        Stack<Shader> pending = new();
        pending.Push(shader);

        while (pending.Count > 0)
        {
            foreach (string include in Hlsl.Includes(pending.Pop().Text))
            {
                ulong id = IdOf(ctx, include);
                if (id == 0 || seen.Contains(id) || Preloads.Get<Shader>(ctx, id) is not { } included)
                    continue;

                seen.Add(id);
                hash.Append(Encoding.UTF8.GetBytes(included.Path));
                hash.Append(Encoding.UTF8.GetBytes(included.Text));
                pending.Push(included);
            }
        }

        return (Convert.ToHexStringLower(hash.GetCurrentHash()), [.. seen]);
    }

    private static string CacheKey(ShaderCache cache, string source, GpuStage stage, string salt)
    {
        XxHash128 hash = new();
        hash.Append(MemoryMarshal.AsBytes(source.AsSpan())); // UTF-16 as it sits, no copy
        hash.Append(Encoding.UTF8.GetBytes($"|{stage}|{cache.Format}|shadercross4|{salt}|"));
        return Convert.ToHexStringLower(hash.GetCurrentHash());
    }

    private static bool TryLoad(RenderContext ctx, string key, GpuStage stage, [NotNullWhen(true)] out CompiledShader? shader)
    {
        shader = null;
        if (ctx.Shaders.Folder is not { } folder)
            return false;

        string path = ctx.Files.Combine(folder, key + ".bin");
        if (!ctx.Files.FileExists(path) || ctx.Files.ReadBinary(path) is not { Ok: true, Payload: { Length: > CountsSize } bytes })
            return false;

        ReadOnlySpan<uint> counts = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, CountsSize));
        shader = new CompiledShader
        {
            Code = bytes[CountsSize..],
            Stage = stage,
            Samplers = counts[0],
            StorageTextures = counts[1],
            StorageBuffers = counts[2],
            UniformBuffers = counts[3],
            ReadWriteStorageTextures = counts[4],
            ReadWriteStorageBuffers = counts[5],
            ThreadCountX = counts[6],
            ThreadCountY = counts[7],
            ThreadCountZ = counts[8],
        };
        return true;
    }

    private static void Store(RenderContext ctx, string key, CompiledShader shader)
    {
        if (ctx.Shaders.Folder is not { } folder)
            return;

        uint[] counts =
        [
            shader.Samplers, shader.StorageTextures, shader.StorageBuffers, shader.UniformBuffers, shader.ReadWriteStorageTextures,
            shader.ReadWriteStorageBuffers, shader.ThreadCountX, shader.ThreadCountY, shader.ThreadCountZ,
        ];
        string path = ctx.Files.Combine(folder, key + ".bin");
        if (ctx.Files.WriteBinary(path, [.. MemoryMarshal.AsBytes(counts.AsSpan()), .. shader.Code]) is { Failed: true } failed)
            Log.Warn($"Could not write {path} into the shader cache: {failed.Message}"); // a cache that cannot be written only costs time
    }
}
