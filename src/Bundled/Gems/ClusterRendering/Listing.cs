using System.Globalization;
using System.Text;
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

    public void Dispose()
    {
        _isDisposed = true;
        foreach (ListedPass pass in Passes)
        {
            gpu.Release(pass.Pipeline);
            gpu.Release(pass.Clear);
            foreach (GpuPipeline surface in pass.Surfaces.Values)
            {
                gpu.Release(surface);
            }
        }
    }

    public List<ListedPass> Passes { get; } = [];

    /// <summary>
    /// The passes of each stage, in the order they run, by <see cref="PassStage"/>.
    /// </summary>
    public List<ListedPass>[] Stages { get; } = NewStages();

    /// <summary>
    /// What the passes make, by name.
    /// </summary>
    public Dictionary<string, CreatedResource> Created { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Pipelines still to come back, and the build itself until it has asked for them all: one from the start, so a
    /// listing is not taken for compiled before it has been fitted.
    /// </summary>
    public int Pending { get; set; } = 1;

    public bool IsCompiled => Pending == 0;

    /// <summary>
    /// Whether the pass that makes <paramref name="name"/> runs: it fitted and its pipeline is made.
    /// </summary>
    public bool Runs(string name)
    {
        foreach (ListedPass pass in Passes)
        {
            foreach (PassCreate create in pass.Pass.Creates)
            {
                if (string.Equals(create.Name, name, StringComparison.Ordinal))
                {
                    return pass.IsReady;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the passes in frame order and keeps those whose every name stands for something by then: the renderer's, or
    /// made by a pass before it that it can see (what a stage that runs once a frame makes, every pass after it sees; what
    /// a view's stage makes, the passes after it in that view). One that does not fit is said once and left out, and so
    /// is what it would make. Then counts the pipelines to wait for: a pass's own, and a draw's for the
    /// <paramref name="variants"/> it draws (<see cref="ListedPass.VariantsOf"/>).
    /// </summary>
    public void Fit(List<Loaded> loaded, SceneVariants variants)
    {
        Dictionary<string, ResourceInfo> names = new(ResourceInfo.Engine, StringComparer.Ordinal);
        foreach (PassStage stage in Enum.GetValues<PassStage>())
        {
            bool isFrame = stage < PassStage.Cull;
            foreach (Loaded pass in loaded)
            {
                if (pass.Pass.Stage != stage)
                {
                    continue;
                }

                ListedPass listed = new(pass);
                Dictionary<string, PassCreate> made = new(StringComparer.Ordinal);
                foreach (PassCreate create in pass.Pass.Creates)
                {
                    made[create.Name] = create;
                }

                if (listed.Bind(names, made, isFrame) is { } why)
                {
                    Debugging.Error($"Pass {pass.Name} does not run: {why}");
                    continue;
                }

                foreach ((string name, PassCreate create) in made)
                {
                    ResourceInfo info = ResourceInfo.Of(create, isFrame);
                    names[name] = info;
                    Created[name] = new CreatedResource(create, info);
                    if (create.History)
                    {
                        names[ResourceInfo.HistoryOf(name)] = info with { Name = ResourceInfo.HistoryOf(name) };
                    }
                }

                Stages[(int)stage].Add(listed);
                Passes.Add(listed);
            }
        }

        foreach (ListedPass pass in Passes)
        {
            pass.Use(Created);
            pass.UseDispatch(Created);
        }

        Pending = 1 + Passes.Count;
        foreach (ListedPass pass in Passes)
        {
            if (!pass.DrawsSurfaces)
            {
                continue;
            }

            foreach (SurfaceKey variant in pass.VariantsOf(variants))
            {
                pass.Surfaces[variant] = GpuPipeline.None;
                Pending++;
            }
        }
    }

    /// <summary>
    /// A pass's own pipeline came back, or why not, which is said once and leaves it out.
    /// </summary>
    public void Take(ListedPass pass, Result<GpuPipeline> made)
    {
        Pending--;
        if (!made.Ok)
        {
            Debugging.Error($"Pass {pass.Name} does not run: {made.Error}");
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
    public void TakeSurface(ListedPass pass, SurfaceKey variant, Result<GpuPipeline> made, bool isCounted)
    {
        if (isCounted)
        {
            Pending--;
        }

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
    }

    private static List<ListedPass>[] NewStages()
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
    public GpuTextureUsage TextureUsage { get; set; }

    public GpuBufferUsage BufferUsage { get; set; }
}

/// <summary>
/// A pass as a listing runs it: its file, its packed params, what each name it binds stands for in binding order, and
/// its pipelines.
/// </summary>
internal sealed class ListedPass(Loaded loaded)
{
    public Pass Pass => loaded.Pass;

    /// <summary>
    /// Its file name without the extension, which settings and pipelines name its params by.
    /// </summary>
    public string Name => loaded.Name;

    public string Text => loaded.Text;

    public ParamLayout? Layout => loaded.Layout;

    public PassWords Params => loaded.Params;

    public List<PassBinding> Sampled { get; } = [];

    /// <summary>
    /// The textures it reads texel by texel with no sampler (<see cref="ResourceInfo.IsStorageRead"/>).
    /// </summary>
    public List<PassBinding> StorageReads { get; } = [];

    public List<PassBinding> Buffers { get; } = [];

    public List<PassBinding> TextureWrites { get; } = [];

    public List<PassBinding> BufferWrites { get; } = [];

    /// <summary>
    /// A draw's colour targets' formats, in order.
    /// </summary>
    public List<GpuFormat> ColorFormats { get; } = [];

    /// <summary>
    /// A draw of surfaces or the sky, whose pipelines are composed around them, one per variant.
    /// </summary>
    public bool DrawsSurfaces => Pass.Kind == PassKind.Draw && Pass.Draw.Drawn != PassDrawn.Depth;

    /// <summary>
    /// A draw into a cube: the sky captured, a face an iteration.
    /// </summary>
    public bool DrawsCube { get; private set; }

    public GpuPipeline Pipeline { get; set; }

    /// <summary>
    /// A tiled draw's: depth 0, the farthest, everywhere it draws, which empties its tile before it draws into it.
    /// </summary>
    public GpuPipeline Clear { get; set; }

    /// <summary>
    /// A compute shader's threads per group, which its groups are counted by.
    /// </summary>
    public (uint X, uint Y, uint Z) Threads { get; set; } = (1, 1, 1);

    /// <summary>
    /// The vertex stage a draw of surfaces or the sky shares across its pipelines.
    /// </summary>
    public GpuShader? Vertex { get; set; }

    /// <summary>
    /// A draw of the visibility buffer's vertex stage for the bins that do not clip, which pass on only where a piece's
    /// triangles are.
    /// </summary>
    public GpuShader? PlaceVertex { get; set; }

    /// <summary>
    /// A draw's pipelines by the variant they are for; none while one compiles.
    /// </summary>
    public Dictionary<SurfaceKey, GpuPipeline> Surfaces { get; } = [];

    public bool IsReady { get; set; }

    /// <summary>
    /// Resolves what the pass reads, writes and draws with, in binding order; why not, when a name stands for nothing it
    /// can see by then, or the pass is not one the renderer runs.
    /// </summary>
    public string? Bind(Dictionary<string, ResourceInfo> names, Dictionary<string, PassCreate> made, bool isFrame)
    {
        foreach (string name in made.Keys)
        {
            if (names.ContainsKey(name))
            {
                return $"it makes {name}, which the renderer or a pass before it already has.";
            }
        }

        foreach (PassRead read in Pass.Reads)
        {
            if (Made(names, made, read.Name, isFrame) is not { } info)
            {
                if (read.Optional)
                {
                    continue;
                }

                return $"it reads {read.Name}, which neither it nor anything before it makes.";
            }

            (info.IsBuffer ? Buffers : info.IsStorageRead ? StorageReads : Sampled).Add(new PassBinding(info, read.Filter, 0, 0, GpuLoad.Load, false));
        }

        foreach (PassWrite write in Pass.Writes)
        {
            if (Made(names, made, write.Name, isFrame) is not { } info)
            {
                return $"it writes {write.Name}, which neither it nor anything before it makes.";
            }

            (info.IsBuffer ? BufferWrites : TextureWrites).Add(new PassBinding(info, PassFilter.Default, write.Level, write.Layer, write.Load, write.Mipmaps));
        }

        if (Pass.Kind == PassKind.Draw)
        {
            foreach (string color in Pass.Draw.Colors)
            {
                if (Made(names, made, color, isFrame) is not { } info)
                {
                    return $"it draws into {color}, which neither it nor anything before it makes.";
                }

                ColorFormats.Add(info.Format);
                DrawsCube |= info.Shape == GpuTextureKind.Cube;
            }

            foreach (string name in (string[])[Pass.Draw.Depth, Pass.Draw.Args, Pass.Draw.Instances])
            {
                if (name.Length > 0 && Made(names, made, name, isFrame) is null)
                {
                    return $"it draws with {name}, which neither it nor anything before it makes.";
                }
            }
        }

        if (Pass.Kind == PassKind.Compute && Pass.Dispatch.Args.Length > 0 && Made(names, made, Pass.Dispatch.Args, isFrame) is null)
        {
            return $"it dispatches by {Pass.Dispatch.Args}, which neither it nor anything before it makes.";
        }

        return Pass.Kind switch
        {
            PassKind.Compute when Pass.Shader == Handle<Shader>.None => "a compute pass names its shader.",
            PassKind.Compute when TextureWrites.Count + BufferWrites.Count == 0 => "a compute pass writes something.",
            PassKind.Fullscreen when Pass.Shader == Handle<Shader>.None => "a fullscreen pass names its fragment shader.",
            PassKind.Fullscreen when TextureWrites.Count != 1 || BufferWrites.Count > 0 => "a fullscreen pass writes one texture, its target.",
            PassKind.Draw when Pass.Draw.Drawn == PassDrawn.Depth && Pass.Shader == Handle<Shader>.None => "a depth draw names its vertex shader.",
            PassKind.Draw when Pass.Draw.Drawn is PassDrawn.Visibility or PassDrawn.Surfaces or PassDrawn.Depth && (Pass.Draw.Args.Length == 0 || Pass.Draw.Instances.Length == 0) => "a draw of pieces or tiles names the arguments and instances a pass before it made.",
            PassKind.Draw when TextureWrites.Count + BufferWrites.Count > 0 => "a draw pass draws into its colours and depth, and writes nothing else.",
            _ => null,
        };
    }

    /// <summary>
    /// The surface variants a draw's pipelines are made for up front, those it draws: the raster bins' for a draw of the
    /// visibility buffer, the resolve bins' for a draw of surfaces, the transparent ones for a draw of transparent
    /// surfaces, the sky's for a draw of the sky, none for any other pass.
    /// </summary>
    public IReadOnlyCollection<SurfaceKey> VariantsOf(SceneVariants variants)
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
    /// Adds what this pass uses each thing it names that a pass makes for to what that thing is made with.
    /// </summary>
    public void Use(Dictionary<string, CreatedResource> created)
    {
        GpuBufferUsage read = Pass.Kind == PassKind.Compute ? GpuBufferUsage.ComputeRead : GpuBufferUsage.GraphicsRead;
        foreach (PassBinding binding in Sampled)
        {
            Add(created, binding.Info.Name, GpuTextureUsage.Sampler, GpuBufferUsage.None);
        }

        foreach (PassBinding binding in StorageReads)
        {
            Add(created, binding.Info.Name, Pass.Kind == PassKind.Compute ? GpuTextureUsage.ComputeRead : GpuTextureUsage.GraphicsRead, GpuBufferUsage.None);
        }

        foreach (PassBinding binding in Buffers)
        {
            Add(created, binding.Info.Name, GpuTextureUsage.None, read);
        }

        foreach (PassBinding binding in TextureWrites)
        {
            GpuTextureUsage usage = Pass.Kind == PassKind.Compute ? GpuTextureUsage.ComputeWrite : GpuTextureUsage.ColorTarget;
            Add(created, binding.Info.Name, usage | (binding.Mipmaps ? GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler : GpuTextureUsage.None), GpuBufferUsage.None);
        }

        foreach (PassBinding binding in BufferWrites)
        {
            Add(created, binding.Info.Name, GpuTextureUsage.None, GpuBufferUsage.ComputeWrite);
        }

        if (Pass.Kind != PassKind.Draw)
        {
            return;
        }

        foreach (string color in Pass.Draw.Colors)
        {
            Add(created, color, GpuTextureUsage.ColorTarget, GpuBufferUsage.None);
        }

        Add(created, Pass.Draw.Depth, GpuTextureUsage.DepthTarget, GpuBufferUsage.None);
        Add(created, Pass.Draw.Args, GpuTextureUsage.None, GpuBufferUsage.Indirect);
        Add(created, Pass.Draw.Instances, GpuTextureUsage.None, GpuBufferUsage.Vertex);
    }

    /// <summary>
    /// Adds what a compute pass that dispatches by a buffer uses it for: indirect arguments.
    /// </summary>
    public void UseDispatch(Dictionary<string, CreatedResource> created)
    {
        if (Pass.Kind == PassKind.Compute && Pass.Dispatch.Args.Length > 0)
        {
            Add(created, Pass.Dispatch.Args, GpuTextureUsage.None, GpuBufferUsage.Indirect);
        }
    }

    /// <summary>
    /// The declarations of what the pass binds, in the order the renderer binds it: its sampled textures with their
    /// samplers after the <paramref name="sampledBefore"/> the renderer binds first, then the textures it reads with no
    /// sampler, then <paramref name="firstBuffers"/>, the buffers the renderer binds, then the buffers it reads, then, for
    /// compute, the textures and buffers it writes.
    /// Then <c>KeepReadsAlive</c>, which reads each once, for a template to keep them bound however little its surface
    /// reads: the compiler drops what a shader does not use, and its count would no longer match the file's. Each read is
    /// defined first, <c>HAS_Name</c>, so a shader can do without an optional one the listing left out.
    /// </summary>
    public string Header(GpuStage stage, int sampledBefore, IReadOnlyList<string> firstBuffers)
    {
        StringBuilder header = new("#include \"Include/Frame.hlsli\"\n#include \"Include/Structs.hlsli\"\n");
        foreach (PassBinding binding in (List<PassBinding>)[.. Sampled, .. StorageReads, .. Buffers])
        {
            header.Append(CultureInfo.InvariantCulture, $"#define HAS_{binding.Info.Name} 1\n");
        }

        int read = sampledBefore;
        foreach (PassBinding binding in Sampled)
        {
            string sampler = binding.Filter == PassFilter.Comparison ? "SamplerComparisonState" : "SamplerState";
            header.Append(CultureInfo.InvariantCulture, $"{binding.Info.ReadType} {binding.Info.Name} : READ({read});\n{sampler} {binding.Info.Name}Sampler : SAMPLER({read});\n");
            read++;
        }

        foreach (PassBinding binding in StorageReads)
        {
            header.Append(CultureInfo.InvariantCulture, $"{binding.Info.ReadType} {binding.Info.Name} : READ({read++});\n");
        }

        foreach (string firstBuffer in firstBuffers)
        {
            header.Append(CultureInfo.InvariantCulture, $"{firstBuffer} : READ({read++});\n");
        }

        foreach (PassBinding binding in Buffers)
        {
            header.Append(CultureInfo.InvariantCulture, $"{binding.Info.ReadType} {binding.Info.Name} : READ({read++});\n");
        }

        // A surface template calls it where no draw goes, so every read stays bound whatever the surface uses.
        header.Append("float KeepReadsAlive(float2 uv)\n{\n    float nothing = 0.0;\n");
        foreach (PassBinding binding in (List<PassBinding>)[.. Sampled, .. StorageReads, .. Buffers])
        {
            header.Append(CultureInfo.InvariantCulture, $"    nothing += {binding.Info.KeepAlive(binding.Filter)};\n");
        }

        header.Append("    return nothing;\n}\n");
        if (stage != GpuStage.Compute)
        {
            return header.ToString();
        }

        int write = 0;
        foreach (PassBinding binding in TextureWrites)
        {
            header.Append(CultureInfo.InvariantCulture, $"{binding.Info.WriteType} {binding.Info.Name} : WRITE({write++});\n");
        }

        foreach (PassBinding binding in BufferWrites)
        {
            header.Append(CultureInfo.InvariantCulture, $"{binding.Info.WriteType} {binding.Info.Name} : WRITE({write++});\n");
        }

        return header.ToString();
    }

    /// <summary>
    /// Why a compiled shader binds other than the file says.
    /// </summary>
    public string Mismatch(uint textures, uint buffers, uint writes)
    {
        return $"its shader uses {textures} textures, {buffers} buffers and {writes} writes, where its file reads {Sampled.Count + StorageReads.Count} textures and {Buffers.Count} buffers and writes {TextureWrites.Count + BufferWrites.Count}: "
            + "the compiler drops what a shader does not use, so a file names only what its shader uses.";
    }

    private static ResourceInfo? Find(Dictionary<string, ResourceInfo> names, string name, bool isFrame)
    {
        return names.TryGetValue(name, out ResourceInfo? info) && (info.IsFrame || !isFrame) ? info : null;
    }

    /// <summary>
    /// What a name stands for to a pass: what it makes itself, or the last frame's copy of what it keeps from frame to
    /// frame, or what the renderer or a pass before it has.
    /// </summary>
    private static ResourceInfo? Made(Dictionary<string, ResourceInfo> names, Dictionary<string, PassCreate> made, string name, bool isFrame)
    {
        if (made.TryGetValue(name, out PassCreate? create))
        {
            return ResourceInfo.Of(create, isFrame);
        }

        foreach (PassCreate kept in made.Values)
        {
            if (kept.History && string.Equals(ResourceInfo.HistoryOf(kept.Name), name, StringComparison.Ordinal))
            {
                return ResourceInfo.Of(kept, isFrame) with { Name = name };
            }
        }

        return Find(names, name, isFrame);
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
internal sealed record SceneVariants(IReadOnlyCollection<SurfaceKey> Raster, IReadOnlyCollection<SurfaceKey> Resolve, IReadOnlyCollection<SurfaceKey> Transparent, SurfaceKey Sky);

/// <summary>
/// One name a pass binds: what it stands for, how it is sampled, and for a write the level and layer, what happens to
/// what was there, and whether its mips are made after.
/// </summary>
internal readonly record struct PassBinding(ResourceInfo Info, PassFilter Filter, uint Level, uint Layer, GpuLoad Load, bool Mipmaps);
