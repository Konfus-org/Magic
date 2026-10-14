using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// What a name a pass reads or writes stands for: a texture or a buffer, the renderer's or one a pass makes, for the
/// frame or for each view, and how a shader declares it. The renderer's own are <see cref="Engine"/>; a pass's come
/// from its <see cref="PassCreate"/>.
/// </summary>
[SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "A format or shape maps to the HLSL it is declared as; the rest share a default.")]
internal sealed record ResourceInfo(string Name, bool IsBuffer, bool IsFrame, GpuFormat Format, GpuTextureKind Shape, string BufferType)
{
    /// <summary>
    /// What the renderer has, by name: the scene's tables, the templates culling and binning start from, and the shadows'
    /// pool and tables, once a frame; each view's visibility buffer, G-buffer, scene colour, velocity and exposure.
    /// </summary>
    public static IReadOnlyDictionary<string, ResourceInfo> Engine { get; } = new Dictionary<string, ResourceInfo>(StringComparer.Ordinal)
    {
        ["Instances"] = FrameBuffer("Instances", "GpuSceneInstance"),
        ["Models"] = FrameBuffer("Models", "GpuModel"),
        ["Levels"] = FrameBuffer("Levels", "GpuLevel"),
        ["Clusters"] = FrameBuffer("Clusters", "GpuCluster"),
        ["Cards"] = FrameBuffer("Cards", "GpuCard"),
        ["Geometry"] = FrameBuffer("Geometry", "uint"),
        ["Materials"] = FrameBuffer("Materials", "GpuMaterial"),
        ["MaterialBins"] = FrameBuffer("MaterialBins", "uint"),
        ["SceneLights"] = FrameBuffer("SceneLights", "GpuSceneLight"),
        ["Moved"] = FrameBuffer("Moved", "float4"),
        ["ZBinsEmpty"] = FrameBuffer("ZBinsEmpty", "uint"),
        ["Zeros"] = FrameBuffer("Zeros", "uint"),
        ["Depth"] = ViewTexture("Depth", GpuFormat.D32Float),
        ["Visibility"] = ViewTexture("Visibility", GpuFormat.R32Uint),
        ["Albedo"] = ViewTexture("Albedo", GpuFormat.Rgba8Srgb),
        ["Normal"] = ViewTexture("Normal", GpuFormat.Rgb10A2Unorm),
        ["Material"] = ViewTexture("Material", GpuFormat.Rgba8Unorm),
        ["Hdr"] = ViewTexture("Hdr", GpuFormat.Rgba16Float),
        ["Velocity"] = ViewTexture("Velocity", GpuFormat.Rg16Float),
        ["Exposure"] = new("Exposure", true, false, GpuFormat.Invalid, GpuTextureKind.Texture2D, "float4"),
        ["MeshFieldAtlas"] = new("MeshFieldAtlas", false, true, GpuFormat.R16Float, GpuTextureKind.Texture3D, string.Empty),
        ["MeshFields"] = FrameBuffer("MeshFields", "GpuMeshField"),
        ["MaterialGi"] = FrameBuffer("MaterialGi", "GpuMaterialGi"),
        ["GiLevels"] = FrameBuffer("GiLevels", "GpuGiLevel"),
        ["ShadowPool"] = new("ShadowPool", false, true, GpuFormat.D32Float, GpuTextureKind.Texture2D, string.Empty),
        ["ShadowTables"] = FrameBuffer("ShadowTables", "ShadowEntry"),
        ["SunClipmap"] = FrameBuffer("SunClipmap", "GpuSunClipmap"),
        ["ChangedLights"] = FrameBuffer("ChangedLights", "uint"),
    };

    public bool IsDepth => Format == GpuFormat.D32Float;

    /// <summary>
    /// A texture whose texels cannot be filtered, an integer's, is read as a storage texture, texel by texel with no
    /// sampler, as no GPU samples it.
    /// </summary>
    public bool IsStorageRead => !IsBuffer && Format == GpuFormat.R32Uint;

    /// <summary>
    /// The HLSL type a shader reads it as: <c>Texture2D&lt;float4&gt;</c>, or for a buffer <c>ByteAddressBuffer</c>,
    /// whose elements of <see cref="BufferType"/> it reads with LOAD (Include/Bindings.hlsli).
    /// </summary>
    public string ReadType => IsBuffer ? "ByteAddressBuffer" : $"{TextureName}<{Element}>";

    /// <summary>
    /// The HLSL type a compute shader writes it as, with the format Vulkan needs to write a storage texture.
    /// </summary>
    public string WriteType => IsBuffer ? "RWByteAddressBuffer" : $"[[vk::image_format(\"{StorageFormat}\")]] RW{(Shape == GpuTextureKind.Cube ? "Texture2DArray" : TextureName)}<{Element}>";

    private string TextureName => Shape switch
    {
        GpuTextureKind.Texture2DArray => "Texture2DArray",
        GpuTextureKind.Texture3D => "Texture3D",
        GpuTextureKind.Cube => "TextureCube",
        _ => "Texture2D",
    };

    private string Element => Format switch
    {
        GpuFormat.D32Float or GpuFormat.R32Float or GpuFormat.R16Float or GpuFormat.R8Unorm => "float",
        GpuFormat.Rg16Float or GpuFormat.Rg32Float or GpuFormat.Rg8Unorm => "float2",
        GpuFormat.R32Uint => "uint",
        _ => "float4",
    };

    private string StorageFormat => Format switch
    {
        GpuFormat.Rgba16Float => "rgba16f",
        GpuFormat.Rgba32Float => "rgba32f",
        GpuFormat.Rgba8Unorm => "rgba8",
        GpuFormat.Rgb10A2Unorm => "rgb10_a2",
        GpuFormat.R11G11B10Float => "r11g11b10f",
        GpuFormat.Rg16Float => "rg16f",
        GpuFormat.Rg32Float => "rg32f",
        GpuFormat.R16Float => "r16f",
        GpuFormat.R32Float => "r32f",
        GpuFormat.R32Uint => "r32ui",
        GpuFormat.R8Unorm => "r8",
        GpuFormat.Rg8Unorm => "rg8",
        _ => "unknown",
    };

    /// <summary>
    /// The name a texture kept from frame to frame (<see cref="PassCreate.History"/>) is read by as it was the frame
    /// before.
    /// </summary>
    public static string HistoryOf(string name) => name + "History";

    /// <summary>
    /// What a pass makes, as it declares it: for the frame when it is made in a stage that runs once a frame.
    /// </summary>
    public static ResourceInfo Of(PassCreate create, bool isFrame) => new(
        create.Name,
        create.Kind == PassResource.Buffer,
        isFrame,
        create.Format,
        create.Cube ? GpuTextureKind.Cube : create.Depth > 0 ? GpuTextureKind.Texture3D : create.Layers > 1 ? GpuTextureKind.Texture2DArray : GpuTextureKind.Texture2D,
        create.Type);

    /// <summary>
    /// An expression that reads it once, at <c>uv</c>, as a float, through its sampler: what keeps a read the shader may
    /// not use, or reads only texel by texel, bound with its sampler where the renderer binds them
    /// (<see cref="ListedPass.Header"/>). A storage texture, which has no sampler, is read at its first texel.
    /// </summary>
    public string KeepAlive(PassFilter filter)
    {
        string coords = Shape == GpuTextureKind.Texture2D ? "uv" : Shape == GpuTextureKind.Cube ? "float3(uv, 1.0)" : "float3(uv, 0.0)";
        return IsBuffer ? $"asfloat({Name}.Load(0u))"
            : IsStorageRead ? $"asfloat({Name}.Load({(Shape == GpuTextureKind.Texture2D ? "int3(0, 0, 0)" : "int4(0, 0, 0, 0)")}).x)"
            : filter == PassFilter.Comparison ? $"{Name}.SampleCmpLevelZero({Name}Sampler, {coords}, 0.0)"
            : $"{Name}.SampleLevel({Name}Sampler, {coords}, 0.0).x";
    }

    private static ResourceInfo FrameBuffer(string name, string type) => new(name, true, true, GpuFormat.Invalid, GpuTextureKind.Texture2D, type);

    private static ResourceInfo ViewTexture(string name, GpuFormat format) => new(name, false, false, format, GpuTextureKind.Texture2D, string.Empty);
}

/// <summary>
/// A name as it stands this frame: its texture or buffer, and its size.
/// </summary>
internal readonly record struct BoundResource(GpuTexture Texture, GpuBuffer Buffer, uint Width, uint Height, uint Bytes)
{
    public static BoundResource Of(GpuTexture texture, uint width, uint height) => new(texture, GpuBuffer.None, width, height, 0);

    public static BoundResource Of(GpuBuffer buffer, uint bytes) => new(GpuTexture.None, buffer, 0, 0, bytes);
}

/// <summary>
/// Everything that sizes and shapes what a pass makes: two made from the same key are alike, so a key that changes, a
/// view resized or a count grown, makes it again. <see cref="HasHistory"/> makes it twice, the frame's and the last
/// frame's (<see cref="ResourceSet.Swap"/>).
/// </summary>
internal readonly record struct ResourceKey(
    bool IsBuffer,
    GpuFormat Format,
    GpuTextureKind Shape,
    GpuTextureUsage TextureUsage,
    GpuBufferUsage BufferUsage,
    uint Width,
    uint Height,
    uint Layers,
    uint Levels,
    uint Bytes,
    bool HasHistory = false);

/// <summary>
/// The textures and buffers names stand for in one scope, the frame or a view: the renderer's, set each frame as they
/// may have been made again, and the passes', made here as their keys say and made again when a key changes. Render
/// thread.
/// </summary>
internal sealed class ResourceSet(IGpu gpu) : IDisposable
{
    private readonly Dictionary<string, BoundResource> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (BoundResource Resource, ResourceKey Key)> _made = new(StringComparer.Ordinal);

    /// <summary>
    /// The listing last trimmed for, so a frame with the same one does nothing.
    /// </summary>
    private Listing? _kept;

    public void Dispose()
    {
        foreach (string name in new List<string>(_made.Keys))
        {
            Release(name);
        }

        _bound.Clear();
    }

    public bool TryGet(string name, out BoundResource resource) => _bound.TryGetValue(name, out resource);

    /// <summary>
    /// What a name of the renderer's stands for this frame.
    /// </summary>
    public void Set(string name, BoundResource resource)
    {
        _bound[name] = resource;
    }

    /// <summary>
    /// What a pass makes, made now unless it was made with the same key; returns whether it was made now, so whoever
    /// fills it knows. One kept from frame to frame is made with its history, <see cref="ResourceInfo.HistoryOf"/>.
    /// </summary>
    public bool Make(string name, in ResourceKey key)
    {
        if (_made.TryGetValue(name, out (BoundResource Resource, ResourceKey Key) made) && made.Key == key)
        {
            return false;
        }

        Release(name);
        Keep(name, key);
        if (key.HasHistory)
        {
            Keep(ResourceInfo.HistoryOf(name), key with { HasHistory = false });
        }

        return true;
    }

    /// <summary>
    /// Swaps each texture kept from frame to frame with its history, so what was written this frame is read as it the
    /// next, and the next frame writes over the older one.
    /// </summary>
    public void Swap()
    {
        foreach (string name in new List<string>(_made.Keys))
        {
            if (!_made[name].Key.HasHistory || !_made.TryGetValue(ResourceInfo.HistoryOf(name), out (BoundResource Resource, ResourceKey Key) history))
            {
                continue;
            }

            (BoundResource Resource, ResourceKey Key) current = _made[name];
            _made[name] = (history.Resource, current.Key);
            _made[ResourceInfo.HistoryOf(name)] = (current.Resource, history.Key);
            _bound[name] = history.Resource;
            _bound[ResourceInfo.HistoryOf(name)] = current.Resource;
        }
    }

    /// <summary>
    /// Releases what the passes made that <paramref name="listing"/> does not make; what the renderer made here of its
    /// own names stays.
    /// </summary>
    public void Keep(Listing listing)
    {
        if (listing == _kept)
        {
            return;
        }

        _kept = listing;
        foreach (string name in new List<string>(_made.Keys))
        {
            if (_made.ContainsKey(name) && !listing.Created.ContainsKey(name) && !ResourceInfo.Engine.ContainsKey(name) && !IsHistoryOf(listing, name))
            {
                Release(name);
            }
        }
    }

    private static bool IsHistoryOf(Listing listing, string name)
    {
        const string Suffix = "History";
        return name.EndsWith(Suffix, StringComparison.Ordinal) && listing.Created.TryGetValue(name[..^Suffix.Length], out CreatedResource? created) && created.Create.History;
    }

    private void Keep(string name, in ResourceKey key)
    {
        BoundResource resource = key.IsBuffer
            ? BoundResource.Of(gpu.CreateBuffer(key.BufferUsage, key.Bytes), key.Bytes)
            : BoundResource.Of(gpu.CreateTexture(new GpuTextureDesc(key.Format, key.TextureUsage, key.Width, key.Height, key.Levels, key.Layers, key.Shape)), key.Width, key.Height);
        _made[name] = (resource, key);
        _bound[name] = resource;
    }

    /// <summary>
    /// Releases what <paramref name="name"/> was made as, and its history with it.
    /// </summary>
    private void Release(string name)
    {
        if (!_made.Remove(name, out (BoundResource Resource, ResourceKey Key) made))
        {
            return;
        }

        _bound.Remove(name);
        gpu.Release(made.Resource.Texture);
        gpu.Release(made.Resource.Buffer);
        if (made.Key.HasHistory)
        {
            Release(ResourceInfo.HistoryOf(name));
        }
    }
}
