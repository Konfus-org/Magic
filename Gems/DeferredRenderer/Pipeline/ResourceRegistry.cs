using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace DeferredRendererGem;

/// <summary>
/// What is known about a name a pass reads or writes: what kind of thing it is, the scope it lives in, its texture
/// format, and whether it is the engine's (always there) or a pass's.
/// </summary>
internal readonly record struct ResourceInfo(ResourceKind Kind, PassScope Scope, GpuFormat Format, bool IsEngine);

/// <summary>
/// A resource a pass created, as it is this frame: a buffer (two when it keeps a history), or a fixed texture or
/// volume. A texture that follows the render target's size is one of the target's <see cref="FrameTargets.Target"/>s.
/// </summary>
internal sealed class Resource
{
    public required PassCreate Create { get; init; }

    public required ulong Owner { get; init; }

    public GrowableBuffer? Buffer { get; set; }

    public GrowableBuffer? PreviousBuffer { get; set; }

    /// <summary>
    /// Bytes of one slot of the buffer: a pass that runs several times over its own slice offsets by this.
    /// </summary>
    public uint SliceBytes { get; set; }

    /// <summary>
    /// What the buffer was last sized for: when that changes its layout did, so a zeroed buffer is zeroed again.
    /// </summary>
    public uint LastBytes { get; set; }

    public GpuTexture Texture { get; set; }

    public uint Width { get; set; }

    public uint Height { get; set; }

    public uint Depth { get; set; }

    public GpuFormat Format { get; set; }
}

/// <summary>
/// The resources of one scope, by name.
/// </summary>
internal sealed class ResourceSet : Dictionary<string, Resource>
{
    public ResourceSet() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

/// <summary>
/// Every name a pass can read or write, and what stands behind it: the engine's own textures (a render target's) and
/// buffers (the tables), and what passes <see cref="Pass.Creates"/>, kept by scope: frame-wide here, per render target in
/// its <see cref="FrameTargets.Resources"/> (a texture sized to the target among its targets), per view in
/// <see cref="View"/>. <see cref="Ensure"/> makes a pass's resources fit this frame's counts before anything is
/// recorded; <see cref="ReleaseOwnedBy"/> drops them with the pass.
/// </summary>
internal sealed class ResourceRegistry
{
    private const string PreviousSuffix = "Previous";

    private static readonly string[] EngineTextures = ["Depth", "Emissive", "Albedo", "Normal", "Material", "Hdr", "Ldr"];

    /// <summary>
    /// The engine's own volume: the occupancy bricks of the meshes, frame-wide, written by the brick passes.
    /// </summary>
    public const string BrickAtlasName = "BrickAtlas";

    private static readonly (string Name, Func<RenderContext, (GpuBuffer Handle, uint Size)> Get, bool Writable)[] EngineBuffers =
    [
        ("Instances", ctx => Of(ctx.Instances.CullBuffer), false),
        ("Xforms", ctx => Of(ctx.Instances.XformBuffer), false),
        ("Cells", ctx => Of(ctx.Instances.CellBuffer), false),
        ("Pages", ctx => Of(ctx.Instances.PageBuffer), false),
        ("DrawTemplate", ctx => Of(ctx.Buckets.Template), false),
        ("Lods", ctx => Of(ctx.Buckets.Lods), false),
        ("GiGroups", ctx => Of(ctx.Buckets.GiGroups), false),
        ("Materials", ctx => Of(ctx.Materials.Records), false),
        ("GiMaterials", ctx => Of(ctx.Materials.GiRecords), false),
        ("Lights", ctx => Of(ctx.Lights), true),
        ("Glows", ctx => Of(ctx.Glows), false),
        ("MeshVertices", ctx => (ctx.Meshes.VertexBuffer, 0), false),
        ("MeshIndices", ctx => (ctx.Meshes.IndexBuffer, 0), false),
        ("Counts", ctx => Of(ctx.Counts), false),
        ("BrickJobs", ctx => Of(ctx.BrickJobs), false),
        ("BrickArgs", ctx => Of(ctx.BrickArgs), false),
    ];

    private readonly List<ResourceSet> _views = [];
    private byte[] _zeros = [];

    /// <summary>
    /// The frame-wide resources: what the shadow and GI stages create.
    /// </summary>
    public ResourceSet Frame { get; } = new();

    /// <summary>
    /// The resources of the view at <paramref name="index"/> in the frame's view list.
    /// </summary>
    public ResourceSet View(int index)
    {
        while (_views.Count <= index)
            _views.Add(new ResourceSet());

        return _views[index];
    }

    /// <summary>
    /// Every name the engine provides: its textures, then its buffers.
    /// </summary>
    public static readonly string[] EngineNames = [.. EngineTextures, BrickAtlasName, .. EngineBuffers.Select(buffer => buffer.Name)];

    private static readonly HashSet<string> EngineTextureSet = new(EngineTextures, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> EngineBufferIndex = new(EngineBuffers.Select((buffer, index) => KeyValuePair.Create(buffer.Name, index)), StringComparer.OrdinalIgnoreCase);

    public static bool IsEngineTexture(string name)
    {
        return EngineTextureSet.Contains(name);
    }

    public static bool IsEngineBuffer(string name)
    {
        return EngineBufferIndex.ContainsKey(name);
    }

    public static bool IsEngineVolume(string name)
    {
        return string.Equals(name, BrickAtlasName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEngineName(string name)
    {
        return IsEngineTexture(name) || IsEngineVolume(name) || IsEngineBuffer(name);
    }

    /// <summary>
    /// Whether a compute or fullscreen pass may write the engine texture: the lit scene, the shown image, the brick atlas.
    /// </summary>
    public static bool IsComputeWritable(string name)
    {
        return string.Equals(name, "Hdr", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Ldr", StringComparison.OrdinalIgnoreCase) || IsEngineVolume(name);
    }

    /// <summary>
    /// Whether a draw pass may draw into the engine texture: the gbuffer and its depth, and the lit scene.
    /// </summary>
    public static bool IsDrawTarget(string name)
    {
        return IsEngineTexture(name) && !string.Equals(name, "Ldr", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWritableEngineBuffer(string name)
    {
        return EngineBufferIndex.TryGetValue(name, out int index) && EngineBuffers[index].Writable;
    }

    /// <summary>
    /// The engine buffer by name; false for a name that is not one.
    /// </summary>
    public static bool TryEngineBuffer(RenderContext ctx, string name, out GpuBuffer buffer, out uint size)
    {
        if (!EngineBufferIndex.TryGetValue(name, out int index))
        {
            buffer = default;
            size = 0;
            return false;
        }

        (buffer, size) = EngineBuffers[index].Get(ctx);
        return true;
    }

    /// <summary>
    /// The engine's texture format for a file's.
    /// </summary>
    public static GpuFormat Format(PassFormat format, GpuFormat depthFormat)
    {
        return format switch
        {
            PassFormat.Rgba8Unorm => GpuFormat.Rgba8Unorm,
            PassFormat.Rgba16Float => GpuFormat.Rgba16Float,
            PassFormat.R16Float => GpuFormat.R16Float,
            PassFormat.R32Float => GpuFormat.R32Float,
            PassFormat.Rg16Float => GpuFormat.Rg16Float,
            PassFormat.B10G11R11Float => GpuFormat.R11G11B10Float,
            PassFormat.R8Unorm => GpuFormat.R8Unorm,
            PassFormat.R32Uint => GpuFormat.R32Uint,
            PassFormat.Rgba32Float => GpuFormat.Rgba32Float,
            PassFormat.Depth => depthFormat,
            _ => GpuFormat.Rgba8Srgb,
        };
    }

    /// <summary>
    /// What <paramref name="name"/> is: an engine name, or what the pass that creates it says, whichever pass of the
    /// listing that is; null for a name nothing stands behind. A <c>&lt;Name&gt;Previous</c> is its history.
    /// </summary>
    public static ResourceInfo? Describe(RenderContext ctx, PipelineState pipeline, string name)
    {
        GpuFormat depth = ctx.Gpu.DepthFormat;
        if (IsEngineTexture(name))
        {
            return new ResourceInfo(ResourceKind.Texture, PassScope.Target, EngineFormat(name, depth), IsEngine: true);
        }

        if (IsEngineVolume(name))
            return new ResourceInfo(ResourceKind.Volume, PassScope.Frame, GpuFormat.R8Unorm, IsEngine: true);

        if (IsEngineBuffer(name))
            return new ResourceInfo(ResourceKind.Buffer, PassScope.Frame, GpuFormat.Invalid, IsEngine: true);

        foreach (List<PassState> stage in pipeline.Stages)
        {
            foreach (PassState state in stage)
            {
                foreach (PassCreate create in state.Pass.Creates)
                {
                    bool matches = string.Equals(create.Name, name, StringComparison.OrdinalIgnoreCase)
                        || (create.History && string.Equals(create.Name + PreviousSuffix, name, StringComparison.OrdinalIgnoreCase));
                    if (!matches)
                        continue;

                    PassScope scope = create.Kind == ResourceKind.Texture && create.Scale > 0f ? PassScope.Target : PassState.ScopeOf(state.Stage);
                    GpuFormat format = Format(create.Format, depth);
                    return new ResourceInfo(create.Kind, scope, format, IsEngine: false);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Makes everything <paramref name="pass"/> creates fit this frame in <paramref name="set"/> (its scope's), sized by
    /// <paramref name="counts"/>: a texture that follows the render target becomes one of <paramref name="targets"/>'
    /// (in use, with its twin when the pass reads it too); a buffer grows, is seeded and reset as its file says; a
    /// history turns over. False, with the problem said once, when a target of that name is already in use by an
    /// earlier pass in another format or scale: the pass is then skipped this frame.
    /// </summary>
    public bool Ensure(RenderContext ctx, PipelineState pipeline, PassState pass, ResourceSet set, FrameTargets? targets, in FrameCounts counts)
    {
        IRendering gpu = ctx.Gpu;
        foreach (PassCreate create in pass.Pass.Creates)
        {
            if (create.Kind == ResourceKind.Texture && create.Scale > 0f)
            {
                if (targets is null)
                    continue; // a frame-wide pass cannot make one: the validator refused it
                if (!EnsureScaled(gpu, pipeline, pass, targets, create))
                    return false;
                continue;
            }

            if (!set.TryGetValue(create.Name, out Resource? resource))
                set[create.Name] = resource = new Resource { Create = create, Owner = pass.Id };

            if (create.Kind == ResourceKind.Buffer)
                EnsureBuffer(ctx, resource, counts);
            else
                EnsureFixed(gpu, resource);
        }

        // A texture the pass both reads and writes is written into its twin, then the two swap.
        if (targets is not null && pass.PingPong)
        {
            foreach (PassWrite write in pass.Pass.Writes)
            {
                if (!ReadsName(pass.Pass, write.Name) || targets.Get(write.Name) is not { } output)
                    continue;

                targets.TwinOf(gpu, output);
                output.TwinInUse = true;
            }
        }

        return true;
    }

    /// <summary>
    /// Drops every resource the pass made, in every scope, and the targets it defined go with the frame's unused ones.
    /// </summary>
    public void ReleaseOwnedBy(RenderContext ctx, ulong passId)
    {
        Release(ctx.Gpu, Frame, passId);
        foreach (ResourceSet view in _views)
            Release(ctx.Gpu, view, passId);
        foreach (FrameTargets targets in ctx.Targets.Values)
            Release(ctx.Gpu, targets.Resources, passId);
    }

    public static void ReleaseSet(IRendering gpu, ResourceSet set)
    {
        foreach (Resource resource in set.Values)
            ReleaseResource(gpu, resource);

        set.Clear();
    }

    private static void Release(IRendering gpu, ResourceSet set, ulong passId)
    {
        foreach ((string name, Resource resource) in set)
        {
            if (resource.Owner != passId)
                continue;

            ReleaseResource(gpu, resource);
            set.Remove(name);
        }
    }

    private static void ReleaseResource(IRendering gpu, Resource resource)
    {
        if (resource.Buffer is { } buffer)
            gpu.Release(buffer.Handle);
        if (resource.PreviousBuffer is { } previous)
            gpu.Release(previous.Handle);

        gpu.Release(resource.Texture);
    }

    private bool EnsureScaled(IRendering gpu, PipelineState pipeline, PassState pass, FrameTargets targets, PassCreate create)
    {
        GpuFormat format = Format(create.Format, gpu.DepthFormat);
        FrameTargets.Target? existing = targets.Get(create.Name);
        bool differs = existing is null || existing.Format != format || existing.Scale != create.Scale;
        if (differs && existing is { InUse: true })
        {
            pipeline.Problem($"Pass {pass.Path} skipped for {targets.RenderTarget}: {create.Name} is written by an earlier pass in another format or scale; a target has one of each.");
            return false;
        }

        GpuTextureUsage usage = create.Format == PassFormat.Depth
            ? GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler
            : GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite | GpuTextureUsage.ComputeRead;
        FrameTargets.Target target = targets.Define(gpu, create.Name, format, usage, create.Scale);
        targets.Ensure(gpu, targets.Width, targets.Height);
        target.InUse = true;
        return true;
    }

    private void EnsureBuffer(RenderContext ctx, Resource resource, in FrameCounts counts)
    {
        IRendering gpu = ctx.Gpu;
        PassCreate create = resource.Create;
        uint slice = create.Bytes;
        if (create.Per.Length > 0 && counts.TryResolve(create.Per, out uint per))
            slice = per * create.Stride;

        slice = Math.Max(slice + create.Plus, create.Min);
        uint slots = Math.Max(1, create.Slots);
        uint total = slice * slots;
        GpuBufferUsage usage = GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead;
        if (create.Usage == BufferUsage.Indirect)
            usage |= GpuBufferUsage.Indirect;
        else if (create.Usage == BufferUsage.Vertex)
            usage |= GpuBufferUsage.Vertex;

        if (create.History)
            (resource.Buffer, resource.PreviousBuffer) = (resource.PreviousBuffer, resource.Buffer);

        bool resized = resource.LastBytes != total;
        resource.LastBytes = total;
        resource.SliceBytes = slice;
        resource.Buffer = Grow(gpu, resource.Buffer, usage, total, create.Zero, resized);
        if (create.History)
            resource.PreviousBuffer = Grow(gpu, resource.PreviousBuffer, usage, total, create.Zero, resized);

        GpuBuffer handle = resource.Buffer.Handle;
        if (create.Seed.Length > 0 && TryEngineBuffer(ctx, create.Seed, out GpuBuffer seed, out uint seedSize))
        {
            uint bytes = seedSize == 0 ? slice : Math.Min(seedSize, slice);
            for (uint slot = 0; slot < slots; slot++)
                gpu.Copy(seed, 0, handle, slot * slice, bytes);
        }

        if (create.Reset.Length == 0)
            return;

        Span<uint> words = stackalloc uint[create.Reset.Length];
        for (int i = 0; i < words.Length; i++)
            words[i] = ResetWord(create.Reset[i], counts);
        for (uint slot = 0; slot < slots; slot++)
            gpu.Upload<uint>(handle, slot * slice, words);
    }

    /// <summary>
    /// The buffer with room for <paramref name="bytes"/>; a zeroed one is zeroed when it is made and whenever its layout
    /// changed (<paramref name="resized"/>), since what it held means nothing in the new layout.
    /// </summary>
    private GrowableBuffer Grow(IRendering gpu, GrowableBuffer? buffer, GpuBufferUsage usage, uint bytes, bool zero, bool resized)
    {
        bool fresh;
        if (buffer is null)
        {
            buffer = new GrowableBuffer(gpu, usage, bytes);
            fresh = true;
        }
        else
            fresh = buffer.Ensure(gpu, bytes);

        if ((fresh || resized) && zero)
        {
            if (_zeros.Length < buffer.Size)
                _zeros = new byte[buffer.Size];

            gpu.Upload(buffer.Handle, 0, _zeros.AsSpan(0, (int)buffer.Size));
        }

        return buffer;
    }

    private static void EnsureFixed(IRendering gpu, Resource resource)
    {
        PassCreate create = resource.Create;
        GpuFormat format = Format(create.Format, gpu.DepthFormat);
        bool isVolume = create.Kind == ResourceKind.Volume;
        uint width = create.Size.Length > 0 ? Math.Max(1, create.Size[0]) : 1;
        uint height = create.Size.Length > 1 ? Math.Max(1, create.Size[1]) : 1;
        uint depth = isVolume ? (create.Size.Length > 2 ? Math.Max(1, create.Size[2]) : 1) * Math.Max(1, create.Levels) : 1;
        if (resource.Texture.IsValid && resource.Width == width && resource.Height == height && resource.Depth == depth && resource.Format == format)
            return;

        gpu.Release(resource.Texture);
        GpuTextureUsage usage = create.Format == PassFormat.Depth
            ? GpuTextureUsage.DepthTarget | GpuTextureUsage.Sampler
            : GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite | GpuTextureUsage.ComputeRead | (isVolume ? GpuTextureUsage.None : GpuTextureUsage.ColorTarget);
        TextureDesc desc = new(format, usage, width, height, Layers: depth, Kind: isVolume ? GpuTextureKind.Texture3D : GpuTextureKind.Texture2D);
        resource.Texture = gpu.CreateTexture(desc);
        resource.Width = width;
        resource.Height = height;
        resource.Depth = depth;
        resource.Format = format;
    }

    private static uint ResetWord(string word, in FrameCounts counts)
    {
        if (counts.TryResolve(word, out uint count))
            return count;

        return uint.TryParse(word, out uint value) ? value : 0;
    }

    private static bool ReadsName(Pass pass, string name)
    {
        foreach (PassRead read in pass.Reads)
        {
            if (string.Equals(read.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static GpuFormat EngineFormat(string name, GpuFormat depth)
    {
        return name.ToUpperInvariant() switch
        {
            "DEPTH" => depth,
            "EMISSIVE" => GBuffer.EmissiveFormat,
            "ALBEDO" => GBuffer.AlbedoFormat,
            "NORMAL" => GBuffer.NormalFormat,
            "MATERIAL" => GBuffer.MaterialFormat,
            "HDR" => FrameTargets.HdrFormat,
            _ => FrameTargets.LdrFormat,
        };
    }

    private static (GpuBuffer Handle, uint Size) Of(GrowableBuffer buffer)
    {
        return (buffer.Handle, buffer.Size);
    }
}

/// <summary>
/// Where a pass's names are looked up while it is recorded: the view's resources, then the render target's, then the
/// frame's, then the engine's. Reads only.
/// </summary>
internal readonly struct ResourceScope(RenderContext ctx, FrameTargets? targets, ResourceSet? view)
{
    private const string PreviousSuffix = "Previous";

    /// <summary>
    /// The texture behind <paramref name="name"/> with its format; a ping-ponged target's current texture.
    /// </summary>
    public bool TryTexture(string name, out GpuTexture texture, out GpuFormat format, out bool isDepth)
    {
        if (targets?.Get(name) is { Texture.IsValid: true } target)
        {
            texture = target.Texture;
            format = target.Format;
            isDepth = target.IsDepth;
            return true;
        }

        if (ResourceRegistry.IsEngineVolume(name))
        {
            texture = ctx.BrickAtlas;
            format = GpuFormat.R8Unorm;
            isDepth = false;
            return true;
        }

        (Resource? resource, _) = Find(name);
        if (resource is null || resource.Create.Kind == ResourceKind.Buffer)
        {
            texture = default;
            format = GpuFormat.Invalid;
            isDepth = false;
            return false;
        }

        texture = resource.Texture;
        format = resource.Format;
        isDepth = resource.Create.Format == PassFormat.Depth;
        return texture.IsValid;
    }

    /// <summary>
    /// The size of the texture or volume behind <paramref name="name"/>, for a dispatch over it, with how many levels a
    /// volume stacks along its depth.
    /// </summary>
    public bool TrySize(string name, out uint width, out uint height, out uint depth, out uint levels)
    {
        levels = 1;
        if (targets?.Get(name) is { Texture.IsValid: true } target)
        {
            (width, height, depth) = (target.Width, target.Height, 1);
            return true;
        }

        if (ResourceRegistry.IsEngineVolume(name))
        {
            (width, height, depth) = ((uint)(GiBricks.BricksAcross * GiBricks.BrickSize), (uint)(GiBricks.BricksAcross * GiBricks.BrickSize), (uint)(GiBricks.BricksDeep * GiBricks.BrickSize));
            return true;
        }

        (Resource? resource, _) = Find(name);
        if (resource is null || resource.Create.Kind == ResourceKind.Buffer || !resource.Texture.IsValid)
        {
            (width, height, depth) = (0, 0, 0);
            return false;
        }

        (width, height, depth) = (resource.Width, resource.Height, resource.Depth);
        levels = Math.Max(1, resource.Create.Levels);
        return true;
    }

    /// <summary>
    /// The buffer behind <paramref name="name"/>, with the bytes of one slot of it (0 for an engine buffer).
    /// </summary>
    public bool TryBuffer(string name, out GpuBuffer buffer, out uint sliceBytes)
    {
        (Resource? resource, bool previous) = Find(name);
        if (resource is not null && resource.Create.Kind == ResourceKind.Buffer)
        {
            GrowableBuffer? growable = previous ? resource.PreviousBuffer : resource.Buffer;
            buffer = growable?.Handle ?? default;
            sliceBytes = resource.SliceBytes;
            return buffer.IsValid;
        }

        sliceBytes = 0;
        return ResourceRegistry.TryEngineBuffer(ctx, name, out buffer, out _);
    }

    /// <summary>
    /// The texture of the render target the pass writes into when it also reads it: the twin, swapped in afterwards.
    /// </summary>
    public FrameTargets.Target? Target(string name)
    {
        return targets?.Get(name);
    }

    private (Resource? Resource, bool Previous) Find(string name)
    {
        bool previous = false;
        Resource? found = Lookup(name);
        if (found is null && name.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase))
        {
            found = Lookup(name[..^PreviousSuffix.Length]);
            previous = found is { Create.History: true };
            if (!previous)
                found = null;
        }

        return (found, previous);
    }

    private Resource? Lookup(string name)
    {
        if (view is not null && view.TryGetValue(name, out Resource? inView))
            return inView;
        if (targets is not null && targets.Resources.TryGetValue(name, out Resource? inTarget))
            return inTarget;

        return ctx.Resources.Frame.GetValueOrDefault(name);
    }
}
