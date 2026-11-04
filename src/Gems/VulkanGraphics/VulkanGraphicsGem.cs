using Magic;
using Magic.Debugging;
using Magic.Gpu;
using Magic.Platform;
using Magic.Plugins;
using Magic.Threading;
using Magic.Utils;
using Vortice.Vulkan;

namespace VulkanGraphics;

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
    private bool _isBegun;
    private bool _isSubmitted;

    public VulkanGraphicsGem(App app, Threads threads, IWindow? window = null)
    {
        _threads = threads;
        IWindow? drawn = app.IsHeadless ? null : window;
        _device = new VulkanDevice(drawn, wantsValidation: IsDebugBuild);
        _heap = new VulkanHeap(_device.Api, _device.Features.HasRayQuery);
        _resources = new VulkanResources(_device, _heap);
        _frames = new VulkanFrames(_device, _resources);
        _recorder = new VulkanRecorder(_device, _resources, _heap);
        if (drawn is not null && _device.Surface.IsNotNull)
            _swapchain = new VulkanSwapchain(_device, _resources, drawn);
    }

    public GpuFeatures Features => _device.Features;

    public GpuLimits Limits => _device.Limits;

    public GpuTexture WindowTarget { get; private set; }

    public GpuTimings Timings => _frames.Timings;

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
    }

    public TextureDescription GetDescription(GpuTexture texture) =>
        _resources.GetTexture(texture.Id)?.Description
        ?? throw new ArgumentException($"Texture {texture.Id} is gone.", nameof(texture));

    public BufferDescription GetDescription(GpuBuffer buffer) =>
        _resources.GetBuffer(buffer.Id)?.Description
        ?? throw new ArgumentException($"Buffer {buffer.Id} is gone.", nameof(buffer));

    public Result<GpuBuffer> CreateBuffer(in BufferDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreateBuffer(description);
    }

    public Result<GpuTexture> CreateTexture(in TextureDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreateTexture(description);
    }

    public Result<GpuSampler> CreateSampler(in SamplerDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreateSampler(description);
    }

    public Result<GpuPipeline> CreatePipeline(in ComputePipelineDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreatePipeline(description);
    }

    public Result<GpuPipeline> CreatePipeline(in GraphicsPipelineDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreatePipeline(description);
    }

    public void Destroy(GpuBuffer buffer)
    {
        ThrowIfNotOnMain();
        if (_resources.RemoveBuffer(buffer.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(buffer.Id, removed));
    }

    public void Destroy(GpuTexture texture)
    {
        ThrowIfNotOnMain();
        if (_resources.RemoveTexture(texture.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(texture.Id, removed));
    }

    public void Destroy(GpuSampler sampler)
    {
        ThrowIfNotOnMain();
        VkSampler removed = _resources.RemoveSampler(sampler.Id);
        if (removed.IsNotNull)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(sampler.Id, removed));
    }

    public void Destroy(GpuPipeline pipeline)
    {
        ThrowIfNotOnMain();
        if (_resources.RemovePipeline(pipeline.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(pipeline.Id, removed));
    }

    public Result<GpuAcceleration> CreateAcceleration(in BottomLevelDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreateAcceleration(description);
    }

    public Result<GpuAcceleration> CreateAcceleration(in TopLevelDescription description)
    {
        ThrowIfNotOnMain();

        return _resources.CreateAcceleration(description);
    }

    public ulong GetAddress(GpuAcceleration acceleration) => _resources.GetAcceleration(acceleration.Id)?.Address ?? 0;

    public void Destroy(GpuAcceleration acceleration)
    {
        ThrowIfNotOnMain();
        if (_resources.RemoveAcceleration(acceleration.Id) is { } removed)
            _frames.Current.Trash.Add(VulkanFrames.Trash.Of(acceleration.Id, removed));
    }

    public GpuUpload BeginUpload(int bytes)
    {
        ThrowIfNotOnMain();
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
        if (_threads.IsCurrent(EngineThread.Main))
            return ReadBackOnMainAsync(buffer, offset, bytes);

        return ReadBackFromWorkerAsync(buffer, offset, bytes, cancel);
    }

    public void Submit(in GpuCommands commands)
    {
        ThrowIfNotOnMain();
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
    /// Begins the frame before the renderer's turn: last frame's commands if nothing submitted them, then the next
    /// frame and the window's image.
    /// </summary>
    public void Render(in Frame frame)
    {
        if (_isBegun && !_isSubmitted)
            Submit(GpuCommands.None);

        _isBegun = false;
        BeginFrame();
    }

    /// <summary>
    /// Finishes the frames the GPU is done with, so a readback awaited completes whether or not the renderer draws.
    /// </summary>
    public void Update(in Frame frame) => _frames.Poll();

    private void BeginFrame()
    {
        if (_isBegun)
            return;

        _isBegun = true;
        _isSubmitted = false;
        _frames.Begin();
        WindowTarget = _swapchain?.Acquire(_frames.Current.Acquired) ?? GpuTexture.None;
    }

    private Task<Result<ReadOnlyMemory<byte>>> ReadBackOnMainAsync(GpuBuffer buffer, long offset, int bytes)
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
        await _threads.Enter(EngineThread.Main);
        cancel.ThrowIfCancellationRequested();

        return await ReadBackOnMainAsync(buffer, offset, bytes).ConfigureAwait(false);
    }

    private void ThrowIfNotOnMain()
    {
        if (!_threads.IsCurrent(EngineThread.Main))
            throw new InvalidOperationException("The GPU's resources and frames are the main thread's.");
    }
}
