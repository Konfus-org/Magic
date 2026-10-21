using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Numerics;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.SdlBindings;
using Magic.Services;

namespace Magic.SdlGpu;

/// <summary>
/// <see cref="IGpu"/> on SDL GPU, which drives Vulkan, Direct3D 12 or Metal: handles to SDL objects, uploads staged
/// at the call and copied in one pass at the start of the frame's submit, then one SDL call per recorded command. A
/// window is a render target like a texture, an entry of the texture table whose image is acquired the first time a
/// frame draws into it; the window itself is the platform gem's, found by its id in the SDL every gem shares, so
/// the platform is made first and disposed last. A released object stays valid until the end of the next submit, and
/// its SDL object goes once no frame in flight can use it. Vulkan is asked for 1.1, whose subgroup operations are
/// HLSL's wave intrinsics. Everything here runs on the render thread, which made the window: SDL acquires a window's
/// image only there, and a command buffer is used only on the thread that acquired it.
/// </summary>
internal sealed unsafe class SdlGpuGem : IGem, IGpu, IDisposable
{
    /// <summary>
    /// Frames the CPU records ahead of the GPU; SDL is told the same.
    /// </summary>
    private const int FramesInFlight = 2;

    /// <summary>
    /// <c>VK_MAKE_API_VERSION(0, 1, 1, 0)</c>.
    /// </summary>
    private const uint Vulkan11 = (1u << 22) | (1u << 12);

    private const uint InitialStagingBytes = 1024 * 1024;

    private static readonly Meter _meter = new("Magic.Gems.SdlGpu");
    private static readonly Counter<long> _staged = _meter.CreateCounter<long>(
        "magic.gems.sdl_gpu.upload.size",
        "By",
        "Bytes staged to upload to the GPU.");
    private static readonly Histogram<double> _acquireWait = _meter.CreateHistogram(
        "magic.gems.sdl_gpu.present.acquire.duration",
        "s",
        "How long the render thread waited for a window's next image: the time vsync holds the frame.",
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries =
            [
                0.0001, 0.00025, 0.0005, 0.001, 0.002,
                1.0 / 240, 1.0 / 144, 1.0 / 120, 1.0 / 60, 1.0 / 30,
                0.05, 0.1, 0.25, 0.5, 1,
            ],
        });

    private readonly SdlGpuSettings _settings;
    private readonly Threads _threads;
    private readonly SDL.GPUDevice* _device;
    private readonly GpuCommands _commands = new();
    private readonly Table<nint> _buffers = new();
    private readonly Table<TextureObject> _textures = new();
    private readonly Table<nint> _samplers = new();
    private readonly Table<PipelineObject> _pipelines = new();

    /// <summary>
    /// Uploads queued since the last submit, in call order.
    /// </summary>
    private readonly List<Transfer> _transfers = [];

    /// <summary>
    /// What callers released since the last submit, by kind and id.
    /// </summary>
    private readonly List<(Kind Kind, uint Id)> _releasing = [];

    /// <summary>
    /// Staging buffers outgrown since the last submit: that submit's copy pass still reads what was staged in them.
    /// </summary>
    private readonly List<nint> _outgrown = [];

    /// <summary>
    /// For each frame slot, the fence of the frame last submitted from it and the SDL objects to release once it is
    /// done.
    /// </summary>
    private readonly nint[] _fences = new nint[FramesInFlight];
    private readonly List<(Kind Kind, nint Handle)>[] _freed = new List<(Kind Kind, nint Handle)>[FramesInFlight];

    /// <summary>
    /// Every window that is a render target, by its id, so asking again hands back the same texture.
    /// </summary>
    private readonly Dictionary<uint, GpuTexture> _windows = [];

    /// <summary>
    /// The images of the window targets this frame has drawn into, by texture id: 0 for a window with none.
    /// </summary>
    private readonly Dictionary<uint, nint> _images = [];

    /// <summary>
    /// Captures asked for and not yet begun, by window target id: the next frame that draws into the window begins it.
    /// </summary>
    private readonly Dictionary<uint, TaskCompletionSource<Result<GpuImage>>> _captures = [];

    /// <summary>
    /// The captures this frame has begun.
    /// </summary>
    private readonly List<Capture> _capturing = [];

    /// <summary>
    /// Whether the windows present on the display's refresh, as the settings said when they were last followed.
    /// </summary>
    private bool _vsyncPresenting;
    private Composition _composing;

    /// <summary>
    /// Where uploads are written until the next submit's copy pass moves them: mapped on the first upload of a frame,
    /// cycled so SDL hands out fresh memory while a frame in flight still reads the old, unmapped at submit. A frame
    /// that needs more swaps in one twice the size, which stays.
    /// </summary>
    private SDL.GPUTransferBuffer* _staging;
    private uint _stagingCapacity = InitialStagingBytes;
    private byte* _stagingMapped;
    private uint _stagingUsed;

    /// <summary>
    /// Counts submitted frames; a frame's slot is this modulo <see cref="FramesInFlight"/>.
    /// </summary>
    private ulong _frameNumber;

    private SDL.GPURenderPass* _renderPass;
    private SDL.GPUComputePass* _computePass;

    /// <summary>
    /// The render pass draws into a window with no image this frame: its commands are skipped to its end.
    /// </summary>
    private bool _isSkipping;

    public SdlGpuGem(SdlGpuSettings settings, Threads threads)
    {
        _settings = settings;
        _vsyncPresenting = settings.Vsync;
        _composing = settings.Composition;
        _threads = threads;
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            _freed[slot] = [];
        }

        // The SDL this gem draws through is the platform gem's, which opens the windows and quits SDL as it ends:
        // made first, the platform is disposed last, once the GPU has let its windows go.
        if (!IsSdlStarted())
        {
            throw new InvalidOperationException(
                "SdlGpu draws through the SDL a platform gem starts: list that gem before it in the project.");
        }

        // SDL opens a GPU only with its video subsystem up, which a headless platform leaves down. SDL counts each
        // subsystem's starts, so this one is undone in Dispose, before the platform quits SDL.
        if (!SDL.InitSubSystem(SDL.InitVideo))
        {
            throw new InvalidOperationException(
                $"SDL could not start its video subsystem for the GPU: {CString.Read(SDL.GetError())}");
        }

        SDL.GPUVulkanOptions vulkan = new() { VulkanApiVersion = Vulkan11 };
        uint properties = SDL.CreateProperties();
        SDL.SetBooleanProperty(properties, SDL.PropGpuDeviceCreateShadersSpirvBoolean, value: true);
        SDL.SetBooleanProperty(properties, SDL.PropGpuDeviceCreateShadersDxilBoolean, value: true);
        SDL.SetBooleanProperty(properties, SDL.PropGpuDeviceCreateShadersMslBoolean, value: true);
        SDL.SetBooleanProperty(properties, SDL.PropGpuDeviceCreateDebugmodeBoolean, settings.Validation);
        SDL.SetBooleanProperty(properties, SDL.PropGpuDeviceCreatePreferlowpowerBoolean, settings.LowPower);
        SDL.SetPointerProperty(properties, SDL.PropGpuDeviceCreateVulkanOptionsPointer, &vulkan);
        if (settings.Driver.ToSdl() is { Length: > 0 } driver)
        {
            fixed (byte* driverName = CString.ToBytes(driver))
            {
                SDL.SetStringProperty(properties, SDL.PropGpuDeviceCreateNameString, driverName);
            }
        }

        _device = SDL.CreateGPUDeviceWithProperties(properties);
        SDL.DestroyProperties(properties);
        if (_device == null)
        {
            string error = CString.Read(SDL.GetError());
            SDL.QuitSubSystem(SDL.InitVideo);
            throw new InvalidOperationException($"SDL could not open a GPU device: {error}");
        }

        uint formats = SDL.GetGPUShaderFormats(_device);
        bool hasSpirv = (formats & SDL.GpuShaderformatSpirv) != 0;
        bool hasDxil = (formats & SDL.GpuShaderformatDxil) != 0;
        ShaderFormat = (hasSpirv, hasDxil) switch
        {
            (true, _) => GpuShaderFormat.Spirv,
            (false, true) => GpuShaderFormat.Dxil,
            _ => GpuShaderFormat.Msl,
        };

        string name;
        fixed (byte* unnamed = "an unnamed GPU"u8)
        {
            name = CString.Read(
                SDL.GetStringProperty(SDL.GetGPUDeviceProperties(_device), SDL.PropGpuDeviceNameString, unnamed));
        }

        Name = $"{name} ({CString.Read(SDL.GetGPUDeviceDriver(_device))})";
        SDL.SetGPUAllowedFramesInFlight(_device, FramesInFlight);
        _staging = CreateTransferBuffer(SDL.GPUTransferBufferUsage.Upload, _stagingCapacity);
        Debugging.Info($"GPU: {Name}, shaders {ShaderFormat}, validation {settings.Validation}");
    }

    public string Name { get; }

    public GpuShaderFormat ShaderFormat { get; }

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

        SDL.WaitForGPUIdle(_device);
        for (int slot = 0; slot < FramesInFlight; slot++)
        {
            ReleaseFrame(slot);
        }

        foreach (nint buffer in _buffers.Live)
        {
            Release(Kind.Buffer, buffer);
        }

        foreach (TextureObject texture in _textures.Live)
        {
            if (texture.Window is { } window)
            {
                if (window.Native != 0) Release(Kind.Window, window.Native);

                continue;
            }

            Release(Kind.Texture, texture.Handle);
        }

        foreach (nint sampler in _samplers.Live)
        {
            Release(Kind.Sampler, sampler);
        }

        foreach (PipelineObject pipeline in _pipelines.Live)
        {
            Release(pipeline.Kind, pipeline.Handle);
        }

        foreach (nint staging in _outgrown)
        {
            Release(Kind.TransferBuffer, staging);
        }

        UnmapStaging();
        SDL.ReleaseGPUTransferBuffer(_device, _staging);
        SDL.DestroyGPUDevice(_device);
        SDL.QuitSubSystem(SDL.InitVideo);
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

        SDL.GPUBufferCreateInfo info = new() { Usage = usage.ToSdl(), Size = bytes };
        SDL.GPUBuffer* buffer = SDL.CreateGPUBuffer(_device, &info);
        return buffer == null ? throw MakeFailure($"a {bytes}-byte buffer") : new GpuBuffer(_buffers.Add((nint)buffer));
    }

    public GpuTexture CreateTexture(in GpuTextureDescription description)
    {
        ThrowIfNotRender();

        SDL.GPUTextureCreateInfo info = new()
        {
            Type = description.Kind.ToSdl(),
            Format = description.Format.ToSdl(),
            Usage = description.Usage.ToSdl(),
            Width = description.Width,
            Height = description.Height,
            LayerCountOrDepth = description.Kind == GpuTextureKind.Cube ? 6 : description.Layers,
            NumLevels = description.Levels,
            SampleCount = SDL.GPUSampleCount.Count1,
        };
        SDL.GPUTexture* texture = SDL.CreateGPUTexture(_device, &info);
        if (texture == null)
        {
            throw MakeFailure($"a {description.Width} x {description.Height} {description.Format} texture");
        }

        uint depth = description.Kind == GpuTextureKind.Texture3D ? description.Layers : 1;
        return new GpuTexture(_textures.Add(
            new TextureObject((nint)texture, description.Format, description.Width, description.Height, depth)));
    }

    public GpuTexture GetWindowTarget(IWindow window)
    {
        ThrowIfNotRender();

        if (_windows.TryGetValue(window.Id, out GpuTexture target)) return target;

        // A headless window is none of SDL's: a target with no image, ever.
        SDL.Window* native = window.Id == 0 ? null : SDL.GetWindowFromID(window.Id);
        if (window.Id != 0 && native == null)
        {
            throw new InvalidOperationException(
                $"SDL knows no window {window.Id}: it must be the platform gem's, in the SDL this gem shares.");
        }

        GpuFormat format = native == null ? GpuFormat.Invalid : Claim(native, window.Id);
        target = new GpuTexture(
            _textures.Add(new TextureObject(0, format, 0, 0, 1, new WindowObject((nint)native, window.Id))));
        _windows[window.Id] = target;
        return target;
    }

    public GpuFormat GetFormat(GpuTexture texture)
    {
        ThrowIfNotRender();

        return _textures[texture.Id].Format;
    }

    public Size GetSize(GpuTexture texture)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[texture.Id];
        if (stored.Window is not { } window) return new Size((int)stored.Width, (int)stored.Height);

        SDL.Window* native = (SDL.Window*)window.Native;
        if (native == null || (SDL.GetWindowFlags(native) & SDL.WindowMinimized) != 0) return Size.Empty;

        int width;
        int height;
        return SDL.GetWindowSizeInPixels(native, &width, &height) ? new Size(width, height) : Size.Empty;
    }

    public GpuSampler CreateSampler(in GpuSamplerDescription description)
    {
        ThrowIfNotRender();

        SDL.GPUSamplerCreateInfo info = new()
        {
            MinFilter = description.Filter.ToSdl(),
            MagFilter = description.Filter.ToSdl(),
            MipmapMode = description.Filter.ToSdlMipmaps(),
            AddressModeU = description.Wrap.ToSdl(),
            AddressModeV = description.Wrap.ToSdl(),
            AddressModeW = description.Wrap.ToSdl(),
            MaxAnisotropy = description.Anisotropy,
            EnableAnisotropy = description.Anisotropy > 1,
            CompareOp = (description.Compare ?? GpuCompare.Never).ToSdl(),
            EnableCompare = description.Compare is not null,
            MaxLod = 1000,
        };
        SDL.GPUSampler* sampler = SDL.CreateGPUSampler(_device, &info);
        return sampler == null ? throw MakeFailure("a sampler") : new GpuSampler(_samplers.Add((nint)sampler));
    }

    public Result<GpuPipeline> CreatePipeline(GpuPipelineDescription description)
    {
        ThrowIfNotRender();

        SDL.GPUShader* vertex = CreateShader(description.Vertex);
        if (vertex == null)
        {
            return Result.Failure<GpuPipeline>(
                $"The GPU would not make the vertex shader: {CString.Read(SDL.GetError())}");
        }

        SDL.GPUShader* fragment = CreateShader(description.Fragment);
        if (fragment == null)
        {
            SDL.ReleaseGPUShader(_device, vertex);
            return Result.Failure<GpuPipeline>(
                $"The GPU would not make the fragment shader: {CString.Read(SDL.GetError())}");
        }

        SDL.GPUVertexBufferDescription* buffers =
            stackalloc SDL.GPUVertexBufferDescription[description.VertexBuffers.Count];
        for (int i = 0; i < description.VertexBuffers.Count; i++)
        {
            GpuVertexBuffer buffer = description.VertexBuffers[i];
            buffers[i] = new SDL.GPUVertexBufferDescription
            {
                Slot = buffer.Slot,
                Pitch = buffer.Stride,
                InputRate = buffer.PerInstance ? SDL.GPUVertexInputRate.Instance : SDL.GPUVertexInputRate.Vertex,
            };
        }

        SDL.GPUVertexAttribute* inputs = stackalloc SDL.GPUVertexAttribute[description.VertexInputs.Count];
        for (int i = 0; i < description.VertexInputs.Count; i++)
        {
            GpuVertexInput input = description.VertexInputs[i];
            inputs[i] = new SDL.GPUVertexAttribute
            {
                Location = input.Location,
                BufferSlot = input.Slot,
                Format = input.Format.ToSdl(),
                Offset = input.Offset
            };
        }

        SDL.GPUColorTargetDescription* colors =
            stackalloc SDL.GPUColorTargetDescription[description.ColorFormats.Count];
        for (int i = 0; i < description.ColorFormats.Count; i++)
        {
            GpuBlend blend = i < description.Blends.Count ? description.Blends[i] : GpuBlend.None;
            colors[i] = new SDL.GPUColorTargetDescription
            {
                Format = description.ColorFormats[i].ToSdl(),
                BlendState = blend.ToSdl()
            };
        }

        bool hasDepth = description.DepthFormat != GpuFormat.Invalid;
        SDL.GPUGraphicsPipelineCreateInfo info = new()
        {
            VertexShader = vertex,
            FragmentShader = fragment,
            VertexInputState = new SDL.GPUVertexInputState
            {
                VertexBufferDescriptions = buffers,
                NumVertexBuffers = (uint)description.VertexBuffers.Count,
                VertexAttributes = inputs,
                NumVertexAttributes = (uint)description.VertexInputs.Count,
            },
            PrimitiveType = SDL.GPUPrimitiveType.Trianglelist,
            RasterizerState = new SDL.GPURasterizerState
            {
                FillMode = SDL.GPUFillMode.Fill,
                CullMode = description.Cull.ToSdl(),
                FrontFace = SDL.GPUFrontFace.Clockwise,
                DepthBiasConstantFactor = description.DepthBias,
                DepthBiasSlopeFactor = description.DepthBiasSlope,
                DepthBiasClamp = description.DepthBiasClamp,
                EnableDepthBias = description.DepthBias != 0 || description.DepthBiasSlope != 0,
                EnableDepthClip = description.DepthClip,
            },
            MultisampleState = new SDL.GPUMultisampleState { SampleCount = SDL.GPUSampleCount.Count1 },
            DepthStencilState = new SDL.GPUDepthStencilState
            {
                CompareOp = description.DepthCompare.ToSdl(),
                EnableDepthTest = hasDepth,
                EnableDepthWrite = hasDepth && description.DepthWrite,
            },
            TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
            {
                ColorTargetDescriptions = colors,
                NumColorTargets = (uint)description.ColorFormats.Count,
                DepthStencilFormat = description.DepthFormat.ToSdl(),
                HasDepthStencilTarget = hasDepth,
            },
        };
        SDL.GPUGraphicsPipeline* pipeline = SDL.CreateGPUGraphicsPipeline(_device, &info);
        string error = pipeline == null ? CString.Read(SDL.GetError()) : string.Empty;
        SDL.ReleaseGPUShader(_device, vertex);
        SDL.ReleaseGPUShader(_device, fragment);
        return pipeline == null
            ? Result.Failure<GpuPipeline>($"The GPU would not make the pipeline: {error}")
            : Result.Success(
                new GpuPipeline(_pipelines.Add(new PipelineObject((nint)pipeline, Kind.GraphicsPipeline))));
    }

    public Result<GpuPipeline> CreatePipeline(GpuShader compute)
    {
        ThrowIfNotRender();

        byte[] entryPoint = CString.ToBytes(compute.EntryPoint);
        fixed (byte* code = compute.Code.Span)
        fixed (byte* entry = entryPoint)
        {
            SDL.GPUComputePipelineCreateInfo info = new()
            {
                Code = code,
                CodeSize = (nuint)compute.Code.Length,
                Entrypoint = entry,
                Format = ShaderFormat.ToSdl(),
                NumSamplers = compute.Samplers,
                NumReadonlyStorageTextures = compute.StorageTextures,
                NumReadonlyStorageBuffers = compute.StorageBuffers,
                NumReadwriteStorageTextures = compute.ReadWriteStorageTextures,
                NumReadwriteStorageBuffers = compute.ReadWriteStorageBuffers,
                NumUniformBuffers = compute.UniformBuffers,
                ThreadcountX = compute.ThreadCountX,
                ThreadcountY = compute.ThreadCountY,
                ThreadcountZ = compute.ThreadCountZ,
            };
            SDL.GPUComputePipeline* pipeline = SDL.CreateGPUComputePipeline(_device, &info);
            return pipeline == null
                ? Result.Failure<GpuPipeline>(
                    $"The GPU would not make the compute pipeline: {CString.Read(SDL.GetError())}")
                : Result.Success(
                    new GpuPipeline(_pipelines.Add(new PipelineObject((nint)pipeline, Kind.ComputePipeline))));
        }
    }

    public void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty) return;

        (nint staged, uint stagedOffset) = Stage(data);
        _transfers.Add(new Transfer(staged, stagedOffset, (uint)data.Length, buffer, offset, default));
    }

    public void Upload(in GpuTextureRegion region, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty) return;

        (nint staged, uint stagedOffset) = Stage(data);
        _transfers.Add(new Transfer(staged, stagedOffset, (uint)data.Length, default, 0, region));
    }

    public void Release(GpuBuffer buffer)
    {
        ThrowIfNotRender();

        if (buffer != GpuBuffer.None) _releasing.Add((Kind.Buffer, buffer.Id));
    }

    public void Release(GpuTexture texture)
    {
        ThrowIfNotRender();

        if (texture == GpuTexture.None) return;

        if (_textures[texture.Id].Window is { } window)
        {
            _windows.Remove(window.Id);
            _releasing.Add((Kind.Window, texture.Id));
            return;
        }

        _releasing.Add((Kind.Texture, texture.Id));
    }

    public void Release(GpuSampler sampler)
    {
        ThrowIfNotRender();

        if (sampler != GpuSampler.None) _releasing.Add((Kind.Sampler, sampler.Id));
    }

    public void Release(GpuPipeline pipeline)
    {
        ThrowIfNotRender();

        if (_pipelines[pipeline.Id] is { Handle: not 0 } stored) _releasing.Add((stored.Kind, pipeline.Id));
    }

    public Result<byte[]> Read(GpuBuffer buffer, uint bytes)
    {
        ThrowIfNotRender();

        nint handle = _buffers[buffer.Id];
        if (handle == 0 || bytes == 0)
        {
            return Result.Failure<byte[]>($"Buffer {buffer.Id} is not one the GPU made, or none of it was asked for.");
        }

        SDL.GPUTransferBuffer* transfer = CreateTransferBuffer(SDL.GPUTransferBufferUsage.Download, bytes);
        SDL.GPUCommandBuffer* commandBuffer = SDL.AcquireGPUCommandBuffer(_device);
        SDL.GPUCopyPass* copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        SDL.GPUBufferRegion source = new() { Buffer = (SDL.GPUBuffer*)handle, Size = bytes };
        SDL.GPUTransferBufferLocation destination = new() { TransferBuffer = transfer };
        SDL.DownloadFromGPUBuffer(copyPass, &source, &destination);
        SDL.EndGPUCopyPass(copyPass);
        return Download(commandBuffer, transfer, bytes);
    }

    public Result<byte[]> Read(GpuTexture texture)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[texture.Id];
        if (stored.Handle == 0)
        {
            return Result.Failure<byte[]>($"Texture {texture.Id} is not one the GPU made, or is a window's, "
                + "whose image cannot be read: draw into a texture of your own and read that.");
        }

        uint bytes = SDL.CalculateGPUTextureFormatSize(stored.Format.ToSdl(), stored.Width, stored.Height, 1);
        SDL.GPUTransferBuffer* transfer = CreateTransferBuffer(SDL.GPUTransferBufferUsage.Download, bytes);
        SDL.GPUCommandBuffer* commandBuffer = SDL.AcquireGPUCommandBuffer(_device);
        DownloadTexture(commandBuffer, stored.Handle, stored.Width, stored.Height, transfer);
        return Download(commandBuffer, transfer, bytes);
    }

    public Task<Result<GpuImage>> CaptureAsync(GpuTexture window)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[window.Id];
        if (stored.Window is not { Native: not 0 })
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

    private static InvalidOperationException MakeFailure(string what)
        => new($"The GPU could not make {what}: {CString.Read(SDL.GetError())}");

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
    /// Records, in a copy pass of its own, the download of <paramref name="texture"/>'s first level,
    /// <paramref name="width"/> x <paramref name="height"/>, into <paramref name="transfer"/>: the bytes are there once
    /// the command buffer is done.
    /// </summary>
    private static void DownloadTexture(
        SDL.GPUCommandBuffer* commandBuffer,
        nint texture,
        uint width,
        uint height,
        SDL.GPUTransferBuffer* transfer)
    {
        SDL.GPUCopyPass* copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        SDL.GPUTextureRegion source = new() { Texture = (SDL.GPUTexture*)texture, W = width, H = height, D = 1 };
        SDL.GPUTextureTransferInfo destination = new()
        {
            TransferBuffer = transfer,
            PixelsPerRow = width,
            RowsPerLayer = height
        };
        SDL.DownloadFromGPUTexture(copyPass, &source, &destination);
        SDL.EndGPUCopyPass(copyPass);
    }

    /// <summary>
    /// The window's image this frame, and its size; null when it has none (minimised).
    /// </summary>
    private static SDL.GPUTexture* Acquire(
        SDL.GPUCommandBuffer* commandBuffer,
        WindowObject window,
        uint* width,
        uint* height)
    {
        SDL.GPUTexture* image = null;
        long start = Stopwatch.GetTimestamp();
        bool isAcquired = SDL.WaitAndAcquireGPUSwapchainTexture(
            commandBuffer,
            (SDL.Window*)window.Native,
            &image,
            width,
            height);
        _acquireWait.Record(Stopwatch.GetElapsedTime(start).TotalSeconds);
        if (!isAcquired)
        {
            Debugging.Warn($"Window {window.Id}'s image was not acquired: {CString.Read(SDL.GetError())}");
            return null;
        }

        return image;
    }

    /// <summary>
    /// SDL ties a window and the command buffers that draw into it to the thread that made them: the render thread.
    /// </summary>
    private void ThrowIfNotRender()
    {
        if (_threads.IsCurrent(EngineThread.Render)) return;

        throw new InvalidOperationException("The GPU is the render thread's: use it in Render, "
            + "or hand the work to the render thread (Threads.InvokeAsync).");
    }

    /// <summary>
    /// Runs the queued uploads, then the frame's commands, then presents the window if they drew into it, and clears
    /// the list. What was released since the last submit goes once this frame is done with it.
    /// </summary>
    private void Submit()
    {
        if (_commands.Count == 0 && _transfers.Count == 0)
        {
            FreeReleased();
            return;
        }

        int slot = (int)(_frameNumber % FramesInFlight);
        ReleaseFrame(slot);
        SDL.GPUCommandBuffer* commandBuffer = SDL.AcquireGPUCommandBuffer(_device);
        UnmapStaging();
        if (commandBuffer == null)
        {
            Debugging.Error($"The frame was not drawn: SDL had no command buffer: {CString.Read(SDL.GetError())}");
            _transfers.Clear();
            _images.Clear();
            _commands.Clear();
            FreeReleased();
            return;
        }

        RunTransfers(commandBuffer);
        foreach (ref readonly GpuCommand command in _commands.Commands)
        {
            Run(commandBuffer, command);
        }

        CopyCaptures(commandBuffer);
        _fences[slot] = (nint)SDL.SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
        if (_fences[slot] == 0) Debugging.Error($"SDL did not submit the frame: {CString.Read(SDL.GetError())}");

        FinishCaptures((SDL.GPUFence*)_fences[slot]);

        _frameNumber++;
        _images.Clear();
        _commands.Clear();
        FreeReleased();
    }

    /// <summary>
    /// Copies each capture begun this frame into its window's image, which the frame then shows, and downloads it.
    /// After every command, so outside any pass.
    /// </summary>
    private void CopyCaptures(SDL.GPUCommandBuffer* commandBuffer)
    {
        for (int i = 0; i < _capturing.Count; i++)
        {
            Capture capture = _capturing[i];
            SDL.GPUBlitInfo blit = new()
            {
                Source = new SDL.GPUBlitRegion
                {
                    Texture = (SDL.GPUTexture*)capture.Copy,
                    W = capture.Width,
                    H = capture.Height
                },
                Destination = new SDL.GPUBlitRegion
                {
                    Texture = (SDL.GPUTexture*)capture.Image,
                    W = capture.Width,
                    H = capture.Height
                },
                LoadOp = SDL.GPULoadOp.DontCare,
                Filter = SDL.GPUFilter.Nearest,
            };
            SDL.BlitGPUTexture(commandBuffer, &blit);
            SDL.GPUTransferBuffer* transfer = CreateTransferBuffer(
                SDL.GPUTransferBufferUsage.Download,
                capture.Width * capture.Height * 4);
            DownloadTexture(commandBuffer, capture.Copy, capture.Width, capture.Height, transfer);
            _capturing[i] = capture with { Transfer = (nint)transfer };
        }
    }

    /// <summary>
    /// Waits for the frame the captures were in, then hands each its pixels and lets its texture and download go.
    /// </summary>
    private void FinishCaptures(SDL.GPUFence* fence)
    {
        if (_capturing.Count == 0) return;

        bool isDone = fence != null && SDL.WaitForGPUFences(_device, waitAll: true, &fence, 1);
        foreach (Capture capture in _capturing)
        {
            capture.Done.SetResult(isDone
                ? TryReadPixels(capture)
                : Result.Failure<GpuImage>($"The frame it was in never finished: {CString.Read(SDL.GetError())}"));
            SDL.ReleaseGPUTransferBuffer(_device, (SDL.GPUTransferBuffer*)capture.Transfer);
            SDL.ReleaseGPUTexture(_device, (SDL.GPUTexture*)capture.Copy);
        }

        _capturing.Clear();
    }

    /// <summary>
    /// A capture's downloaded pixels as RGBA, opaque: a window shows no alpha, whatever was written to it.
    /// </summary>
    private Result<GpuImage> TryReadPixels(in Capture capture)
    {
        byte* mapped = (byte*)SDL.MapGPUTransferBuffer(_device, (SDL.GPUTransferBuffer*)capture.Transfer, cycle: false);
        if (mapped == null)
        {
            return Result.Failure<GpuImage>($"The capture never arrived: {CString.Read(SDL.GetError())}");
        }

        byte[] pixels = new ReadOnlySpan<byte>(mapped, (int)(capture.Width * capture.Height * 4)).ToArray();
        SDL.UnmapGPUTransferBuffer(_device, (SDL.GPUTransferBuffer*)capture.Transfer);
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

        return Result.Success(new GpuImage((int)capture.Width, (int)capture.Height, pixels));
    }

    /// <summary>
    /// Waits for the frame last submitted from <paramref name="slot"/>, then releases what was freed during it.
    /// </summary>
    private void ReleaseFrame(int slot)
    {
        SDL.GPUFence* fence = (SDL.GPUFence*)_fences[slot];
        if (fence != null)
        {
            if (!SDL.WaitForGPUFences(_device, waitAll: true, &fence, 1))
            {
                Debugging.Error($"Waiting for a frame failed: {CString.Read(SDL.GetError())}");
            }

            SDL.ReleaseGPUFence(_device, fence);
            _fences[slot] = 0;
        }

        foreach ((Kind kind, nint handle) in _freed[slot])
        {
            Release(kind, handle);
        }

        _freed[slot].Clear();
    }

    /// <summary>
    /// What callers released leaves the tables, and outgrown staging goes too; their SDL objects go once the last frame
    /// submitted is done, since that and any frame before it may still use them.
    /// </summary>
    private void FreeReleased()
    {
        List<(Kind Kind, nint Handle)> freed = _freed[(int)((_frameNumber + FramesInFlight - 1) % FramesInFlight)];
        foreach (nint staging in _outgrown)
        {
            freed.Add((Kind.TransferBuffer, staging));
        }

        _outgrown.Clear();
        foreach ((Kind kind, uint id) in _releasing)
        {
            nint handle = kind switch
            {
                Kind.Buffer => _buffers.Free(id),
                Kind.Texture => _textures.Free(id).Handle,
                Kind.Window => _textures.Free(id).Window?.Native ?? 0,
                Kind.Sampler => _samplers.Free(id),
                Kind.GraphicsPipeline or Kind.ComputePipeline => _pipelines.Free(id).Handle,
                Kind.TransferBuffer => 0,
                _ => 0,
            };
            if (handle != 0) freed.Add((kind, handle));
        }

        _releasing.Clear();
    }

    private void Release(Kind kind, nint handle)
    {
        switch (kind)
        {
            case Kind.Buffer:
                SDL.ReleaseGPUBuffer(_device, (SDL.GPUBuffer*)handle);
                break;
            case Kind.Texture:
                SDL.ReleaseGPUTexture(_device, (SDL.GPUTexture*)handle);
                break;
            case Kind.Sampler:
                SDL.ReleaseGPUSampler(_device, (SDL.GPUSampler*)handle);
                break;
            case Kind.GraphicsPipeline:
                SDL.ReleaseGPUGraphicsPipeline(_device, (SDL.GPUGraphicsPipeline*)handle);
                break;
            case Kind.ComputePipeline:
                SDL.ReleaseGPUComputePipeline(_device, (SDL.GPUComputePipeline*)handle);
                break;
            case Kind.TransferBuffer:
                SDL.ReleaseGPUTransferBuffer(_device, (SDL.GPUTransferBuffer*)handle);
                break;
            case Kind.Window:
                SDL.ReleaseWindowFromGPUDevice(_device, (SDL.Window*)handle);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such kind of GPU object.");
        }
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
        foreach ((uint id, GpuTexture target) in _windows)
        {
            SDL.Window* window = id == 0 ? null : SDL.GetWindowFromID(id);
            if (window == null) continue;

            _textures[target.Id] = _textures[target.Id] with { Format = ApplySwapchain(window, id) };
        }
    }

    /// <summary>
    /// Claims <paramref name="window"/>, number <paramref name="id"/>, for the GPU, presenting as the settings ask, and
    /// returns its images' format. A window given back a moment ago is still the GPU's until the frames in flight are
    /// done with it, so a refused claim waits for them and tries once more.
    /// </summary>
    private GpuFormat Claim(SDL.Window* window, uint id)
    {
        if (!SDL.ClaimWindowForGPUDevice(_device, window))
        {
            for (int slot = 0; slot < FramesInFlight; slot++)
            {
                ReleaseFrame(slot);
            }

            if (!SDL.ClaimWindowForGPUDevice(_device, window))
            {
                throw new InvalidOperationException(
                    $"The GPU cannot draw into window {id}: {CString.Read(SDL.GetError())}");
            }
        }

        return ApplySwapchain(window, id);
    }

    /// <summary>
    /// Presents <paramref name="window"/>, number <paramref name="id"/>, as the settings ask, composed and timed so,
    /// and returns its images' format; when SDL refuses, it presents as SDL chose, which is warned about.
    /// </summary>
    private GpuFormat ApplySwapchain(SDL.Window* window, uint id)
    {
        SDL.GPUPresentMode present = GetPresentMode(window);
        SDL.GPUSwapchainComposition composition = GetComposition(window, id);
        if (!SDL.SetGPUSwapchainParameters(_device, window, composition, present))
        {
            Debugging.Warn(
                $"Window {id} presents as SDL chose, not {composition} with {present}: {CString.Read(SDL.GetError())}");
        }

        GpuFormat format = SDL.GetGPUSwapchainTextureFormat(_device, window).ToMagic();
        Debugging.Verbose($"Window {id} presents {format}, composed {composition}, with {present}.");
        return format;
    }

    /// <summary>
    /// The composition the settings ask for, where the window supports it; SDR, which every window does, otherwise,
    /// and a warning says so.
    /// </summary>
    private SDL.GPUSwapchainComposition GetComposition(SDL.Window* window, uint id)
    {
        SDL.GPUSwapchainComposition asked = _settings.Composition.ToSdl();
        if (asked == SDL.GPUSwapchainComposition.Sdr
            || SDL.WindowSupportsGPUSwapchainComposition(_device, window, asked))
        {
            return asked;
        }

        Debugging.Warn($"Window {id} does not support the {_settings.Composition} composition: it is composed SDR.");
        return SDL.GPUSwapchainComposition.Sdr;
    }

    /// <summary>
    /// Vsync, or when the settings turn it off, uncapped: immediate (tearing allowed) where the window has it, else
    /// mailbox.
    /// </summary>
    private SDL.GPUPresentMode GetPresentMode(SDL.Window* window)
    {
        if (_settings.Vsync) return SDL.GPUPresentMode.Vsync;

        if (SDL.WindowSupportsGPUPresentMode(_device, window, SDL.GPUPresentMode.Immediate))
        {
            return SDL.GPUPresentMode.Immediate;
        }

        return SDL.WindowSupportsGPUPresentMode(_device, window, SDL.GPUPresentMode.Mailbox)
            ? SDL.GPUPresentMode.Mailbox
            : SDL.GPUPresentMode.Vsync;
    }

    /// <summary>
    /// A vertex or fragment shader object, kept only while a pipeline is made of it; null when SDL refuses it.
    /// </summary>
    private SDL.GPUShader* CreateShader(GpuShader shader)
    {
        byte[] entryPoint = CString.ToBytes(shader.EntryPoint);
        fixed (byte* code = shader.Code.Span)
        fixed (byte* entry = entryPoint)
        {
            SDL.GPUShaderCreateInfo info = new()
            {
                Code = code,
                CodeSize = (nuint)shader.Code.Length,
                Entrypoint = entry,
                Format = ShaderFormat.ToSdl(),
                Stage = shader.Stage == GpuStage.Vertex ? SDL.GPUShaderStage.Vertex : SDL.GPUShaderStage.Fragment,
                NumSamplers = shader.Samplers,
                NumStorageTextures = shader.StorageTextures,
                NumStorageBuffers = shader.StorageBuffers,
                NumUniformBuffers = shader.UniformBuffers,
            };
            return SDL.CreateGPUShader(_device, &info);
        }
    }

    private SDL.GPUTransferBuffer* CreateTransferBuffer(SDL.GPUTransferBufferUsage usage, uint bytes)
    {
        SDL.GPUTransferBufferCreateInfo info = new() { Usage = usage, Size = bytes };
        SDL.GPUTransferBuffer* transfer = SDL.CreateGPUTransferBuffer(_device, &info);
        return transfer == null ? throw MakeFailure($"a {bytes}-byte transfer buffer") : transfer;
    }

    /// <summary>
    /// Submits a command buffer of downloads into <paramref name="transfer"/>, waits for it, and copies the bytes out.
    /// </summary>
    private Result<byte[]> Download(SDL.GPUCommandBuffer* commandBuffer, SDL.GPUTransferBuffer* transfer, uint bytes)
    {
        try
        {
            SDL.GPUFence* fence = SDL.SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
            if (fence == null)
            {
                return Result.Failure<byte[]>($"SDL did not submit the read: {CString.Read(SDL.GetError())}");
            }

            bool isDone = SDL.WaitForGPUFences(_device, waitAll: true, &fence, 1);
            SDL.ReleaseGPUFence(_device, fence);
            byte* mapped = isDone ? (byte*)SDL.MapGPUTransferBuffer(_device, transfer, cycle: false) : null;
            if (mapped == null)
            {
                return Result.Failure<byte[]>($"The read never arrived: {CString.Read(SDL.GetError())}");
            }

            byte[] read = new ReadOnlySpan<byte>(mapped, (int)bytes).ToArray();
            SDL.UnmapGPUTransferBuffer(_device, transfer);
            return Result.Success(read);
        }
        finally
        {
            SDL.ReleaseGPUTransferBuffer(_device, transfer);
        }
    }

    /// <summary>
    /// Copies <paramref name="data"/> into staging, 16-byte aligned; returns the transfer buffer and offset the next
    /// copy pass reads it from.
    /// </summary>
    private (nint Buffer, uint Offset) Stage(ReadOnlySpan<byte> data)
    {
        uint bytes = (uint)data.Length;
        _staged.Add(bytes);
        uint offset = (_stagingUsed + 15u) & ~15u;
        if (_stagingMapped != null && offset + bytes > _stagingCapacity)
        {
            UnmapStaging();
            GrowStaging(offset + bytes);
        }

        if (_stagingMapped == null)
        {
            if (bytes > _stagingCapacity) GrowStaging(bytes);

            _stagingMapped = (byte*)SDL.MapGPUTransferBuffer(_device, _staging, cycle: true);
            if (_stagingMapped == null)
            {
                throw new InvalidOperationException(
                    $"SDL would not map staging memory: {CString.Read(SDL.GetError())}");
            }

            offset = 0;
        }

        data.CopyTo(new Span<byte>(_stagingMapped + offset, data.Length));
        _stagingUsed = offset + bytes;
        return ((nint)_staging, offset);
    }

    /// <summary>
    /// A bigger staging buffer, for the rest of this frame and the frames after; what was staged in the old one stays
    /// there until the frame that copies it is done.
    /// </summary>
    private void GrowStaging(uint needed)
    {
        _outgrown.Add((nint)_staging);
        _stagingCapacity = BitOperations.RoundUpToPowerOf2(Math.Max(needed, _stagingCapacity * 2));
        _staging = CreateTransferBuffer(SDL.GPUTransferBufferUsage.Upload, _stagingCapacity);
        Debugging.Verbose($"GPU staging grew to {_stagingCapacity / 1024} KiB.");
    }

    private void UnmapStaging()
    {
        if (_stagingMapped == null) return;

        SDL.UnmapGPUTransferBuffer(_device, _staging);
        _stagingMapped = null;
        _stagingUsed = 0;
    }

    /// <summary>
    /// Every upload queued since the last submit, in order, in one copy pass.
    /// </summary>
    private void RunTransfers(SDL.GPUCommandBuffer* commandBuffer)
    {
        if (_transfers.Count == 0) return;

        SDL.GPUCopyPass* copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        foreach (Transfer transfer in _transfers)
        {
            if (transfer.Region.Texture == GpuTexture.None)
            {
                SDL.GPUTransferBufferLocation source = new()
                {
                    TransferBuffer = (SDL.GPUTransferBuffer*)transfer.Staged,
                    Offset = transfer.StagedOffset
                };
                SDL.GPUBufferRegion destination = new()
                {
                    Buffer = (SDL.GPUBuffer*)_buffers[transfer.Buffer.Id],
                    Offset = transfer.BufferOffset,
                    Size = transfer.Bytes
                };
                SDL.UploadToGPUBuffer(copyPass, &source, &destination, cycle: false);
                continue;
            }

            SDL.GPUTextureRegion region = ToTextureRegion(transfer.Region);
            SDL.GPUTextureTransferInfo texels = new()
            {
                TransferBuffer = (SDL.GPUTransferBuffer*)transfer.Staged,
                Offset = transfer.StagedOffset,
                PixelsPerRow = region.W,
                RowsPerLayer = region.H
            };
            SDL.UploadToGPUTexture(copyPass, &texels, &region, cycle: false);
        }

        SDL.EndGPUCopyPass(copyPass);
        _transfers.Clear();
    }

    /// <summary>
    /// A region with its size filled in: a width, height or depth of 0 is the whole level.
    /// </summary>
    private SDL.GPUTextureRegion ToTextureRegion(in GpuTextureRegion region)
    {
        TextureObject stored = _textures[region.Texture.Id];
        int level = (int)region.Level;
        return new SDL.GPUTextureRegion
        {
            Texture = (SDL.GPUTexture*)stored.Handle,
            MipLevel = region.Level,
            Layer = region.Layer,
            X = region.X,
            Y = region.Y,
            Z = region.Z,
            W = region.Width != 0 ? region.Width : Math.Max(1, stored.Width >> level),
            H = region.Height != 0 ? region.Height : Math.Max(1, stored.Height >> level),
            D = region.Depth != 0 ? region.Depth : Math.Max(1, stored.Depth >> level),
        };
    }

    /// <summary>
    /// One recorded command, as its SDL call.
    /// </summary>
    private void Run(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        if (_isSkipping && command.Type != GpuCommandType.EndRenderPass) return;

        switch (command.Type)
        {
            case GpuCommandType.BeginRenderPass:
                BeginRenderPass(commandBuffer, command);
                break;
            case GpuCommandType.EndRenderPass:
                if (_renderPass != null) SDL.EndGPURenderPass(_renderPass);

                _renderPass = null;
                _isSkipping = false;
                break;
            case GpuCommandType.BeginComputePass:
                BeginComputePass(commandBuffer, command);
                break;
            case GpuCommandType.EndComputePass:
                SDL.EndGPUComputePass(_computePass);
                _computePass = null;
                break;
            case GpuCommandType.Viewport:
            {
                SDL.GPUViewport viewport = new()
                {
                    X = command.Area.X,
                    Y = command.Area.Y,
                    W = command.Area.Width,
                    H = command.Area.Height,
                    MaxDepth = 1
                };
                SDL.SetGPUViewport(_renderPass, &viewport);
                break;
            }

            case GpuCommandType.Scissor:
            {
                // Inside the target's corner: a scissor that starts before it is an error on some backends.
                int x = Math.Max(command.Area.X, 0);
                int y = Math.Max(command.Area.Y, 0);
                SDL.Rect scissor = new()
                {
                    X = x,
                    Y = y,
                    W = Math.Max(command.Area.Right - x, 0),
                    H = Math.Max(command.Area.Bottom - y, 0)
                };
                SDL.SetGPUScissor(_renderPass, &scissor);
                break;
            }

            case GpuCommandType.Pipeline:
                BindPipeline(command.Pipeline);
                break;
            case GpuCommandType.VertexBuffers:
                BindVertexBuffers(command);
                break;
            case GpuCommandType.IndexBuffer:
            {
                SDL.GPUBufferBinding index = new() { Buffer = (SDL.GPUBuffer*)_buffers[command.Buffer.Id] };
                SDL.BindGPUIndexBuffer(_renderPass, &index, command.IndexSize == GpuIndexSize.Bits32
                    ? SDL.GPUIndexElementSize.Size32bit
                    : SDL.GPUIndexElementSize.Size16bit);
                break;
            }

            case GpuCommandType.Buffers:
                BindBuffers(command);
                break;
            case GpuCommandType.RawTextures:
                BindRawTextures(command);
                break;
            case GpuCommandType.Textures:
                BindTextures(command);
                break;
            case GpuCommandType.Constants:
                PushConstants(commandBuffer, command);
                break;
            case GpuCommandType.Draw:
                SDL.DrawGPUPrimitives(
                    _renderPass,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexed:
                SDL.DrawGPUIndexedPrimitives(
                    _renderPass,
                    command.Count,
                    command.Instances,
                    command.First,
                    command.VertexOffset,
                    command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexedFromBuffer:
                SDL.DrawGPUIndexedPrimitivesIndirect(
                    _renderPass,
                    (SDL.GPUBuffer*)_buffers[command.Buffer.Id],
                    command.Offset,
                    command.Count);
                break;
            case GpuCommandType.Dispatch:
                SDL.DispatchGPUCompute(_computePass, command.GroupsX, command.GroupsY, command.GroupsZ);
                break;
            case GpuCommandType.DispatchFromBuffer:
                SDL.DispatchGPUComputeIndirect(
                    _computePass,
                    (SDL.GPUBuffer*)_buffers[command.Buffer.Id],
                    command.Offset);
                break;
            case GpuCommandType.CopyTexture:
                CopyTexture(commandBuffer, command);
                break;
            case GpuCommandType.CopyBuffer:
                CopyBuffer(commandBuffer, command);
                break;
            case GpuCommandType.GenerateMipmaps:
                SDL.GenerateMipmapsForGPUTexture(commandBuffer, (SDL.GPUTexture*)_textures[command.Texture.Id].Handle);
                break;
            case GpuCommandType.BeginDebugGroup:
                BeginDebugGroup(commandBuffer, command);
                break;
            case GpuCommandType.EndDebugGroup:
                SDL.PopGPUDebugGroup(commandBuffer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Type, "There is no such GPU command.");
        }
    }

    private void BeginRenderPass(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuTextureLevel> colors = _commands.GetLevels(command);
        SDL.GPUColorTargetInfo* targets = stackalloc SDL.GPUColorTargetInfo[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            SDL.GPUTexture* texture = Texture(commandBuffer, colors[i].Texture);
            if (texture == null)
            {
                _isSkipping = true;
                return;
            }

            targets[i] = new SDL.GPUColorTargetInfo
            {
                Texture = texture,
                MipLevel = colors[i].Level,
                LayerOrDepthPlane = colors[i].Layer,
                ClearColor = new SDL.FColor
                {
                    R = command.Clear.R,
                    G = command.Clear.G,
                    B = command.Clear.B,
                    A = command.Clear.A
                },
                LoadOp = command.Contents.ToSdl(),
                StoreOp = SDL.GPUStoreOp.Store,
            };
        }

        if (command.Depth.Texture == GpuTexture.None)
        {
            _renderPass = SDL.BeginGPURenderPass(commandBuffer, targets, (uint)colors.Length, null);
            return;
        }

        SDL.GPUDepthStencilTargetInfo depth = new()
        {
            Texture = Texture(commandBuffer, command.Depth.Texture),
            LoadOp = command.Contents.ToSdl(),
            StoreOp = SDL.GPUStoreOp.Store,
            StencilLoadOp = SDL.GPULoadOp.DontCare,
            StencilStoreOp = SDL.GPUStoreOp.DontCare,
            MipLevel = (byte)command.Depth.Level,
            Layer = (byte)command.Depth.Layer,
        };
        _renderPass = SDL.BeginGPURenderPass(commandBuffer, targets, (uint)colors.Length, &depth);
    }

    private void BeginComputePass(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuTextureLevel> levels = _commands.GetLevels(command);
        ReadOnlySpan<GpuBuffer> written = _commands.GetBuffers(command);
        SDL.GPUStorageTextureReadWriteBinding* textures =
            stackalloc SDL.GPUStorageTextureReadWriteBinding[levels.Length];
        SDL.GPUStorageBufferReadWriteBinding* buffers = stackalloc SDL.GPUStorageBufferReadWriteBinding[written.Length];
        for (int i = 0; i < levels.Length; i++)
        {
            textures[i] = new SDL.GPUStorageTextureReadWriteBinding
            {
                Texture = (SDL.GPUTexture*)_textures[levels[i].Texture.Id].Handle,
                MipLevel = levels[i].Level,
                Layer = levels[i].Layer
            };
        }

        for (int i = 0; i < written.Length; i++)
        {
            buffers[i] = new SDL.GPUStorageBufferReadWriteBinding { Buffer = (SDL.GPUBuffer*)_buffers[written[i].Id] };
        }

        _computePass = SDL.BeginGPUComputePass(
            commandBuffer,
            textures,
            (uint)levels.Length,
            buffers,
            (uint)written.Length);
    }

    private void BindPipeline(GpuPipeline pipeline)
    {
        PipelineObject stored = _pipelines[pipeline.Id];
        if (stored.Kind == Kind.ComputePipeline)
        {
            SDL.BindGPUComputePipeline(_computePass, (SDL.GPUComputePipeline*)stored.Handle);
            return;
        }

        SDL.BindGPUGraphicsPipeline(_renderPass, (SDL.GPUGraphicsPipeline*)stored.Handle);
    }

    private void BindVertexBuffers(in GpuCommand command)
    {
        ReadOnlySpan<GpuBuffer> run = _commands.GetBuffers(command);
        SDL.GPUBufferBinding* buffers = stackalloc SDL.GPUBufferBinding[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            buffers[i] = new SDL.GPUBufferBinding { Buffer = (SDL.GPUBuffer*)_buffers[run[i].Id] };
        }

        SDL.BindGPUVertexBuffers(_renderPass, 0, buffers, (uint)run.Length);
    }

    private void BindBuffers(in GpuCommand command)
    {
        ReadOnlySpan<GpuBuffer> run = _commands.GetBuffers(command);
        SDL.GPUBuffer** buffers = stackalloc SDL.GPUBuffer*[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            buffers[i] = (SDL.GPUBuffer*)_buffers[run[i].Id];
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL.BindGPUVertexStorageBuffers(_renderPass, 0, buffers, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL.BindGPUFragmentStorageBuffers(_renderPass, 0, buffers, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL.BindGPUComputeStorageBuffers(_computePass, 0, buffers, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void BindRawTextures(in GpuCommand command)
    {
        ReadOnlySpan<GpuTexture> run = _commands.GetTextures(command);
        SDL.GPUTexture** textures = stackalloc SDL.GPUTexture*[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            textures[i] = (SDL.GPUTexture*)_textures[run[i].Id].Handle;
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL.BindGPUVertexStorageTextures(_renderPass, 0, textures, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL.BindGPUFragmentStorageTextures(_renderPass, 0, textures, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL.BindGPUComputeStorageTextures(_computePass, 0, textures, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void BindTextures(in GpuCommand command)
    {
        ReadOnlySpan<GpuSampledTexture> run = _commands.GetSampled(command);
        SDL.GPUTextureSamplerBinding* textures = stackalloc SDL.GPUTextureSamplerBinding[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            textures[i] = new SDL.GPUTextureSamplerBinding
            {
                Texture = (SDL.GPUTexture*)_textures[run[i].Texture.Id].Handle,
                Sampler = (SDL.GPUSampler*)_samplers[run[i].Sampler.Id],
            };
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL.BindGPUVertexSamplers(_renderPass, 0, textures, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL.BindGPUFragmentSamplers(_renderPass, 0, textures, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL.BindGPUComputeSamplers(_computePass, 0, textures, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void PushConstants(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<byte> bytes = _commands.GetBytes(command);
        fixed (byte* data = bytes)
        {
            switch (command.Stage)
            {
                case GpuStage.Vertex:
                    SDL.PushGPUVertexUniformData(commandBuffer, 0, data, (uint)bytes.Length);
                    break;
                case GpuStage.Fragment:
                    SDL.PushGPUFragmentUniformData(commandBuffer, 0, data, (uint)bytes.Length);
                    break;
                case GpuStage.Compute:
                    SDL.PushGPUComputeUniformData(commandBuffer, 0, data, (uint)bytes.Length);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(command),
                        command.Stage,
                        "There is no such shader stage.");
            }
        }
    }

    /// <summary>
    /// The source region's size (the whole level where it says 0) from each region's corner.
    /// </summary>
    private void CopyTexture(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuTextureRegion> run = _commands.GetRegions(command);
        SDL.GPUTextureRegion from = ToTextureRegion(run[0]);
        SDL.GPUTextureLocation source = new()
        {
            Texture = from.Texture,
            MipLevel = from.MipLevel,
            Layer = from.Layer,
            X = from.X,
            Y = from.Y,
            Z = from.Z
        };
        SDL.GPUTextureLocation destination = new()
        {
            Texture = (SDL.GPUTexture*)_textures[run[1].Texture.Id].Handle,
            MipLevel = run[1].Level,
            Layer = run[1].Layer,
            X = run[1].X,
            Y = run[1].Y,
            Z = run[1].Z
        };
        SDL.GPUCopyPass* copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        SDL.CopyGPUTextureToTexture(copyPass, &source, &destination, from.W, from.H, from.D, cycle: false);
        SDL.EndGPUCopyPass(copyPass);
    }

    private void CopyBuffer(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBuffer> run = _commands.GetBuffers(command);
        SDL.GPUBufferLocation source = new() { Buffer = (SDL.GPUBuffer*)_buffers[run[0].Id], Offset = command.First };
        SDL.GPUBufferLocation destination = new()
        {
            Buffer = (SDL.GPUBuffer*)_buffers[run[1].Id],
            Offset = command.Offset
        };
        SDL.GPUCopyPass* copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        SDL.CopyGPUBufferToBuffer(copyPass, &source, &destination, command.Count, cycle: false);
        SDL.EndGPUCopyPass(copyPass);
    }

    private void BeginDebugGroup(SDL.GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<byte> name = _commands.GetBytes(command);
        byte* terminated = stackalloc byte[name.Length + 1];
        name.CopyTo(new Span<byte>(terminated, name.Length));
        terminated[name.Length] = 0;
        SDL.PushGPUDebugGroup(commandBuffer, terminated);
    }

    /// <summary>
    /// A texture's SDL object. A window's image is acquired the first time the frame draws into it, and is null when
    /// there is none this frame (minimised, or headless).
    /// </summary>
    private SDL.GPUTexture* Texture(SDL.GPUCommandBuffer* commandBuffer, GpuTexture texture)
    {
        TextureObject stored = _textures[texture.Id];
        if (stored.Window is not { } window) return (SDL.GPUTexture*)stored.Handle;

        if (_images.TryGetValue(texture.Id, out nint acquired)) return (SDL.GPUTexture*)acquired;

        uint width = 0;
        uint height = 0;
        SDL.GPUTexture* image = window.Native == 0 ? null : Acquire(commandBuffer, window, &width, &height);
        if (image != null && _captures.Remove(texture.Id, out TaskCompletionSource<Result<GpuImage>>? capture))
        {
            image = BeginCapture(image, stored.Format, width, height, capture);
        }

        _images[texture.Id] = (nint)image;
        return image;
    }

    /// <summary>
    /// A texture like the window's <paramref name="image"/>, which the frame draws into in its place, then copies into
    /// it and reads back (<see cref="CopyCaptures"/>); the image itself when no such texture can be made, the capture
    /// failing.
    /// </summary>
    private SDL.GPUTexture* BeginCapture(
        SDL.GPUTexture* image,
        GpuFormat format,
        uint width,
        uint height,
        TaskCompletionSource<Result<GpuImage>> done)
    {
        SDL.GPUTextureCreateInfo info = new()
        {
            Type = SDL.GPUTextureType.Type2d,
            Format = format.ToSdl(),
            Usage = SDL.GpuTextureusageColorTarget | SDL.GpuTextureusageSampler,
            Width = width,
            Height = height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.Count1,
        };
        SDL.GPUTexture* copy = SDL.CreateGPUTexture(_device, &info);
        if (copy == null)
        {
            done.SetResult(Result.Failure<GpuImage>($"The GPU could not make a {width} x {height} copy of the window's "
                + $"image: {CString.Read(SDL.GetError())}"));
            return image;
        }

        _capturing.Add(new Capture((nint)image, (nint)copy, width, height, format, 0, done));
        return copy;
    }

    /// <summary>
    /// What a kind of handle is, for releasing its SDL object.
    /// </summary>
    private enum Kind : byte
    {
        Buffer,
        Texture,
        Sampler,
        GraphicsPipeline,
        ComputePipeline,
        TransferBuffer,
        Window,
    }

    /// <summary>
    /// One queued upload: staged bytes into a buffer, or into a texture region when <see cref="Region"/> names one.
    /// </summary>
    private readonly record struct Transfer(
        nint Staged,
        uint StagedOffset,
        uint Bytes,
        GpuBuffer Buffer,
        uint BufferOffset,
        GpuTextureRegion Region);

    /// <summary>
    /// A texture, or a window's render target, which has no SDL texture of its own (no <see cref="Handle"/>): its
    /// texture is the image acquired each frame.
    /// </summary>
    private readonly record struct TextureObject(
        nint Handle,
        GpuFormat Format,
        uint Width,
        uint Height,
        uint Depth,
        WindowObject? Window = null);

    /// <summary>
    /// The window a render target draws into: SDL's window (none, headless) and the platform's id for it.
    /// </summary>
    private readonly record struct WindowObject(nint Native, uint Id);

    /// <summary>
    /// A capture begun this frame: the window's image, the texture drawn into in its place, their size and format,
    /// where it is downloaded to, and who waits for it.
    /// </summary>
    private readonly record struct Capture(
        nint Image,
        nint Copy,
        uint Width,
        uint Height,
        GpuFormat Format,
        nint Transfer,
        TaskCompletionSource<Result<GpuImage>> Done);

    /// <summary>
    /// A graphics or compute pipeline: <see cref="Kind"/> is which, as it is bound and released.
    /// </summary>
    private readonly record struct PipelineObject(nint Handle, Kind Kind);

    /// <summary>
    /// SDL objects by handle id: dense, id 0 never used, freed ids used again.
    /// </summary>
    private sealed class Table<T>
        where T : struct
    {
        private readonly List<T> _items = [default];
        private readonly List<bool> _isLive = [false];
        private readonly Stack<uint> _free = [];

        internal IEnumerable<T> Live
        {
            get
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_isLive[i]) yield return _items[i];
                }
            }
        }

        /// <summary>
        /// What <paramref name="id"/> holds; the default for an id that holds nothing.
        /// </summary>
        internal T this[uint id]
        {
            get => id < _items.Count && _isLive[(int)id] ? _items[(int)id] : default;
            set => _items[(int)id] = value;
        }

        internal uint Add(T item)
        {
            if (_free.TryPop(out uint id))
            {
                _items[(int)id] = item;
                _isLive[(int)id] = true;
                return id;
            }

            _items.Add(item);
            _isLive.Add(true);
            return (uint)(_items.Count - 1);
        }

        /// <summary>
        /// Forgets <paramref name="id"/>, handing back what it held.
        /// </summary>
        internal T Free(uint id)
        {
            if (id == 0 || id >= _items.Count || !_isLive[(int)id]) return default;

            T item = _items[(int)id];
            _items[(int)id] = default;
            _isLive[(int)id] = false;
            _free.Push(id);
            return item;
        }
    }
}
