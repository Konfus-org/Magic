using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Debugging;
using Magic.Graphics;
using Magic.Platform;
using Magic.Projects;
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
    /// <summary>
    /// How many frames in a row a named resource may go unasked for before it is destroyed.
    /// </summary>
    private const int UnusedFrames = 3;

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
    /// The textures, buffers and acceleration structures the graph names, by their name, and the samplers it
    /// asks for, by their description, each with the description it was made to and the frame that last asked for it.
    /// </summary>
    private readonly Dictionary<string, Named<TextureDescription, GpuTexture>> _namedTextures = [];
    private readonly Dictionary<string, Named<BufferDescription, GpuBuffer>> _namedBuffers = [];
    private readonly Dictionary<string, Named<BottomLevelDescription, GpuAcceleration>> _namedBottomLevels = [];
    private readonly Dictionary<string, Named<TopLevelDescription, GpuAcceleration>> _namedTopLevels = [];
    private readonly Dictionary<SamplerDescription, Named<SamplerDescription, GpuSampler>> _samplers = [];

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

    public Result<GpuTexture> GetTexture(in TextureDescription description) =>
        GetNamed(
            _namedTextures,
            description.Name,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateTexture(made),
            static (gem, texture) => gem.Destroy(texture)
        );

    public Result<GpuBuffer> GetBuffer(in BufferDescription description) =>
        GetNamed(
            _namedBuffers,
            description.Name,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateBuffer(made),
            static (gem, buffer) => gem.Destroy(buffer)
        );

    public Result<GpuAcceleration> GetAcceleration(in BottomLevelDescription description) =>
        GetNamed(
            _namedBottomLevels,
            description.Name,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateAcceleration(made),
            static (gem, acceleration) => gem.Destroy(acceleration)
        );

    public Result<GpuAcceleration> GetAcceleration(in TopLevelDescription description) =>
        GetNamed(
            _namedTopLevels,
            description.Name,
            description,
            description.Name,
            static (gem, made) => gem._resources.CreateAcceleration(made),
            static (gem, acceleration) => gem.Destroy(acceleration)
        );

    public Result<GpuSampler> GetSampler(in SamplerDescription description) =>
        GetNamed(
            _samplers,
            description,
            description,
            "sampler",
            static (gem, made) => gem._resources.CreateSampler(made),
            static (gem, sampler) => gem.Destroy(sampler)
        );

    public GpuState GetState(in ShaderEntry shader)
    {
        ThrowIfNotOnRender();

        return _shaders.GetState(shader);
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

        DestroyUnasked();

        // The next frame: what the GPU is done with finished, so a readback awaited completes whether or not anything
        // draws, then its commands begun and the window's next image taken.
        _frames.Poll();
        _memoryUsage.Record(_resources.MemoryBytes);
        _memoryLimit.Record(_device.Limits.DeviceMemoryBytes);
        BeginFrame();
    }

    private void Destroy(GpuBuffer buffer)
    {
        if (_resources.Buffers.Remove(buffer.Id) is { } removed)
            _frames.Current.Trash.Add(new(buffer.Id, removed));
    }

    private void Destroy(GpuTexture texture)
    {
        if (_resources.Textures.Remove(texture.Id) is { } removed)
            _frames.Current.Trash.Add(new(texture.Id, removed));
    }

    private void Destroy(GpuSampler sampler)
    {
        if (_resources.Samplers.Remove(sampler.Id) is { } removed)
            _frames.Current.Trash.Add(new(sampler.Id, removed));
    }

    private void Destroy(GpuAcceleration acceleration)
    {
        if (_resources.Accelerations.Remove(acceleration.Id) is { } removed)
            _frames.Current.Trash.Add(new(acceleration.Id, removed));
    }

    private void BeginFrame()
    {
        _frames.Begin();
        Compact();
        WindowTarget = _swapchain?.Acquire(_frames.Current.Acquired) ?? GpuTexture.None;
    }

    /// <summary>
    /// Each bottom-level structure a finished frame built, copied into one of the size it compacts to at the start of
    /// this frame's commands. Its name takes the copy, and the built one dies with its scratch once this frame has run.
    /// The gem that asks for it gets the copy's address this frame and builds the instances over it again, before
    /// anything traces the copy.
    /// </summary>
    private void Compact()
    {
        foreach ((VulkanAcceleration built, ulong bytes) in _frames.Compactable)
        {
            // Destroyed, or made again, since it was built.
            ref Named<BottomLevelDescription, GpuAcceleration> named = ref CollectionsMarshal.GetValueRefOrNullRef(
                _namedBottomLevels,
                built.Name
            );
            if (Unsafe.IsNullRef(ref named) || !ReferenceEquals(_resources.Accelerations[named.Made.Id], built))
                continue;

            Result<GpuAcceleration> compacted = _resources.Compact(_frames.Current.CommandBuffer, built, bytes);
            if (!compacted.Ok)
            {
                Log.Warn($"{built.Name} keeps its build's size: {compacted.Error}");
                continue;
            }

            Destroy(named.Made);
            named.Made = compacted.Value;
        }

        _frames.Compactable.Clear();
    }

    /// <summary>
    /// What <paramref name="description"/> makes, under <paramref name="key"/> in <paramref name="named"/>: made on the
    /// first ask, the same one while the key is asked for with the same description, made again, the old one destroyed,
    /// when the description changes, and marked asked for in this frame.
    /// </summary>
    private Result<T> GetNamed<TKey, TDescription, T>(
        Dictionary<TKey, Named<TDescription, T>> named,
        TKey key,
        TDescription description,
        string name,
        Func<VulkanGraphicsGem, TDescription, Result<T>> create,
        Action<VulkanGraphicsGem, T> destroy
    )
        where TKey : notnull
        where TDescription : struct, IEquatable<TDescription>
    {
        ThrowIfNotOnRender();
        ulong frame = _frames.Current.Serial;
        ref Named<TDescription, T> old = ref CollectionsMarshal.GetValueRefOrNullRef(named, key);
        if (!Unsafe.IsNullRef(ref old))
        {
            if (old.Description.Equals(description))
            {
                old.Asked = frame;

                return Result.Success(old.Made);
            }

            destroy(this, old.Made);
            named.Remove(key);
        }

        Result<T> created = create(this, description);
        if (!created.Ok)
            return Result.Failure($"the {name} was not made: {created.Error}");

        named[key] = new Named<TDescription, T>(description, created.Value, frame);

        return created;
    }

    /// <summary>
    /// Destroys every named resource no frame asked for in the last <see cref="UnusedFrames"/>, as this frame ends.
    /// </summary>
    private void DestroyUnasked()
    {
        DestroyUnasked(_namedTextures, static (gem, texture) => gem.Destroy(texture));
        DestroyUnasked(_namedBuffers, static (gem, buffer) => gem.Destroy(buffer));
        DestroyUnasked(_namedBottomLevels, static (gem, acceleration) => gem.Destroy(acceleration));
        DestroyUnasked(_namedTopLevels, static (gem, acceleration) => gem.Destroy(acceleration));
        DestroyUnasked(_samplers, static (gem, sampler) => gem.Destroy(sampler));
    }

    private void DestroyUnasked<TKey, TDescription, T>(
        Dictionary<TKey, Named<TDescription, T>> named,
        Action<VulkanGraphicsGem, T> destroy
    )
        where TKey : notnull
    {
        ulong frame = _frames.Current.Serial;
        foreach ((TKey key, Named<TDescription, T> entry) in named)
        {
            if (frame - entry.Asked < UnusedFrames)
                continue;

            destroy(this, entry.Made);
            named.Remove(key);
        }
    }

    private void ThrowIfNotOnRender()
    {
        if (!_threads.IsCurrent(EngineThread.Render))
            throw new InvalidOperationException("The GPU's resources and frames are the render thread's.");
    }

    /// <summary>
    /// A named resource, the description it was made to, and the frame that last asked for it.
    /// </summary>
    private record struct Named<TDescription, T>(TDescription Description, T Made, ulong Asked);
}
