using System.Diagnostics.Metrics;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Platform;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Vortice.Vulkan;

namespace Magic.VulkanGraphics;

/// <summary>
/// The GPU through Vulkan 1.3: one bindless heap, resources made from Core's descriptions, and one frame of commands
/// submitted at a time. Its <see cref="Render"/> runs before a renderer's, as a renderer is made after it, so it begins
/// the frame there: waits for the frame two before, takes the window's image, and leaves the staging ring empty for
/// what records next. The renderer records and submits; a frame nothing submitted still shows the window's image.
/// It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class VulkanGraphicsGem : IGem, IGraphicsApi, IDisposable
{
    private readonly Threads _threads;
    private readonly VulkanDevice _device;
    private readonly VulkanHeap _heap;
    private readonly VulkanResources _resources;
    private readonly VulkanFrames _frames;
    private readonly VulkanRecorder _recorder;
    private readonly VulkanSwapchain? _swapchain;
    private readonly Meter _meter;
    private readonly Gauge<long> _memoryUsage;
    private readonly Gauge<long> _memoryLimit;
    private bool _isBegun;
    private bool _isSubmitted;

    public VulkanGraphicsGem(App app, Threads threads, IWindow? window = null)
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
        _recorder = new VulkanRecorder(_device, _resources, _heap);
        if (drawn is not null && _device.Surface.IsNotNull)
            _swapchain = new VulkanSwapchain(_device, _resources, drawn);
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
        _swapchain?.Dispose();
        _frames.Dispose();
        _resources.Dispose();
        _heap.Dispose();
        _device.Dispose();
        _meter.Dispose();
    }

    public TextureDescription GetDescription(GpuTexture texture) =>
        _resources.GetTexture(texture.Id)?.Description
        ?? throw new ArgumentException($"Texture {texture.Id} is gone.", nameof(texture));

    public BufferDescription GetDescription(GpuBuffer buffer) =>
        _resources.GetBuffer(buffer.Id)?.Description
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
        if (_resources.RemoveBuffer(buffer.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(buffer.Id, removed));
    }

    public void Destroy(GpuTexture texture)
    {
        ThrowIfNotOnRender();
        if (_resources.RemoveTexture(texture.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(texture.Id, removed));
    }

    public void Destroy(GpuSampler sampler)
    {
        ThrowIfNotOnRender();
        VkSampler removed = _resources.RemoveSampler(sampler.Id);
        if (removed.IsNotNull)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(sampler.Id, removed));
    }

    public void Destroy(GpuPipeline pipeline)
    {
        ThrowIfNotOnRender();
        if (_resources.RemovePipeline(pipeline.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(pipeline.Id, removed));
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

    public ulong GetAddress(GpuAcceleration acceleration) => _resources.GetAcceleration(acceleration.Id)?.Address ?? 0;

    public void Destroy(GpuAcceleration acceleration)
    {
        ThrowIfNotOnRender();
        if (_resources.RemoveAcceleration(acceleration.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(acceleration.Id, removed));
    }

    public GpuUpload BeginUpload(int bytes)
    {
        ThrowIfNotOnRender();
        BeginFrame();

        return _frames.Upload(bytes, _device.Limits.UploadAlignment);
    }

    public Task<Result<ReadOnlyMemory<byte>>> ReadBackAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    )
    {
        if (_threads.IsCurrent(EngineThread.Render))
            return ReadBackOnRenderAsync(buffer, offset, bytes);

        return ReadBackFromWorkerAsync(buffer, offset, bytes, cancel);
    }

    public void Submit(in GpuCommands commands)
    {
        ThrowIfNotOnRender();
        BeginFrame();
        if (_isSubmitted)
        {
            Log.Warn("The frame was submitted already; a second set of commands is dropped.");

            return;
        }

        VulkanFrames.Frame frame = _frames.Current;
        _recorder.Record(frame, commands);
        // The window's image is shown whatever the commands left it as.
        if (_resources.GetTexture(WindowTarget.Id) is { } window && window.Layouts[0] != VkImageLayout.PresentSrcKHR)
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

        _isSubmitted = true;
        if (!WindowTarget.IsNone)
            _swapchain?.Present();

        WindowTarget = GpuTexture.None;
    }

    /// <summary>
    /// Begins the frame before the renderer's turn: finishes the frames the GPU is done with, so a readback awaited
    /// completes whether or not the renderer draws, submits last frame's commands if nothing did, then begins the next
    /// frame and takes the window's image.
    /// </summary>
    public void Render(in Frame frame)
    {
        _frames.Poll();
        _memoryUsage.Record(_resources.MemoryBytes);
        _memoryLimit.Record(_device.Limits.DeviceMemoryBytes);
        if (_isBegun && !_isSubmitted)
            Submit(GpuCommands.None);

        _isBegun = false;
        BeginFrame();
    }

    private void BeginFrame()
    {
        if (_isBegun)
            return;

        _isBegun = true;
        _isSubmitted = false;
        _frames.Begin();
        WindowTarget = _swapchain?.Acquire(_frames.Current.Acquired) ?? GpuTexture.None;
    }

    private Task<Result<ReadOnlyMemory<byte>>> ReadBackOnRenderAsync(GpuBuffer buffer, long offset, int bytes)
    {
        if (_resources.GetBuffer(buffer.Id) is not { } vulkanBuffer)
            return Task.FromResult<Result<ReadOnlyMemory<byte>>>(Result.Failure($"buffer {buffer.Id} is gone."));

        BeginFrame();

        return _frames.ReadBackAsync(buffer.Id, vulkanBuffer, offset, bytes);
    }

    private async Task<Result<ReadOnlyMemory<byte>>> ReadBackFromWorkerAsync(
        GpuBuffer buffer,
        long offset,
        int bytes,
        CancellationToken cancel
    )
    {
        await _threads.Enter(EngineThread.Render);
        cancel.ThrowIfCancellationRequested();

        return await ReadBackOnRenderAsync(buffer, offset, bytes).ConfigureAwait(false);
    }

    private void ThrowIfNotOnRender()
    {
        if (!_threads.IsCurrent(EngineThread.Render))
            throw new InvalidOperationException("The GPU's resources and frames are the render thread's.");
    }
}
