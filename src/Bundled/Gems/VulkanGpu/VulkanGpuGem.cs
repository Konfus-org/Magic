using System.Diagnostics.Metrics;
using System.Drawing;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.SdlBindings;
using Magic.Services;
using Vortice.Vulkan;

namespace Magic.VulkanGpu;

/// <summary>
/// <see cref="IGpu"/> on Vulkan: ids for Vulkan objects, uploads staged at the call and copied at the start of the
/// frame's submit, then the recorded commands (<see cref="VulkanRecorder"/>), in one command
/// buffer. A window is a render target like a texture, whose image is acquired the first time a frame draws into it;
/// the window itself is the platform gem's, found by its id in the SDL every gem shares, so the platform is made first
/// and disposed last. Two frames are in flight, counted by one timeline semaphore: a frame slot is reused once the GPU
/// has finished the frame last recorded from it, and what was released goes then. Everything here runs on the render
/// thread, which made the window.
/// </summary>
internal sealed unsafe class VulkanGpuGem : IGem, IGpu, IDisposable
{
    /// <summary>
    /// Frames the CPU records ahead of the GPU.
    /// </summary>
    private const int FramesInFlight = 2;

    private static readonly Meter _meter = new("Magic.Gems.VulkanGpu");

    /// <summary>
    /// For what spans up to a frame: a tenth of a millisecond to a second, with edges at the frame budgets of 240, 144,
    /// 120, 60 and 30 frames a second.
    /// </summary>
    private static readonly InstrumentAdvice<double> _frameBuckets = new()
    {
        HistogramBucketBoundaries =
        [
            0.0001, 0.00025, 0.0005, 0.001, 0.002,
            1.0 / 240, 1.0 / 144, 1.0 / 120, 1.0 / 60, 1.0 / 30,
            0.05, 0.1, 0.25, 0.5, 1,
        ],
    };
    private static readonly Counter<long> _staged = _meter.CreateCounter<long>(
        "magic.gems.vulkan_gpu.upload.size",
        "By",
        "Bytes staged to upload to the GPU.");
    private static readonly Histogram<double> _acquireWait = _meter.CreateHistogram(
        "magic.gems.vulkan_gpu.present.acquire.duration",
        "s",
        "How long the render thread waited for a window's next image: the time vsync holds the frame.",
        tags: null,
        advice: _frameBuckets);
    private static readonly Histogram<double> _groupDuration = _meter.CreateHistogram(
        "magic.gems.vulkan_gpu.group.duration",
        "s",
        "The GPU's time on each debug group of a frame, a renderer's pass, added up where one recurs, by the GPU's "
        + "own clock, as the frame runs.",
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries =
            [
                0.00001, 0.000025, 0.00005, 0.0001, 0.00025, 0.0005,
                0.001, 0.002, 0.004, 0.008, 0.016, 1.0 / 30, 0.1,
            ],
        });
    private static readonly Histogram<double> _frameDuration = _meter.CreateHistogram(
        "magic.gems.vulkan_gpu.frame.duration",
        "s",
        "The GPU's time on a frame, from its first command to its last, by the GPU's own clock.",
        tags: null,
        advice: _frameBuckets);

    private readonly VulkanGpuSettings _settings;
    private readonly Threads _threads;
    private readonly VulkanDevice _device;
    private readonly VulkanResources _resources;
    private readonly VulkanPipelines _pipelines;
    private readonly VulkanDescriptors _descriptors;
    private readonly VulkanWindows _windows;
    private readonly VulkanTransfers _transfers;
    private readonly VulkanTimer _timer;
    private readonly VulkanBarriers _barriers = new();
    private readonly VulkanRecorder _recorder;
    private readonly GpuCommands _commands = new();

    /// <summary>
    /// Per frame slot: its command pool and buffer, the number of the frame last recorded from it, and what was
    /// released by the time that frame was submitted, to destroy once the GPU has finished it.
    /// </summary>
    private readonly VkCommandPool[] _pools = new VkCommandPool[FramesInFlight];
    private readonly VkCommandBuffer[] _commandBuffers = new VkCommandBuffer[FramesInFlight];
    private readonly ulong[] _slotFrames = new ulong[FramesInFlight];
    private readonly List<(Kind Kind, object Item)>[] _retired = new List<(Kind Kind, object Item)>[FramesInFlight];

    /// <summary>
    /// What callers released since the last submit, by kind and id.
    /// </summary>
    private readonly List<(Kind Kind, uint Id)> _releasing = [];

    /// <summary>
    /// Every window that is a render target, by its id, so asking again hands back the same texture.
    /// </summary>
    private readonly Dictionary<uint, GpuTexture> _windowTargets = [];

    /// <summary>
    /// Captures asked for and not yet begun, by window target id: the next frame that draws into the window begins it;
    /// and the captures this frame has begun.
    /// </summary>
    private readonly Dictionary<uint, TaskCompletionSource<Result<GpuImage>>> _captures = [];
    private readonly List<Capture> _capturing = [];

    /// <summary>
    /// Whether the windows present on the display's refresh, and how they are composed, as the settings said when they
    /// were last followed.
    /// </summary>
    private bool _vsyncPresenting;
    private Composition _composing;

    /// <summary>
    /// The number of the frame being recorded, the value the timeline reaches once the GPU has finished it; its slot is
    /// this modulo <see cref="FramesInFlight"/>, taken back from the GPU the first time the frame needs it.
    /// </summary>
    private ulong _frame = 1;
    private bool _isSlotTaken;

    public VulkanGpuGem(VulkanGpuSettings settings, Threads threads)
    {
        _settings = settings;
        _threads = threads;
        _vsyncPresenting = settings.Vsync;
        _composing = settings.Composition;

        // The SDL whose windows this gem draws into is the platform gem's, which opens the windows and quits SDL as it
        // ends: made first, the platform is disposed last, once the GPU has let its windows go.
        if (!IsSdlStarted())
        {
            throw new InvalidOperationException(
                "VulkanGpu draws through the SDL a platform gem starts: list that gem before it in the project.");
        }

        _device = new VulkanDevice(settings);
        _resources = new VulkanResources(_device);
        _pipelines = new VulkanPipelines(_device);
        _descriptors = new VulkanDescriptors(_device, _resources, _pipelines, FramesInFlight);
        _windows = new VulkanWindows(_device, settings, _acquireWait, FramesInFlight);
        _transfers = new VulkanTransfers(_device, _resources, _staged, FramesInFlight);
        _timer = new VulkanTimer(_device, _groupDuration, _frameDuration, FramesInFlight);
        _recorder = new VulkanRecorder(_device, _resources, _pipelines, _descriptors, _windows, _barriers, _timer);
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            _retired[slot] = [];
            VkCommandPoolCreateInfo info = new()
            {
                flags = VkCommandPoolCreateFlags.Transient,
                queueFamilyIndex = _device.QueueFamily,
            };
            VkCommandPool pool;
            _device.Api.vkCreateCommandPool(&info, &pool);
            _pools[slot] = pool;
            _device.Api.vkAllocateCommandBuffer(pool, out VkCommandBuffer commandBuffer);
            _commandBuffers[slot] = commandBuffer;
        }
    }

    public string Name => _device.Name;

    public GpuShaderFormat ShaderFormat => GpuShaderFormat.Spirv;

    public GpuCommands Commands
    {
        get
        {
            ThrowIfNotRender();

            return _commands;
        }
    }

    public void Dispose()
    {
        foreach (TaskCompletionSource<Result<GpuImage>> capture in _captures.Values)
        {
            capture.TrySetResult(Result.Failure<GpuImage>("The GPU stopped before the window was drawn again."));
        }

        _device.Api.vkDeviceWaitIdle();
        FreeReleased();
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            DestroyRetired(slot);
            _device.Api.vkDestroyCommandPool(_pools[slot]);
        }

        foreach (VulkanPipeline pipeline in _resources.Pipelines.Live)
        {
            _pipelines.Destroy(pipeline);
        }

        _timer.Dispose();
        _transfers.Dispose();
        _descriptors.Dispose();
        _windows.Dispose();
        _pipelines.Dispose();
        _resources.Dispose();
        _device.Destroy();
    }

    /// <summary>
    /// Render runs from the last gem made to the first, so every gem that takes this one has recorded by now.
    /// </summary>
    public void Render(in Frame frame)
    {
        FollowSwapchain();
        Submit();
    }

    public GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes)
    {
        ThrowIfNotRender();

        VulkanBuffer buffer = _resources.CreateBuffer(bytes, usage.ToVulkan(), isHost: false);

        return new GpuBuffer(_resources.Buffers.Add(buffer));
    }

    public GpuTexture CreateTexture(in GpuTextureDescription description)
    {
        ThrowIfNotRender();

        return new GpuTexture(_resources.Textures.Add(_resources.CreateTexture(description)));
    }

    public GpuSampler CreateSampler(in GpuSamplerDescription description)
    {
        ThrowIfNotRender();

        return new GpuSampler(_resources.Samplers.Add(_resources.CreateSampler(description)));
    }

    public Result<GpuPipeline> CreatePipeline(GpuPipelineDescription description)
    {
        ThrowIfNotRender();

        Result<VulkanPipeline> made = _pipelines.CreateGraphics(description);

        return made.Ok && made.Value is { } pipeline
            ? Result.Success(new GpuPipeline(_resources.Pipelines.Add(pipeline)))
            : Result.Failure<GpuPipeline>(made.Error);
    }

    public Result<GpuPipeline> CreatePipeline(GpuShader compute)
    {
        ThrowIfNotRender();

        Result<VulkanPipeline> made = _pipelines.CreateCompute(compute);

        return made.Ok && made.Value is { } pipeline
            ? Result.Success(new GpuPipeline(_resources.Pipelines.Add(pipeline)))
            : Result.Failure<GpuPipeline>(made.Error);
    }

    public GpuTexture GetWindowTarget(IWindow window)
    {
        ThrowIfNotRender();

        if (_windowTargets.TryGetValue(window.Id, out GpuTexture target)) return target;

        // A headless window is none of SDL's: a target with no image, ever.
        SDL.Window* native = window.Id == 0 ? null : SDL.GetWindowFromID(window.Id);

        if (window.Id != 0 && native == null)
        {
            throw new InvalidOperationException(
                $"SDL knows no window {window.Id}: it must be the platform gem's, in the SDL this gem shares.");
        }

        VulkanWindow? claimed = native == null ? null : _windows.Claim(window.Id, native);
        GpuTextureDescription description = new(GpuFormat.Invalid, GpuTextureUsage.ColorTarget, 0, 0);
        VulkanTexture texture = new(description, VkImage.Null, VkDeviceMemory.Null, claimed);
        target = new GpuTexture(_resources.Textures.Add(texture));
        _windowTargets[window.Id] = target;

        return target;
    }

    public GpuFormat GetFormat(GpuTexture texture)
    {
        ThrowIfNotRender();

        return _resources.Textures[texture.Id]?.Format ?? GpuFormat.Invalid;
    }

    public Size GetSize(GpuTexture texture)
    {
        ThrowIfNotRender();

        if (_resources.Textures[texture.Id] is not { } stored) return Size.Empty;

        if (stored.Window is not { } window)
            return new Size((int)stored.Description.Width, (int)stored.Description.Height);

        SDL.Window* native = (SDL.Window*)window.Native;

        if ((SDL.GetWindowFlags(native) & SDL.WindowMinimized) != 0) return Size.Empty;

        int width;
        int height;

        return SDL.GetWindowSizeInPixels(native, &width, &height) ? new Size(width, height) : Size.Empty;
    }

    public void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty || _resources.Buffers[buffer.Id] is not { } stored) return;

        _transfers.Upload(TakeSlot(), stored, offset, data);
    }

    public void Upload(in GpuTextureRegion region, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty || _resources.Textures[region.Texture.Id] is not { Window: null } stored) return;

        _transfers.Upload(TakeSlot(), stored, region, data);
    }

    public void Release(GpuBuffer buffer)
    {
        ThrowIfNotRender();

        if (buffer != GpuBuffer.None) _releasing.Add((Kind.Buffer, buffer.Id));
    }

    public void Release(GpuTexture texture)
    {
        ThrowIfNotRender();

        if (texture == GpuTexture.None || _resources.Textures[texture.Id] is not { } stored) return;

        if (stored.Window is null && stored.Image.IsNotNull)
        {
            _releasing.Add((Kind.Texture, texture.Id));

            return;
        }

        foreach ((uint id, GpuTexture target) in _windowTargets)
        {
            if (target != texture) continue;

            _windowTargets.Remove(id);

            break;
        }

        _releasing.Add((Kind.Window, texture.Id));
    }

    public void Release(GpuSampler sampler)
    {
        ThrowIfNotRender();

        if (sampler != GpuSampler.None) _releasing.Add((Kind.Sampler, sampler.Id));
    }

    public void Release(GpuPipeline pipeline)
    {
        ThrowIfNotRender();

        if (_resources.Pipelines[pipeline.Id] is not null) _releasing.Add((Kind.Pipeline, pipeline.Id));
    }

    public Result<byte[]> Read(GpuBuffer buffer, uint bytes)
    {
        ThrowIfNotRender();

        if (_resources.Buffers[buffer.Id] is not { } stored || bytes == 0 || bytes > stored.Size)
            return Result.Failure<byte[]>($"Buffer {buffer.Id} is not one the GPU made, or none of it was asked for.");

        return _transfers.Read(stored, bytes, _barriers);
    }

    public Result<byte[]> Read(GpuTexture texture)
    {
        ThrowIfNotRender();

        if (_resources.Textures[texture.Id] is not { Window: null } stored || stored.Image.IsNull)
        {
            return Result.Failure<byte[]>($"Texture {texture.Id} is not one the GPU made, or is a window's, "
                + "whose image cannot be read: draw into a texture of your own and read that.");
        }

        return _transfers.Read(stored, _barriers);
    }

    public Task<Result<GpuImage>> CaptureAsync(GpuTexture window)
    {
        ThrowIfNotRender();

        if (_resources.Textures[window.Id]?.Window is not { } stored)
        {
            return Task.FromResult(Result.Failure<GpuImage>(
                $"Texture {window.Id} is no window's, or its window never has an image (headless)."));
        }

        if (stored.Format is not (GpuFormat.Rgba8Unorm or GpuFormat.Rgba8Srgb
            or GpuFormat.Bgra8Unorm or GpuFormat.Bgra8Srgb))
        {
            return Task.FromResult(
                Result.Failure<GpuImage>($"The window's images are {stored.Format}, which is not read back."));
        }

        if (!_captures.TryGetValue(window.Id, out TaskCompletionSource<Result<GpuImage>>? capture))
        {
            capture = new TaskCompletionSource<Result<GpuImage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captures[window.Id] = capture;
        }

        return capture.Task;
    }

    /// <summary>
    /// Whether SDL is loaded and started, as the platform gem starts it: its events are up, whatever else is.
    /// </summary>
    private static bool IsSdlStarted()
    {
        try
        {
            return (SDL.WasInit(SDL.InitEvents) & SDL.InitEvents) != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// A capture's pixels as RGBA, opaque: a window shows no alpha, whatever was written to it.
    /// </summary>
    private static GpuImage ReadPixels(in Capture capture, VulkanBuffer read)
    {
        byte[] pixels = new ReadOnlySpan<byte>(read.Mapped, (int)(capture.Width * capture.Height * 4)).ToArray();
        if (capture.Format is GpuFormat.Bgra8Unorm or GpuFormat.Bgra8Srgb)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
        }

        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = byte.MaxValue;
        }

        return new GpuImage((int)capture.Width, (int)capture.Height, pixels);
    }

    /// <summary>
    /// The GPU is the render thread's: the windows' images are acquired and presented there, and its objects are
    /// recorded into one command buffer.
    /// </summary>
    private void ThrowIfNotRender()
    {
        if (_threads.IsCurrent(EngineThread.Render)) return;

        throw new InvalidOperationException("The GPU is the render thread's: use it in Render, "
            + "or hand the work to the render thread (Threads.InvokeAsync).");
    }

    /// <summary>
    /// The slot the frame being recorded is drawn from, taken back from the GPU the first time the frame needs it:
    /// once the GPU has finished the frame last drawn from it, what that frame released goes, and its command buffer,
    /// descriptors, staging and timings are the new frame's.
    /// </summary>
    private int TakeSlot()
    {
        int slot = (int)(_frame % FramesInFlight);

        if (_isSlotTaken) return slot;

        _isSlotTaken = true;
        _device.WaitFor(_slotFrames[slot]);
        _timer.Collect(slot);
        DestroyRetired(slot);
        _device.Api.vkResetCommandPool(_pools[slot], VkCommandPoolResetFlags.None);
        _descriptors.Reset(slot);
        _transfers.Reset(slot);

        return slot;
    }

    /// <summary>
    /// Runs the queued uploads, then the frame's commands, then presents the windows they drew into, and clears the
    /// list. What was released since the last submit goes once this frame is done with it.
    /// </summary>
    private void Submit()
    {
        if (_commands.Count == 0 && _transfers.Count == 0)
        {
            FreeReleased();

            return;
        }

        int slot = TakeSlot();
        VkCommandBuffer commandBuffer = _commandBuffers[slot];
        _device.Api.vkBeginCommandBuffer(commandBuffer, VkCommandBufferUsageFlags.OneTimeSubmit);
        bool isTimed = _timer.IsListenedTo;
        if (isTimed) _timer.Begin(commandBuffer, slot);

        RecordUploads(commandBuffer, isTimed);
        _recorder.Record(_commands, commandBuffer, slot, isTimed);
        CopyCaptures(commandBuffer);
        PreparePresent(commandBuffer);
        if (isTimed) _timer.End(commandBuffer);

        _device.Api.vkEndCommandBuffer(commandBuffer);
        SubmitFrame(commandBuffer, slot);
        _windows.Present();
        FinishCaptures();

        _slotFrames[slot] = _frame;
        _frame++;
        _isSlotTaken = false;
        _commands.Clear();
        FreeReleased();
    }

    private void RecordUploads(VkCommandBuffer commandBuffer, bool isTimed)
    {
        if (_transfers.Count == 0) return;

        bool isGroupTimed = isTimed && _timer.Start(commandBuffer, VulkanTimer.UploadsGroup);
        _transfers.Record(commandBuffer, _barriers);
        if (isGroupTimed) _timer.Stop(commandBuffer);
    }

    /// <summary>
    /// Copies the image of each window a capture waits for, if the frame drew into it, for the capture to read once
    /// the frame is done. After every command, so outside any pass.
    /// </summary>
    private void CopyCaptures(VkCommandBuffer commandBuffer)
    {
        foreach ((uint id, TaskCompletionSource<Result<GpuImage>> done) in _captures)
        {
            if (_resources.Textures[id]?.Window is not { Image: >= 0 } window) continue;

            if (!window.CanCopy)
            {
                done.SetResult(Result.Failure<GpuImage>("The window's images cannot be copied on this GPU."));
                _capturing.Add(new Capture(id, null, 0, 0, GpuFormat.Invalid, done));

                continue;
            }

            int image = window.Image;
            (uint width, uint height) = (window.Extent.width, window.Extent.height);
            VulkanBuffer read = _resources.CreateBuffer(
                width * height * 4,
                VkBufferUsageFlags.TransferDst,
                isHost: true);
            _barriers.Use(
                window.Images[image],
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
                ref window.States[image],
                VkImageLayout.TransferSrcOptimal,
                VkPipelineStageFlags2.Copy,
                VkAccessFlags2.TransferRead,
                isWrite: false);
            _barriers.Record(_device.Api, commandBuffer);
            VkBufferImageCopy copy = new()
            {
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageExtent = new VkExtent3D(width, height, 1),
            };
            _device.Api.vkCmdCopyImageToBuffer(
                commandBuffer,
                window.Images[image],
                VkImageLayout.TransferSrcOptimal,
                read.Handle,
                1,
                &copy);
            _capturing.Add(new Capture(id, read, width, height, window.Format, done));
        }

        foreach (Capture capture in _capturing)
        {
            _captures.Remove(capture.Window);
        }
    }

    /// <summary>
    /// Waits for the frame the captures were in, then hands each its pixels and lets its read go.
    /// </summary>
    private void FinishCaptures()
    {
        if (_capturing.Count == 0) return;

        _device.WaitFor(_frame);
        foreach (Capture capture in _capturing)
        {
            if (capture.Read is not { } read) continue;

            capture.Done.SetResult(Result.Success(ReadPixels(capture, read)));
            _resources.Destroy(read);
        }

        _capturing.Clear();
    }

    /// <summary>
    /// Every image a window drew into this frame, made ready to present once what drew into it is done.
    /// </summary>
    private void PreparePresent(VkCommandBuffer commandBuffer)
    {
        foreach (VulkanWindow window in _windows.Claimed)
        {
            if (window.Image < 0) continue;

            _barriers.Use(
                window.Images[window.Image],
                new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
                ref window.States[window.Image],
                VkImageLayout.PresentSrcKHR,
                VkPipelineStageFlags2.None,
                VkAccessFlags2.None,
                isWrite: false);
        }

        _barriers.Record(_device.Api, commandBuffer);
    }

    /// <summary>
    /// Submits the frame: it starts drawing into a window's image once the image is acquired, signals each image drawn
    /// into for its presenting, and the timeline with the frame's number once it is done.
    /// </summary>
    private void SubmitFrame(VkCommandBuffer commandBuffer, int slot)
    {
        int windows = _windows.Claimed.Count;
        VkSemaphoreSubmitInfo* waits = stackalloc VkSemaphoreSubmitInfo[Math.Max(windows, 1)];
        VkSemaphoreSubmitInfo* signals = stackalloc VkSemaphoreSubmitInfo[windows + 1];
        uint waitCount = 0;
        uint signalCount = 0;
        foreach (VulkanWindow window in _windows.Claimed)
        {
            if (window.Image < 0) continue;

            waits[waitCount++] = new VkSemaphoreSubmitInfo
            {
                semaphore = window.Acquired[slot],
                stageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            };
            signals[signalCount++] = new VkSemaphoreSubmitInfo
            {
                semaphore = window.Drawn[window.Image],
                stageMask = VkPipelineStageFlags2.AllCommands,
            };
        }

        signals[signalCount++] = new VkSemaphoreSubmitInfo
        {
            semaphore = _device.Timeline,
            value = _frame,
            stageMask = VkPipelineStageFlags2.AllCommands,
        };
        VkCommandBufferSubmitInfo buffer = new() { commandBuffer = commandBuffer };
        VkSubmitInfo2 submit = new()
        {
            waitSemaphoreInfoCount = waitCount,
            pWaitSemaphoreInfos = waits,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &buffer,
            signalSemaphoreInfoCount = signalCount,
            pSignalSemaphoreInfos = signals,
        };
        VkResult result = _device.Api.vkQueueSubmit2(_device.Queue, 1, &submit, VkFence.Null);
        if (result != VkResult.Success) Debugging.Error($"The GPU did not take frame {_frame}: {result}");
    }

    /// <summary>
    /// What callers released leaves the tables; its Vulkan objects go once the last frame submitted is done, since that
    /// and any frame before it may still use them.
    /// </summary>
    private void FreeReleased()
    {
        List<(Kind Kind, object Item)> retired = _retired[(int)((_frame + FramesInFlight - 1) % FramesInFlight)];
        foreach ((Kind kind, uint id) in _releasing)
        {
            object? item = kind switch
            {
                Kind.Buffer => _resources.Buffers.Free(id),
                Kind.Texture => _resources.Textures.Free(id),
                Kind.Window => _resources.Textures.Free(id)?.Window,
                Kind.Sampler => _resources.Samplers.Free(id),
                Kind.Pipeline => _resources.Pipelines.Free(id),
                _ => null,
            };
            if (item is not null) retired.Add((kind, item));
        }

        _releasing.Clear();
    }

    private void DestroyRetired(int slot)
    {
        foreach ((Kind kind, object item) in _retired[slot])
        {
            switch (kind)
            {
                case Kind.Buffer:
                    _resources.Destroy((VulkanBuffer)item);
                    break;
                case Kind.Texture:
                    _resources.Destroy((VulkanTexture)item);
                    break;
                case Kind.Window:
                    _windows.Release((VulkanWindow)item);
                    break;
                case Kind.Sampler:
                    _device.Api.vkDestroySampler((VkSampler)item);
                    break;
                case Kind.Pipeline:
                    _pipelines.Destroy((VulkanPipeline)item);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(slot), kind, "There is no such kind of GPU object.");
            }
        }

        _retired[slot].Clear();
    }

    /// <summary>
    /// Presents every window the way the settings now say, when vsync or the composition was changed while running:
    /// the window's image is in the new composition's format from the next frame.
    /// </summary>
    private void FollowSwapchain()
    {
        if (_vsyncPresenting == _settings.Vsync && _composing == _settings.Composition) return;

        _vsyncPresenting = _settings.Vsync;
        _composing = _settings.Composition;
        _windows.Follow();
    }

    /// <summary>
    /// What a kind of handle is, for destroying its Vulkan objects.
    /// </summary>
    private enum Kind : byte
    {
        Buffer,
        Texture,
        Window,
        Sampler,
        Pipeline,
    }

    /// <summary>
    /// A capture begun this frame: the window target's id, where its image was copied to (none when it could not be),
    /// the image's size and format, and who waits for it.
    /// </summary>
    private readonly record struct Capture(
        uint Window,
        VulkanBuffer? Read,
        uint Width,
        uint Height,
        GpuFormat Format,
        TaskCompletionSource<Result<GpuImage>> Done);
}
