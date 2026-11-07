using System.Diagnostics.Metrics;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Platform;
using Magic.Projects;
using Magic.Shaders;
using Magic.Threading;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The GPU through Vulkan 1.3: one bindless heap, resources made from Core's descriptions, and one frame of commands
/// submitted at a time. Core hands it each frame the gems recorded, and <see cref="Present"/> runs it, shows it and
/// begins the next, as the first begins as it is made: waits for the frame two before, takes the window's image, and
/// leaves the staging ring empty for what records next.
/// It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class VulkanGraphicsGem : IGem, IGraphicsApi, IDisposable
{
    private readonly Threads _threads;
    private readonly VulkanDevice _device;
    private readonly VulkanHeap _heap;
    private readonly VulkanResources _resources;
    private readonly VulkanFrames _frames;
    private readonly VulkanShaders _shaders;
    private readonly VulkanRecorder _recorder;
    private readonly VulkanSwapchain? _swapchain;
    private readonly Meter _meter;
    private readonly Gauge<long> _memoryUsage;
    private readonly Gauge<long> _memoryLimit;

    /// <summary>
    /// The transient textures not lent, by their description: a pool, so a frame makes none it made before.
    /// </summary>
    private readonly Dictionary<TextureDescription, Stack<GpuTexture>> _spareTransients = [];

    /// <summary>
    /// The transient textures lent this frame, which go back to the pool as it ends.
    /// </summary>
    private readonly List<(TextureDescription Description, GpuTexture Texture)> _lentTransients = [];

    /// <summary>
    /// The textures, buffers and top-level acceleration structures the graph names, by their name, with the
    /// description each was made to.
    /// </summary>
    private readonly Dictionary<string, (TextureDescription Description, GpuTexture Made)> _namedTextures = [];
    private readonly Dictionary<string, (BufferDescription Description, GpuBuffer Made)> _namedBuffers = [];
    private readonly Dictionary<string, (TopLevelDescription Description, GpuAcceleration Made)> _namedAccelerations =
    [];

    public VulkanGraphicsGem(App app, Threads threads, IShaderCompiler compiler, IWindow? window = null)
    {
        _threads = threads;
        IWindow? drawn = app.IsHeadless ? null : window;
        _device = new VulkanDevice(drawn, wantsValidation: IsDebugBuild);

        // The gem's meter goes with it, so nothing holds the gem's code once it unloads.
        _meter = new Meter(new MeterOptions("Magic.Gems.VulkanGraphics") { Tags = _device.Description });
        _memoryUsage = _meter.CreateGauge<long>(
            "magic.gems.vulkan_graphics.gpu.memory.usage",
            "By",
            "Device memory the resources hold."
        );
        _memoryLimit = _meter.CreateGauge<long>(
            "magic.gems.vulkan_graphics.gpu.memory.limit",
            "By",
            "Device memory the GPU has for the resources."
        );
        _heap = new VulkanHeap(_device.Api, _device.Features.HasRayQuery);
        _resources = new VulkanResources(_device, _heap);
        _frames = new VulkanFrames(_device, _resources, _meter);
        _shaders = new VulkanShaders(compiler, _resources, threads);
        _recorder = new VulkanRecorder(_device, _resources, _heap, _shaders);
        if (drawn is not null && _device.Surface.IsNotNull)
            _swapchain = new VulkanSwapchain(_device, _resources, drawn);

        BeginFrame();
    }

    public GpuFeatures Features => _device.Features;

    public GpuLimits Limits => _device.Limits;

    public GpuTexture WindowTarget { get; private set; }

    /// <summary>
    /// Whether this is a Debug build, which turns the validation layer on when the Vulkan SDK provides it.
    /// </summary>
    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public void Dispose()
    {
        _shaders.Dispose();
        _swapchain?.Dispose();
        _frames.Dispose();
        _resources.Dispose();
        _heap.Dispose();
        _device.Dispose();
        _meter.Dispose();
    }

    public TextureDescription GetDescription(GpuTexture texture) =>
        _resources.Textures[texture.Id]?.Description
        ?? throw new ArgumentException($"Texture {texture.Id} is gone.", nameof(texture));

    public BufferDescription GetDescription(GpuBuffer buffer) =>
        _resources.Buffers[buffer.Id]?.Description
        ?? throw new ArgumentException($"Buffer {buffer.Id} is gone.", nameof(buffer));

    public Result<GpuBuffer> CreateBuffer(in BufferDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreateBuffer(description);
    }

    public Result<GpuTexture> CreateTexture(in TextureDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreateTexture(description);
    }

    public Result<GpuTexture> GetTexture(in TextureDescription description) =>
        GetNamed(
            _namedTextures,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateTexture(made),
            static (gem, texture) => gem.Destroy(texture)
        );

    public Result<GpuBuffer> GetBuffer(in BufferDescription description) =>
        GetNamed(
            _namedBuffers,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateBuffer(made),
            static (gem, buffer) => gem.Destroy(buffer)
        );

    public Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description) =>
        GetNamed(
            _namedAccelerations,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateAcceleration(made),
            static (gem, acceleration) => gem.Destroy(acceleration)
        );

    public GpuState GetState(in ShaderEntry shader)
    {
        ThrowIfNotOnRender();

        return _shaders.GetState(shader);
    }

    public Result<GpuTexture> CreateTransient(in TextureDescription description)
    {
        ThrowIfNotOnRender();
        if (
            _spareTransients.TryGetValue(description, out Stack<GpuTexture>? spare)
            && spare.TryPop(out GpuTexture pooled)
        )
        {
            _lentTransients.Add((description, pooled));

            return Result.Success(pooled);
        }

        Result<GpuTexture> made = _resources.CreateTexture(description);
        if (made.Ok)
            _lentTransients.Add((description, made.Value));

        return made;
    }

    public Result<GpuSampler> CreateSampler(in SamplerDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreateSampler(description);
    }

    public Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreatePipeline(description);
    }

    public Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreatePipeline(description);
    }

    public void Destroy(GpuBuffer buffer)
    {
        ThrowIfNotOnRender();
        if (_resources.Buffers.Remove(buffer.Id) is { } removed)
            _frames.Current.Trash.Add(new(buffer.Id, removed));
    }

    public void Destroy(GpuTexture texture)
    {
        ThrowIfNotOnRender();
        if (_resources.Textures.Remove(texture.Id) is { } removed)
            _frames.Current.Trash.Add(new(texture.Id, removed));
    }

    public void Destroy(GpuSampler sampler)
    {
        ThrowIfNotOnRender();
        if (_resources.Samplers.Remove(sampler.Id) is { } removed)
            _frames.Current.Trash.Add(new(sampler.Id, removed));
    }

    public void Destroy(GpuPipeline pipeline)
    {
        ThrowIfNotOnRender();
        if (_resources.Pipelines.Remove(pipeline.Id) is { } removed)
            _frames.Current.Trash.Add(new(pipeline.Id, removed));
    }

    public Result<GpuAcceleration> CreateAcceleration(in BottomLevelDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreateAcceleration(description);
    }

    public Result<GpuAcceleration> CreateAcceleration(in TopLevelDescription description)
    {
        ThrowIfNotOnRender();

        return _resources.CreateAcceleration(description);
    }

    public ulong GetAddress(GpuAcceleration acceleration) => _resources.Accelerations[acceleration.Id]?.Address ?? 0;

    public void Destroy(GpuAcceleration acceleration)
    {
        ThrowIfNotOnRender();
        if (_resources.Accelerations.Remove(acceleration.Id) is { } removed)
            _frames.Current.Trash.Add(new(acceleration.Id, removed));
    }

    public GpuUpload BeginUpload(int bytes)
    {
        ThrowIfNotOnRender();

        return _frames.Upload(bytes, _device.Limits.UploadAlignment);
    }

    public async Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        cancel.ThrowIfCancellationRequested();
        if (_resources.Buffers[buffer.Id] is not { } read)
            return Result.Failure($"buffer {buffer.Id} is gone.");

        return await _frames.ReadBackAsync(buffer.Id, read, offset, bytes).ConfigureAwait(false);
    }

    public void Present(in GpuCommands commands)
    {
        ThrowIfNotOnRender();
        VulkanFrames.Frame frame = _frames.Current;
        _recorder.Record(frame, commands);
        // The window's image is shown whatever the passes left it as.
        if (_resources.Textures[WindowTarget.Id] is { } window)
            _recorder.TransitionTo(frame.CommandBuffer, window, GpuAccess.Present);

        _device.Api.vkEndCommandBuffer(frame.CommandBuffer);
        unsafe
        {
            VkCommandBufferSubmitInfo commandInfo = new() { commandBuffer = frame.CommandBuffer };
            VkSemaphoreSubmitInfo wait = new()
            {
                semaphore = frame.Acquired,
                stageMask = VkPipelineStageFlags2.AllCommands,
            };
            VkSemaphoreSubmitInfo signal = new()
            {
                semaphore = _swapchain?.Rendered ?? VkSemaphore.Null,
                stageMask = VkPipelineStageFlags2.AllCommands,
            };
            bool hasWindow = !WindowTarget.IsNone;
            VkSubmitInfo2 submit = new()
            {
                waitSemaphoreInfoCount = hasWindow ? 1u : 0u,
                pWaitSemaphoreInfos = &wait,
                commandBufferInfoCount = 1,
                pCommandBufferInfos = &commandInfo,
                signalSemaphoreInfoCount = hasWindow ? 1u : 0u,
                pSignalSemaphoreInfos = &signal,
            };
            VkResult result = _device.Api.vkQueueSubmit2(_device.Queue, 1, &submit, frame.Fence);
            if (result != VkResult.Success)
                Log.Error($"The GPU refused the frame: {result}.");
        }

        if (!WindowTarget.IsNone)
            _swapchain?.Present();

        ReturnTransients();

        // The next frame: what the GPU is done with finished, so a readback awaited completes whether or not anything
        // draws, then its commands begun and the window's next image taken.
        _frames.Poll();
        _memoryUsage.Record(_resources.MemoryBytes);
        _memoryLimit.Record(_device.Limits.DeviceMemoryBytes);
        BeginFrame();
    }

    private void BeginFrame()
    {
        _frames.Begin();
        WindowTarget = _swapchain?.Acquire(_frames.Current.Acquired) ?? GpuTexture.None;
    }

    /// <summary>
    /// What <paramref name="description"/> makes, under <paramref name="name"/> in <paramref name="named"/>: made on the
    /// first ask, the same one while the name is asked for with the same description, and made again, the old one
    /// destroyed, when the description changes.
    /// </summary>
    private Result<T> GetNamed<TDescription, T>(
        Dictionary<string, (TDescription Description, T Made)> named,
        TDescription description,
        string name,
        Func<VulkanGraphicsGem, TDescription, Result<T>> create,
        Action<VulkanGraphicsGem, T> destroy
    )
        where TDescription : struct, IEquatable<TDescription>
    {
        ThrowIfNotOnRender();
        if (named.TryGetValue(name, out (TDescription Description, T Made) old))
        {
            if (old.Description.Equals(description))
                return Result.Success(old.Made);

            destroy(this, old.Made);
            named.Remove(name);
        }

        Result<T> created = create(this, description);
        if (!created.Ok)
            return Result.Failure($"the {name} was not made: {created.Error}");

        named[name] = (description, created.Value);

        return created;
    }

    private void ReturnTransients()
    {
        foreach ((TextureDescription description, GpuTexture texture) in _lentTransients)
        {
            if (!_spareTransients.TryGetValue(description, out Stack<GpuTexture>? spare))
            {
                spare = new Stack<GpuTexture>();
                _spareTransients[description] = spare;
            }

            spare.Push(texture);
        }

        _lentTransients.Clear();
    }

    private void ThrowIfNotOnRender()
    {
        if (!_threads.IsCurrent(EngineThread.Render))
            throw new InvalidOperationException("The GPU's resources and frames are the render thread's.");
    }
}
