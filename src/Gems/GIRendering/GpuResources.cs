using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// Where an asset is in the loading: not yet, on the GPU, or never, drawn as the failure.
/// </summary>
internal enum ResourceState : byte
{
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// The GPU resources the gem draws with, each made once on its first ask and freed with the gem: a texture asset by its
/// handle, read and made with every level and the sampler its asset asks for, then copied up in the frame after it
/// arrives; a sampler by its description, shared by every ask for the same one; a material asset by its handle, read
/// once, with the texture it names asked for, and handed out as the material row a slot's params make over its own.
/// </summary>
internal sealed class GpuResources(IGraphicsApi gpu, Assets assets, Threads threads) : IDisposable
{
    private const string FailureShader = "Shaders/Material/Failure.slang";

    private readonly Dictionary<ulong, TextureEntry> _textures = [];

    /// <summary>
    /// Each material asked for, by its id, null while it loads; one that did not load is the failure material.
    /// </summary>
    private readonly Dictionary<ulong, Material?> _materials = [];
    private readonly Dictionary<SamplerDescription, GpuSampler> _samplers = [];
    private readonly List<(Texture Asset, GpuTexture Texture)> _arrived = [];

    /// <summary>
    /// The material row a slot is drawn with until its material is loaded, and an empty slot always.
    /// </summary>
    public static GpuMaterial DefaultMaterial =>
        new() { BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1), Roughness = 0.6f };

    private static GpuMaterial FailureMaterial =>
        new()
        {
            BaseColor = new Vector4(1, 0, 1, 1),
            Roughness = 1,
            Kind = (uint)MaterialKind.Failure,
        };

    public void Dispose()
    {
        foreach (TextureEntry entry in _textures.Values)
        {
            gpu.Destroy(entry.Texture);
        }

        foreach (GpuSampler sampler in _samplers.Values)
        {
            gpu.Destroy(sampler);
        }
    }

    /// <summary>
    /// Whether the texture is on the GPU, and its heap ids when it is; the first ask starts reading it.
    /// </summary>
    public ResourceState Get(Handle<Texture> handle, out uint texture, out uint sampler)
    {
        if (!_textures.TryGetValue(handle.Id, out TextureEntry? entry))
        {
            entry = new TextureEntry();
            _textures[handle.Id] = entry;
            LoadAsync(handle, entry).FireAndForget();
        }

        texture = entry.Texture.Id;
        sampler = entry.Sampler.Id;

        return entry.State;
    }

    /// <summary>
    /// Whether the material and the texture it names are on the GPU, or it is drawn as the failure; the first ask starts
    /// loading it.
    /// </summary>
    public ResourceState Get(Handle<Material> handle) => Get(new MaterialInstance { Handle = handle }, out _);

    /// <summary>
    /// Whether the slot's material and the texture it names are on the GPU, and the row the slot's params over the
    /// material's make: "color", "roughness", "metalness", "emissive" and the material's "colorMap" texture, each the
    /// default when left out. The default row while the material loads and without the texture while that loads; of the
    /// failure kind when the material did not load or names the failure shader, and the bare failure row when its
    /// texture failed. The first ask starts loading the material.
    /// </summary>
    public ResourceState Get(in MaterialInstance slot, out GpuMaterial row)
    {
        row = DefaultMaterial;
        if (!_materials.TryGetValue(slot.Handle.Id, out Material? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return ResourceState.Loading;
        }

        if (asked is not { } material)
            return ResourceState.Loading;

        MaterialOverrides overrides = slot.Overrides;
        bool isFailure = material.Shader == assets.Find<Shader>(FailureShader);
        row = isFailure ? FailureMaterial : DefaultMaterial;
        if (TryGetParam("color", out MaterialParam? color))
            row.BaseColor = new Vector4(color.R, color.G, color.B, color.A);

        if (TryGetParam("roughness", out MaterialParam? roughness))
            row.Roughness = roughness.X;

        if (TryGetParam("metalness", out MaterialParam? metalness))
            row.Metalness = metalness.X;

        if (TryGetParam("emissive", out MaterialParam? emissive))
            row.Emissive = new Vector3(emissive.R, emissive.G, emissive.B);

        ResourceState colorMap = GetColorMap(material.Params, out uint texture, out uint sampler);
        switch (colorMap)
        {
            case ResourceState.Ready:
                row.BaseColorTexture = texture;
                row.BaseColorSampler = sampler;

                return isFailure ? ResourceState.Failed : ResourceState.Ready;
            case ResourceState.Failed:
                row = FailureMaterial;

                return ResourceState.Failed;
            case ResourceState.Loading:
                return ResourceState.Loading;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), colorMap, "not a resource state");
        }

        bool TryGetParam(string name, [MaybeNullWhen(false)] out MaterialParam param) =>
            overrides.TryGet(name, out param) || material.Params.TryGetValue(name, out param);
    }

    /// <summary>
    /// The sampler <paramref name="description"/> makes, made on the first ask for it.
    /// </summary>
    public Result<GpuSampler> TryGetSampler(in SamplerDescription description)
    {
        if (_samplers.TryGetValue(description, out GpuSampler sampler))
            return Result.Success(sampler);

        Result<GpuSampler> made = gpu.CreateSampler(description);
        if (made.Ok)
            _samplers[description] = made.Value;

        return made;
    }

    /// <summary>
    /// Copies the levels of the textures that arrived since the last frame, in the frame's upload pass.
    /// </summary>
    public void Upload(RenderGraph graph)
    {
        foreach ((Texture asset, GpuTexture texture) in _arrived)
        {
            graph.Use(texture, GpuAccess.CopyDestination);
            for (int level = 0; level < asset.Levels.Length; level++)
            {
                TextureLevel source = asset.Levels[level];
                GpuUpload upload = gpu.BeginUpload(source.Size);
                asset.Pixels.Span.Slice(source.Offset, source.Size).CopyTo(upload.Bytes);
                graph.CopyBufferToTexture(upload.Buffer, upload.Offset, texture, level);
            }
        }

        _arrived.Clear();
    }

    private static GpuFormat ToGpu(TextureFormat format) =>
        format switch
        {
            TextureFormat.Rgba8Unorm => GpuFormat.R8G8B8A8Unorm,
            TextureFormat.Rgba8Srgb => GpuFormat.R8G8B8A8Srgb,
            TextureFormat.Bc7Unorm => GpuFormat.Bc7Unorm,
            TextureFormat.Bc7Srgb => GpuFormat.Bc7Srgb,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not a texture format"),
        };

    /// <summary>
    /// The state of the "colorMap" texture <paramref name="parameters"/> name, asking for it, with its heap ids once it
    /// is ready; ready, as nothing, when they name none.
    /// </summary>
    private ResourceState GetColorMap(MaterialParamDictionary parameters, out uint texture, out uint sampler)
    {
        if (parameters.TryGetValue("colorMap", out MaterialParam? colorMap) && colorMap.Texture != Handle<Texture>.None)
            return Get(colorMap.Texture, out texture, out sampler);

        texture = 0;
        sampler = 0;

        return ResourceState.Ready;
    }

    private async Task LoadAsync(Handle<Material> handle)
    {
        Result<Material> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        if (!loaded.Ok)
        {
            Log.Error($"{assets.GetPath(handle)} is drawn as a failure: {loaded.Error}");
            _materials[handle.Id] = new Material { Shader = assets.Find<Shader>(FailureShader) };

            return;
        }

        _materials[handle.Id] = loaded.Value;
        // Asks for its texture now, so the loading gate waits for it before any row needs it.
        GetColorMap(loaded.Value.Params, out _, out _);
    }

    private async Task LoadAsync(Handle<Texture> handle, TextureEntry entry)
    {
        Result<Texture> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        string name = assets.GetPath(handle) ?? $"texture {handle.Id}";
        if (!loaded.Ok)
        {
            Log.Error($"{name} is drawn as a failure: {loaded.Error}");
            entry.State = ResourceState.Failed;

            return;
        }

        Texture asset = loaded.Value;
        Result<GpuTexture> texture = gpu.CreateTexture(
            new TextureDescription
            {
                Width = asset.Width,
                Height = asset.Height,
                Format = ToGpu(asset.Format),
                Usage = TextureUsages.Sampled | TextureUsages.CopyDestination,
                Levels = asset.Levels.Length,
                Name = name,
            }
        );
        if (!texture.Ok)
        {
            Log.Error($"{name} is drawn as a failure: {texture.Error}");
            entry.State = ResourceState.Failed;

            return;
        }

        Result<GpuSampler> sampler = TryGetSampler(
            new SamplerDescription
            {
                Filter = asset.Filter,
                Wrap = asset.Wrap,
                Anisotropy = asset.Filter == TextureFilter.Linear ? 8 : 1,
            }
        );
        if (!sampler.Ok)
        {
            Log.Error($"{name} is drawn as a failure: {sampler.Error}");
            gpu.Destroy(texture.Value);
            entry.State = ResourceState.Failed;

            return;
        }

        entry.Texture = texture.Value;
        entry.Sampler = sampler.Value;
        entry.State = ResourceState.Ready;
        _arrived.Add((asset, texture.Value));
    }

    private sealed class TextureEntry
    {
        public ResourceState State { get; set; }

        public GpuTexture Texture { get; set; }

        public GpuSampler Sampler { get; set; }
    }
}
