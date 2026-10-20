using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.ClusterRendering;

/// <summary>
/// The passes of one build by stage, checked against each other, what they make, and their pipelines as they come back
/// compiled; released as a whole when a newer one replaces it. Render thread.
/// </summary>
internal sealed class Listing(IGpu gpu) : IDisposable
{
    private bool _isDisposed;

    /// <summary>
    /// Pipelines still to come back, and the build itself until it has asked for them all: one from the start, so a
    /// listing is not taken for compiled before it has been fitted. Counted down from whichever thread each came back
    /// on (<see cref="Done"/>).
    /// </summary>
    private int _pending = 1;

    public void Dispose()
    {
        _isDisposed = true;
        foreach (ListedPass pass in Passes)
        {
            gpu.Release(pass.Pipeline);
            foreach (GpuPipeline surface in pass.Surfaces.Values)
            {
                gpu.Release(surface);
            }

            (pass.Script as IDisposable)?.Dispose();
        }
    }

    internal List<ListedPass> Passes { get; } = [];

    /// <summary>
    /// The passes of each stage, in the order they run, by <see cref="PassStage"/>.
    /// </summary>
    internal List<ListedPass>[] Stages { get; } = MakeStages();

    /// <summary>
    /// What the passes make, by name.
    /// </summary>
    internal Dictionary<string, CreatedResource> Created { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Why a pass does not run or a pipeline does not load, a line each, which the frame says in words across every view
    /// (<see cref="Failures"/>) for as long as it draws with this listing; the whole of each is in the log.
    /// </summary>
    internal List<string> Failures { get; } = [];

    /// <summary>
    /// Every pipeline asked for has come back, and the build has asked for them all.
    /// </summary>
    internal bool IsCompiled => Volatile.Read(ref _pending) == 0;

    /// <summary>
    /// Whether the pass that makes <paramref name="name"/> runs: it fitted and its pipeline is made.
    /// </summary>
    internal bool HasPass(string name)
    {
        foreach (ListedPass pass in Passes)
        {
            foreach (PassCreate create in pass.Pass.Creates)
            {
                if (string.Equals(create.Name, name, StringComparison.Ordinal)) return pass.IsReady;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the passes in frame order and keeps those whose every name it writes, makes or draws with stands for
    /// something by then: the renderer's, or made by a pass before it that it can see (what a stage that runs once a
    /// frame makes, every pass after it sees; what a view's stage makes, the passes after it in that view), which are
    /// what its shaders can read (<see cref="ListedPass.Visible"/>). One that does not fit is said once and left out,
    /// and so is what it would make; <paramref name="failures"/> is what was already found not to load as the passes
    /// were. Then counts the pipelines to wait for: a pass's own, and a draw's for the <paramref name="variants"/> it
    /// draws (<see cref="ListedPass.GetVariants"/>).
    /// </summary>
    internal void Fit(List<Loaded> loaded, List<string> failures, SceneVariants variants)
    {
        Failures.AddRange(failures);
        List<ResourceInfo> names = [.. ResourceInfo.Engine.Values];
        foreach (PassStage stage in Enum.GetValues<PassStage>())
        {
            bool isFrame = stage < PassStage.Cull;
            bool isTarget = stage >= PassStage.Overlay;
            foreach (Loaded pass in loaded)
            {
                if (pass.Pass.Stage != stage) continue;

                ListedPass listed = new(pass, this);
                Dictionary<string, PassCreate> made = new(StringComparer.Ordinal);
                foreach (PassCreate create in pass.Pass.Creates)
                {
                    made[create.Name] = create;
                }

                if (listed.Bind(names, made, isFrame, isTarget) is { } why)
                {
                    Debugging.Error($"Pass {pass.Name} does not run: {why}");
                    Failures.Add($"Pass {pass.Name} does not run: {GetFirstLine(why)}");
                    continue;
                }

                foreach ((string name, PassCreate create) in made)
                {
                    ResourceInfo info = ResourceInfo.Of(create, isFrame, isTarget);
                    names.Add(info);
                    Created[name] = new CreatedResource(create, info);
                    if (create.History) names.Add(info with { Name = ResourceInfo.GetHistoryName(name) });
                }

                Stages[(int)stage].Add(listed);
                Passes.Add(listed);
            }
        }

        _pending = 1 + Passes.Count;
        foreach (ListedPass pass in Passes)
        {
            if (!pass.DrawsSurfaces) continue;

            foreach (SurfaceKey variant in pass.GetVariants(variants))
            {
                pass.Surfaces[variant] = GpuPipeline.None;
                _pending++;
            }
        }
    }

    /// <summary>
    /// Adds what each pass's shaders use each thing a pass makes for to what that thing is made with, once every pass
    /// knows what its shaders use (<see cref="ListedPass.GetBindings"/>).
    /// </summary>
    internal void Use()
    {
        foreach (ListedPass pass in Passes)
        {
            pass.Use(Created);
        }
    }

    /// <summary>
    /// One of the pipelines still to come back has, or the build has asked for them all. Any thread.
    /// </summary>
    internal void Done()
    {
        Interlocked.Decrement(ref _pending);
    }

    /// <summary>
    /// A pass's own pipeline came back, or why not, which is said once and leaves it out: it never runs, and what it
    /// would draw into is shown as the failure.
    /// </summary>
    internal void Take(ListedPass pass, Result<GpuPipeline> made)
    {
        Done();
        if (!made.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {made.Error}");
            Failures.Add($"Pass {pass.Name} does not run: {GetFirstLine(made.Error)}");
            pass.HasFailed = true;
            return;
        }

        if (_isDisposed)
        {
            gpu.Release(made.Value);
            return;
        }

        pass.Pipeline = made.Value;
        pass.IsReady = true;
    }

    /// <summary>
    /// A surface pipeline came back; one whose surface was not loaded yet (an empty failure) is asked for again.
    /// </summary>
    internal void TakeSurface(ListedPass pass, SurfaceKey variant, Result<GpuPipeline> made, bool isCounted)
    {
        if (isCounted) Done();

        if (made.Ok && _isDisposed)
        {
            gpu.Release(made.Value);
            return;
        }

        if (made.Ok)
        {
            pass.Surfaces[variant] = made.Value;
        }
        else if (made.Error.Length == 0)
        {
            pass.Surfaces.Remove(variant);
        }
        else
        {
            pass.Broken.Add(variant);
        }
    }

    /// <summary>
    /// The first line of an error, which is as much as a line across the view can say.
    /// </summary>
    internal static string GetFirstLine(string error)
    {
        int end = error.IndexOf('\n', StringComparison.Ordinal);
        return (end < 0 ? error : error[..end]).Trim();
    }

    private static List<ListedPass>[] MakeStages()
    {
        List<ListedPass>[] stages = new List<ListedPass>[Enum.GetValues<PassStage>().Length];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i] = [];
        }

        return stages;
    }
}

/// <summary>
/// A texture or buffer a pass makes: how its file says to make it, what it is, and what the passes use it for.
/// </summary>
internal sealed record CreatedResource(PassCreate Create, ResourceInfo Info)
{
    internal GpuTextureUsage TextureUsage { get; set; }

    internal GpuBufferUsage BufferUsage { get; set; }
}

/// <summary>
/// A pass as a listing runs it: its file, its packed params, the names its shaders can see and what each stage of them
/// binds, and its pipelines.
/// </summary>
internal sealed class ListedPass(Loaded loaded, Listing listing)
{
    private readonly StageBindings?[] _bound = new StageBindings?[3];

    internal Pass Pass => loaded.Pass;

    /// <summary>
    /// Its file name without the extension, which settings and pipelines name its params by.
    /// </summary>
    internal string Name => loaded.Name;

    /// <summary>
    /// The listing it runs in.
    /// </summary>
    internal Listing Listing => listing;

    /// <summary>
    /// Its shader's text as HLSL takes it, its params' loader in.
    /// </summary>
    internal string Body => loaded.Body;

    internal PassWords Params => loaded.Params;

    /// <summary>
    /// The texture a compute pass dispatched <c>output:Name</c> runs a thread per texel of; empty for any other dispatch.
    /// </summary>
    internal string DispatchOutput { get; } = loaded.Pass.Dispatch.Per.StartsWith("output:", StringComparison.Ordinal) ? loaded.Pass.Dispatch.Per["output:".Length..] : string.Empty;

    /// <summary>
    /// Every name its shaders can read, in the order the frame makes them: the renderer's, what the passes before it
    /// made that it can see, and its own.
    /// </summary>
    internal List<ResourceInfo> Visible { get; } = [];

    /// <summary>
    /// Why what its shaders use could not be found, which fails its pipelines; empty while it could.
    /// </summary>
    internal string ProbeError { get; set; } = string.Empty;

    internal List<PassBinding> TextureWrites { get; } = [];

    internal List<PassBinding> BufferWrites { get; } = [];

    /// <summary>
    /// A draw's colour targets' formats, in order.
    /// </summary>
    internal List<GpuFormat> ColorFormats { get; } = [];

    /// <summary>
    /// A draw of surfaces or the sky, whose pipelines are composed around them, one per variant.
    /// </summary>
    internal bool DrawsSurfaces => Pass.Kind == PassKind.Draw && Pass.Draw.Drawn != PassDrawn.Depth;

    /// <summary>
    /// A draw through the pieces' vertex stage: the visibility buffer's, or transparent surfaces forward.
    /// </summary>
    internal bool DrawsPieces => Pass.Kind == PassKind.Draw && Pass.Draw.Drawn is PassDrawn.Visibility or PassDrawn.Transparent;

    /// <summary>
    /// A draw from the arguments and instances a pass before it made: the visibility buffer's pieces or depth's, or the
    /// G-buffer's tiles.
    /// </summary>
    internal bool DrawsIndirect => Pass.Kind == PassKind.Draw && Pass.Draw.Drawn is PassDrawn.Visibility or PassDrawn.Surfaces or PassDrawn.Depth;

    /// <summary>
    /// A draw into a cube: the sky captured, a face an iteration.
    /// </summary>
    internal bool DrawsCube { get; private set; }

    internal GpuPipeline Pipeline { get; set; }

    /// <summary>
    /// A script pass's script, made for this listing; none for any other pass, or until it is made.
    /// </summary>
    internal IPipelineScript? Script { get; set; }

    /// <summary>
    /// Its pipeline could not be made, so it never runs: what it would draw into is shown as the failure instead
    /// (<see cref="PassRecorder"/>).
    /// </summary>
    internal bool HasFailed { get; set; }

    /// <summary>
    /// A compute shader's threads per group, which its groups are counted by.
    /// </summary>
    internal (uint X, uint Y, uint Z) Threads { get; set; } = (1, 1, 1);

    /// <summary>
    /// The vertex stage a draw of surfaces or the sky shares across its pipelines.
    /// </summary>
    internal GpuShader? Vertex { get; set; }

    /// <summary>
    /// A draw of the visibility buffer's vertex stage for the bins that do not clip, which pass on only where a piece's
    /// triangles are.
    /// </summary>
    internal GpuShader? PlaceVertex { get; set; }

    /// <summary>
    /// A draw's pipelines by the variant they are for; none while one compiles.
    /// </summary>
    internal Dictionary<SurfaceKey, GpuPipeline> Surfaces { get; } = [];

    /// <summary>
    /// The variants whose pipeline neither its surface nor the failure in its place made, which nothing waits for.
    /// </summary>
    internal HashSet<SurfaceKey> Broken { get; } = [];

    internal bool IsReady { get; set; }

    /// <summary>
    /// What a stage of it binds, once its shaders were probed; none before, or for a stage it has none of.
    /// </summary>
    internal StageBindings? GetBindings(GpuStage stage) => _bound[(int)stage];

    internal void SetBound(GpuStage stage, StageBindings? bound)
    {
        _bound[(int)stage] = bound;
    }

    /// <summary>
    /// Resolves what the pass writes and draws with, and gathers what it can see; why not, when a name stands for nothing
    /// it can see by then, or the pass is not one the renderer runs.
    /// </summary>
    internal string? Bind(List<ResourceInfo> names, Dictionary<string, PassCreate> made, bool isFrame, bool isTarget)
    {
        foreach (ResourceInfo info in names)
        {
            if (made.ContainsKey(info.Name))
            {
                return $"it makes {info.Name}, which the renderer or a pass before it already has.";
            }

            if (CanSee(info, isFrame, isTarget)) Visible.Add(info);
        }

        foreach (PassCreate create in made.Values)
        {
            ResourceInfo info = ResourceInfo.Of(create, isFrame, isTarget);
            Visible.Add(info);
            if (create.History) Visible.Add(info with { Name = ResourceInfo.GetHistoryName(create.Name) });
        }

        foreach (PassWrite write in Pass.Writes)
        {
            if (FindMade(names, made, write.Name, isFrame, isTarget) is not { } info)
            {
                return $"it writes {write.Name}, which neither it nor anything before it makes.";
            }

            (info.IsBuffer ? BufferWrites : TextureWrites).Add(new PassBinding(info, write.Level, write.Layer, write.Contents, write.Mipmaps));
        }

        if (Pass.Kind == PassKind.Draw)
        {
            foreach (string color in Pass.Draw.Colors)
            {
                if (FindMade(names, made, color, isFrame, isTarget) is not { } info)
                {
                    return $"it draws into {color}, which neither it nor anything before it makes.";
                }

                ColorFormats.Add(info.Format);
                DrawsCube |= info.Shape == GpuTextureKind.Cube;
            }

            foreach (string name in (string[])[Pass.Draw.Depth, Pass.Draw.Args, Pass.Draw.Instances])
            {
                if (name.Length > 0 && FindMade(names, made, name, isFrame, isTarget) is null)
                {
                    return $"it draws with {name}, which neither it nor anything before it makes.";
                }
            }
        }

        if (Pass.Kind == PassKind.Compute && Pass.Dispatch.Args.Length > 0 && FindMade(names, made, Pass.Dispatch.Args, isFrame, isTarget) is null)
        {
            return $"it dispatches by {Pass.Dispatch.Args}, which neither it nor anything before it makes.";
        }

        return Pass.Kind switch
        {
            PassKind.Compute when Pass.Shader == Handle<Shader>.None => "a compute pass names its shader.",
            PassKind.Compute when TextureWrites.Count + BufferWrites.Count == 0 => "a compute pass writes something.",
            PassKind.Fullscreen when Pass.Shader == Handle<Shader>.None => "a fullscreen pass names its fragment shader.",
            PassKind.Fullscreen when TextureWrites.Count != 1 || BufferWrites.Count > 0 => "a fullscreen pass writes one texture, its target.",
            PassKind.Fullscreen when TextureWrites[0].Info.IsTarget => $"only a script pass draws into {ResourceInfo.TargetName}, whose format is known only as it is drawn into; a fullscreen pass in the overlay stage writes what it makes.",
            PassKind.Script when Pass.Script == Handle<Script>.None => "a script pass names its script.",
            PassKind.Script when TextureWrites.Count != 1 || BufferWrites.Count > 0 => "a script pass writes one texture, its target.",
            PassKind.Draw when Pass.Draw.Drawn == PassDrawn.Depth && Pass.Shader == Handle<Shader>.None => "a depth draw names its vertex shader.",
            PassKind.Draw when DrawsIndirect && (Pass.Draw.Args.Length == 0 || Pass.Draw.Instances.Length == 0) => "a draw of pieces or tiles names the arguments and instances a pass before it made.",
            PassKind.Draw when TextureWrites.Count + BufferWrites.Count > 0 => "a draw pass draws into its colours and depth, and writes nothing else.",
            _ => null,
        };
    }

    /// <summary>
    /// The surface variants a draw's pipelines are made for up front, those it draws: the raster bins' for a draw of the
    /// visibility buffer, the resolve bins' for a draw of surfaces, the transparent ones for a draw of transparent
    /// surfaces, the sky's for a draw of the sky, none for any other pass.
    /// </summary>
    internal IReadOnlyCollection<SurfaceKey> GetVariants(SceneVariants variants)
    {
        return Pass.Kind != PassKind.Draw ? [] : Pass.Draw.Drawn switch
        {
            PassDrawn.Visibility => variants.Raster,
            PassDrawn.Surfaces => variants.Resolve,
            PassDrawn.Transparent => variants.Transparent,
            PassDrawn.Sky => [variants.Sky],
            PassDrawn.Depth => [],
            _ => [],
        };
    }

    /// <summary>
    /// Adds what this pass's shaders use each thing they name that a pass makes for to what that thing is made with, and
    /// what a compute pass that dispatches by a buffer uses it for: indirect arguments.
    /// </summary>
    internal void Use(Dictionary<string, CreatedResource> created)
    {
        foreach (StageBindings? bound in _bound)
        {
            if (bound is null) continue;

            GpuTextureUsage storage = Pass.Kind == PassKind.Compute ? GpuTextureUsage.ComputeRead : GpuTextureUsage.GraphicsRead;
            GpuBufferUsage read = Pass.Kind == PassKind.Compute ? GpuBufferUsage.ComputeRead : GpuBufferUsage.GraphicsRead;
            foreach (BoundName name in bound.Sampled)
            {
                Add(created, name.Binding.Info.Name, GpuTextureUsage.Sampled, GpuBufferUsage.None);
            }

            foreach (BoundName name in bound.StorageReads)
            {
                Add(created, name.Binding.Info.Name, storage, GpuBufferUsage.None);
            }

            foreach (BoundName name in bound.Buffers)
            {
                Add(created, name.Binding.Info.Name, GpuTextureUsage.None, read);
            }
        }

        foreach (PassBinding binding in TextureWrites)
        {
            GpuTextureUsage usage = Pass.Kind == PassKind.Compute ? GpuTextureUsage.ComputeWrite : GpuTextureUsage.ColorTarget;
            Add(created, binding.Info.Name, usage | (binding.Mipmaps ? GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampled : GpuTextureUsage.None), GpuBufferUsage.None);
        }

        foreach (PassBinding binding in BufferWrites)
        {
            Add(created, binding.Info.Name, GpuTextureUsage.None, GpuBufferUsage.ComputeWrite);
        }

        if (Pass.Kind == PassKind.Compute && Pass.Dispatch.Args.Length > 0)
        {
            Add(created, Pass.Dispatch.Args, GpuTextureUsage.None, GpuBufferUsage.Arguments);
        }

        if (Pass.Kind != PassKind.Draw) return;

        foreach (string color in Pass.Draw.Colors)
        {
            Add(created, color, GpuTextureUsage.ColorTarget, GpuBufferUsage.None);
        }

        Add(created, Pass.Draw.Depth, GpuTextureUsage.DepthTarget, GpuBufferUsage.None);
        Add(created, Pass.Draw.Args, GpuTextureUsage.None, GpuBufferUsage.Arguments);
        Add(created, Pass.Draw.Instances, GpuTextureUsage.None, GpuBufferUsage.Vertex);
    }

    /// <summary>
    /// What a stage's shader is given to write: a compute pass's writes, nothing for any other stage, which draws into
    /// its target.
    /// </summary>
    internal IReadOnlyList<PassBinding> GetWrites(GpuStage stage)
    {
        return stage == GpuStage.Compute ? [.. TextureWrites, .. BufferWrites] : [];
    }

    /// <summary>
    /// Whether a pass of a stage's scope can see a name: every pass the frame's; a view's stages the views'; a target's
    /// stages the targets'.
    /// </summary>
    private static bool CanSee(ResourceInfo info, bool isFrame, bool isTarget)
    {
        return info.IsFrame || (isTarget ? info.IsTarget : !isFrame && !info.IsTarget);
    }

    private static ResourceInfo? Find(List<ResourceInfo> names, string name, bool isFrame, bool isTarget)
    {
        foreach (ResourceInfo info in names)
        {
            if (string.Equals(info.Name, name, StringComparison.Ordinal) && CanSee(info, isFrame, isTarget))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>
    /// What a name stands for to a pass: what it makes itself, or the last frame's copy of what it keeps from frame to
    /// frame, or what the renderer or a pass before it has.
    /// </summary>
    private static ResourceInfo? FindMade(List<ResourceInfo> names, Dictionary<string, PassCreate> made, string name, bool isFrame, bool isTarget)
    {
        if (made.TryGetValue(name, out PassCreate? create)) return ResourceInfo.Of(create, isFrame, isTarget);

        foreach (PassCreate kept in made.Values)
        {
            if (kept.History && string.Equals(ResourceInfo.GetHistoryName(kept.Name), name, StringComparison.Ordinal))
            {
                return ResourceInfo.Of(kept, isFrame, isTarget) with { Name = name };
            }
        }

        return Find(names, name, isFrame, isTarget);
    }

    private static void Add(Dictionary<string, CreatedResource> created, string name, GpuTextureUsage textures, GpuBufferUsage buffers)
    {
        if (created.TryGetValue(name, out CreatedResource? resource))
        {
            resource.TextureUsage |= textures;
            resource.BufferUsage |= buffers;
        }
    }
}

/// <summary>
/// The surface variants the frame draws now, by how it draws them: the raster bins' into the visibility buffer (a
/// surface of none for the bins that do not clip), the resolve bins' into the G-buffer, transparent forward, and the
/// sky's.
/// </summary>
internal readonly record struct SceneVariants(IReadOnlyCollection<SurfaceKey> Raster, IReadOnlyCollection<SurfaceKey> Resolve, IReadOnlyCollection<SurfaceKey> Transparent, SurfaceKey Sky);

/// <summary>
/// One name a pass binds: what it stands for, and for a write the level and layer, what happens to what was there, and
/// whether its mips are made after.
/// </summary>
internal readonly record struct PassBinding(ResourceInfo Info, uint Level, uint Layer, GpuContents Contents, bool Mipmaps);
