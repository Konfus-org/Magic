using Magic.Debugging;
using Magic.Files;
using Magic.Files.Types;
using Magic.Gpu;
using Magic.Rendering;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// Where a texture asset is in the loading: not yet, on the GPU, or never.
/// </summary>
internal enum TextureState : byte
{
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// The GPU resources the gem draws with, each made once on its first ask and freed with the gem: a texture asset by its
/// handle, read and made with every level and the sampler its asset asks for, then copied up in the frame after it
/// arrives; a sampler by its description, shared by every ask for the same one.
/// </summary>
internal sealed class GpuResources(IGraphicsApi gpu, Assets assets, Threads threads) : IDisposable
{
    private readonly Dictionary<ulong, TextureEntry> _textures = [];
    private readonly Dictionary<SamplerDescription, GpuSampler> _samplers = [];
    private readonly List<(Texture Asset, GpuTexture Texture)> _arrived = [];

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
    public TextureState Get(Handle<Texture> handle, out uint texture, out uint sampler)
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

    private async Task LoadAsync(Handle<Texture> handle, TextureEntry entry)
    {
        Result<Texture> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        string name = assets.GetPath(handle) ?? $"texture {handle.Id}";
        if (!loaded.Ok)
        {
            Log.Error($"{name} is drawn as a failure: {loaded.Error}");
            entry.State = TextureState.Failed;

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
            entry.State = TextureState.Failed;

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
            entry.State = TextureState.Failed;

            return;
        }

        entry.Texture = texture.Value;
        entry.Sampler = sampler.Value;
        entry.State = TextureState.Ready;
        _arrived.Add((asset, texture.Value));
    }

    private sealed class TextureEntry
    {
        public TextureState State { get; set; }

        public GpuTexture Texture { get; set; }

        public GpuSampler Sampler { get; set; }
    }
}
