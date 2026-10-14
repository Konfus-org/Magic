using System.Numerics;
using System.Text;
using Magic.Contexts.Assets;
using Magic.Contexts.Frames;
using Magic.Contexts.Gpu;
using Magic.Contexts.Threading;
using Magic.Contexts.Utils;
using Magic.Interfaces;
using Magic.Services;
using SDL;
using Size = System.Drawing.Size;

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

    private readonly SdlGpuSettings _settings;
    private readonly Threads _threads;
    private readonly SDL_GPUDevice* _device;
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
    private readonly List<(Kind Kind, nint Handle)>[] _freed = [[], []];

    /// <summary>
    /// Every window that is a render target, by its id, so asking again hands back the same texture.
    /// </summary>
    private readonly Dictionary<uint, GpuTexture> _windows = [];

    /// <summary>
    /// <see cref="DebugView"/>'s stage as a number, -1 for none: an int, so any thread reads and writes it whole.
    /// </summary>
    private int _debugView = -1;

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
    /// Where uploads are written until the next submit's copy pass moves them: mapped on the first upload of a frame,
    /// cycled so SDL hands out fresh memory while a frame in flight still reads the old, unmapped at submit. A frame
    /// that needs more swaps in one twice the size, which stays.
    /// </summary>
    private SDL_GPUTransferBuffer* _staging;
    private uint _stagingCapacity = InitialStagingBytes;
    private byte* _stagingMapped;
    private uint _stagingUsed;

    /// <summary>
    /// Counts submitted frames; a frame's slot is this modulo <see cref="FramesInFlight"/>.
    /// </summary>
    private ulong _frameNumber;

    private SDL_GPURenderPass* _renderPass;
    private SDL_GPUComputePass* _computePass;

    /// <summary>
    /// The render pass draws into a window with no image this frame: its commands are skipped to its end.
    /// </summary>
    private bool _isSkipping;

    public SdlGpuGem(SdlGpuSettings settings, Threads threads)
    {
        _settings = settings;
        _threads = threads;

        // The SDL this gem draws through is the platform gem's, which opens the windows and quits SDL as it ends:
        // made first, the platform is disposed last, once the GPU has let its windows go.
        if (!IsSdlStarted())
        {
            throw new InvalidOperationException("SdlGpu draws through the SDL a platform gem starts: list that gem before it in the project.");
        }

        // SDL opens a GPU only with its video subsystem up, which a headless platform leaves down. SDL counts each
        // subsystem's starts, so this one is undone in Dispose, before the platform quits SDL.
        if (!SDL3.SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            throw new InvalidOperationException($"SDL could not start its video subsystem for the GPU: {SDL3.SDL_GetError()}");
        }

        SDL_GPUVulkanOptions vulkan = new() { vulkan_api_version = Vulkan11 };
        SDL_PropertiesID properties = SDL3.SDL_CreateProperties();
        SDL3.SDL_SetBooleanProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_SHADERS_SPIRV_BOOLEAN, value: true);
        SDL3.SDL_SetBooleanProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_SHADERS_DXIL_BOOLEAN, value: true);
        SDL3.SDL_SetBooleanProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_SHADERS_MSL_BOOLEAN, value: true);
        SDL3.SDL_SetBooleanProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_DEBUGMODE_BOOLEAN, settings.Validation);
        SDL3.SDL_SetBooleanProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_PREFERLOWPOWER_BOOLEAN, settings.LowPower);
        SDL3.SDL_SetPointerProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_VULKAN_OPTIONS_POINTER, (nint)(&vulkan));
        if (settings.Driver.Length > 0)
        {
            SDL3.SDL_SetStringProperty(properties, SDL3.SDL_PROP_GPU_DEVICE_CREATE_NAME_STRING, settings.Driver);
        }

        _device = SDL3.SDL_CreateGPUDeviceWithProperties(properties);
        SDL3.SDL_DestroyProperties(properties);
        if (_device == null)
        {
            string? error = SDL3.SDL_GetError();
            SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO);
            throw new InvalidOperationException($"SDL could not open a GPU device: {error}");
        }

        SDL_GPUShaderFormat formats = SDL3.SDL_GetGPUShaderFormats(_device);
        ShaderFormat = formats.HasFlag(SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV) ? GpuShaderFormat.Spirv
            : formats.HasFlag(SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL) ? GpuShaderFormat.Dxil
            : GpuShaderFormat.Msl;
        string name = SDL3.SDL_GetStringProperty(SDL3.SDL_GetGPUDeviceProperties(_device), SDL3.SDL_PROP_GPU_DEVICE_NAME_STRING, "an unnamed GPU") ?? "an unnamed GPU";
        Device = $"{name} ({SDL3.SDL_GetGPUDeviceDriver(_device)})";
        SDL3.SDL_SetGPUAllowedFramesInFlight(_device, FramesInFlight);
        _staging = CreateTransferBuffer(SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, _stagingCapacity);
        Debugging.Info($"GPU: {Device}, shaders {ShaderFormat}, validation {settings.Validation}");
    }

    public void Dispose()
    {
        foreach (TaskCompletionSource<Result<GpuImage>> capture in _captures.Values)
        {
            capture.TrySetResult(Result.Failure<GpuImage>("The GPU stopped before the window was drawn again."));
        }

        SDL3.SDL_WaitForGPUIdle(_device);
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
                if (window.Native != 0)
                {
                    Release(Kind.Window, window.Native);
                }

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
            Release(pipeline.IsCompute ? Kind.ComputePipeline : Kind.GraphicsPipeline, pipeline.Handle);
        }

        foreach (nint staging in _outgrown)
        {
            Release(Kind.TransferBuffer, staging);
        }

        UnmapStaging();
        SDL3.SDL_ReleaseGPUTransferBuffer(_device, _staging);
        SDL3.SDL_DestroyGPUDevice(_device);
        SDL3.SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO);
    }

    public string Device { get; }

    public GpuShaderFormat ShaderFormat { get; }

    public GpuCommands Commands
    {
        get
        {
            ThrowIfNotRender();
            return _commands;
        }
    }

    /// <summary>
    /// Kept for whatever draws the frame, which reads it; the GPU itself draws nothing differently.
    /// </summary>
    public PassStage? DebugView
    {
        get
        {
            int stage = Volatile.Read(ref _debugView);
            return stage < 0 ? null : (PassStage)stage;
        }

        set => Volatile.Write(ref _debugView, value is { } stage ? (int)stage : -1);
    }

    /// <summary>
    /// Render runs from the last gem made to the first, so every gem that takes this one has recorded by now.
    /// </summary>
    public void Render(in Frame frame)
    {
        Submit();
    }

    public GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes)
    {
        ThrowIfNotRender();

        SDL_GPUBufferCreateInfo info = new() { usage = usage.ToSdl(), size = bytes };
        SDL_GPUBuffer* buffer = SDL3.SDL_CreateGPUBuffer(_device, &info);
        return buffer == null ? throw Failed($"a {bytes}-byte buffer") : new GpuBuffer(_buffers.Add((nint)buffer));
    }

    public GpuTexture CreateTexture(in GpuTextureDesc desc)
    {
        ThrowIfNotRender();

        SDL_GPUTextureCreateInfo info = new()
        {
            type = desc.Kind.ToSdl(),
            format = desc.Format.ToSdl(),
            usage = desc.Usage.ToSdl(),
            width = desc.Width,
            height = desc.Height,
            layer_count_or_depth = desc.Kind == GpuTextureKind.Cube ? 6 : desc.Layers,
            num_levels = desc.Levels,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        SDL_GPUTexture* texture = SDL3.SDL_CreateGPUTexture(_device, &info);
        if (texture == null)
        {
            throw Failed($"a {desc.Width} x {desc.Height} {desc.Format} texture");
        }

        uint depth = desc.Kind == GpuTextureKind.Texture3D ? desc.Layers : 1;
        return new GpuTexture(_textures.Add(new TextureObject((nint)texture, desc.Format, desc.Width, desc.Height, depth)));
    }

    public GpuTexture Target(IWindow window)
    {
        ThrowIfNotRender();

        if (_windows.TryGetValue(window.Id, out GpuTexture target))
        {
            return target;
        }

        // A headless window is none of SDL's: a target with no image, ever.
        SDL_Window* native = window.Id == 0 ? null : SDL3.SDL_GetWindowFromID((SDL_WindowID)window.Id);
        if (window.Id != 0 && native == null)
        {
            throw new InvalidOperationException($"SDL knows no window {window.Id}: it must be the platform gem's, in the SDL this gem shares.");
        }

        GpuFormat format = native == null ? GpuFormat.Invalid : Claim(native);
        target = new GpuTexture(_textures.Add(new TextureObject(0, format, 0, 0, 1, new WindowObject((nint)native, window.Id))));
        _windows[window.Id] = target;
        return target;
    }

    public GpuFormat Format(GpuTexture texture)
    {
        ThrowIfNotRender();

        return _textures[texture.Id].Format;
    }

    public Size Size(GpuTexture texture)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[texture.Id];
        if (stored.Window is not { } window)
        {
            return new Size((int)stored.Width, (int)stored.Height);
        }

        SDL_Window* native = (SDL_Window*)window.Native;
        if (native == null || SDL3.SDL_GetWindowFlags(native).HasFlag(SDL_WindowFlags.SDL_WINDOW_MINIMIZED))
        {
            return System.Drawing.Size.Empty;
        }

        int width;
        int height;
        return SDL3.SDL_GetWindowSizeInPixels(native, &width, &height) ? new Size(width, height) : System.Drawing.Size.Empty;
    }

    public GpuSampler CreateSampler(in GpuSamplerDesc desc)
    {
        ThrowIfNotRender();

        SDL_GPUSamplerCreateInfo info = new()
        {
            min_filter = desc.Filter.ToSdl(),
            mag_filter = desc.Filter.ToSdl(),
            mipmap_mode = desc.Filter.ToSdlMipmaps(),
            address_mode_u = desc.Address.ToSdl(),
            address_mode_v = desc.Address.ToSdl(),
            address_mode_w = desc.Address.ToSdl(),
            max_anisotropy = desc.Anisotropy,
            enable_anisotropy = desc.Anisotropy > 1,
            compare_op = (desc.Compare ?? GpuCompare.Never).ToSdl(),
            enable_compare = desc.Compare is not null,
            max_lod = 1000,
        };
        SDL_GPUSampler* sampler = SDL3.SDL_CreateGPUSampler(_device, &info);
        return sampler == null ? throw Failed("a sampler") : new GpuSampler(_samplers.Add((nint)sampler));
    }

    public Result<GpuPipeline> CreatePipeline(GpuPipelineDesc desc)
    {
        ThrowIfNotRender();

        SDL_GPUShader* vertex = CreateShader(desc.Vertex);
        if (vertex == null)
        {
            return Result.Failure<GpuPipeline>($"The GPU would not make the vertex shader: {SDL3.SDL_GetError()}");
        }

        SDL_GPUShader* fragment = CreateShader(desc.Fragment);
        if (fragment == null)
        {
            SDL3.SDL_ReleaseGPUShader(_device, vertex);
            return Result.Failure<GpuPipeline>($"The GPU would not make the fragment shader: {SDL3.SDL_GetError()}");
        }

        SDL_GPUVertexBufferDescription* buffers = stackalloc SDL_GPUVertexBufferDescription[desc.Buffers.Count];
        for (int i = 0; i < desc.Buffers.Count; i++)
        {
            GpuVertexBuffer buffer = desc.Buffers[i];
            buffers[i] = new SDL_GPUVertexBufferDescription
            {
                slot = buffer.Slot,
                pitch = buffer.Pitch,
                input_rate = buffer.PerInstance ? SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_INSTANCE : SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX,
            };
        }

        SDL_GPUVertexAttribute* inputs = stackalloc SDL_GPUVertexAttribute[desc.Inputs.Count];
        for (int i = 0; i < desc.Inputs.Count; i++)
        {
            GpuVertexInput input = desc.Inputs[i];
            inputs[i] = new SDL_GPUVertexAttribute { location = input.Location, buffer_slot = input.Slot, format = input.Format.ToSdl(), offset = input.Offset };
        }

        SDL_GPUColorTargetDescription* colors = stackalloc SDL_GPUColorTargetDescription[desc.Colors.Count];
        for (int i = 0; i < desc.Colors.Count; i++)
        {
            GpuBlend blend = i < desc.Blends.Count ? desc.Blends[i] : GpuBlend.None;
            colors[i] = new SDL_GPUColorTargetDescription { format = desc.Colors[i].ToSdl(), blend_state = blend.ToSdl() };
        }

        bool hasDepth = desc.Depth != GpuFormat.Invalid;
        SDL_GPUGraphicsPipelineCreateInfo info = new()
        {
            vertex_shader = vertex,
            fragment_shader = fragment,
            vertex_input_state = new SDL_GPUVertexInputState
            {
                vertex_buffer_descriptions = buffers,
                num_vertex_buffers = (uint)desc.Buffers.Count,
                vertex_attributes = inputs,
                num_vertex_attributes = (uint)desc.Inputs.Count,
            },
            primitive_type = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
            rasterizer_state = new SDL_GPURasterizerState
            {
                fill_mode = SDL_GPUFillMode.SDL_GPU_FILLMODE_FILL,
                cull_mode = desc.Cull.ToSdl(),
                front_face = SDL_GPUFrontFace.SDL_GPU_FRONTFACE_CLOCKWISE,
                depth_bias_constant_factor = desc.DepthBias,
                depth_bias_slope_factor = desc.DepthBiasSlope,
                depth_bias_clamp = desc.DepthBiasClamp,
                enable_depth_bias = desc.DepthBias != 0 || desc.DepthBiasSlope != 0,
                enable_depth_clip = desc.DepthClip,
            },
            multisample_state = new SDL_GPUMultisampleState { sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1 },
            depth_stencil_state = new SDL_GPUDepthStencilState
            {
                compare_op = desc.DepthCompare.ToSdl(),
                enable_depth_test = hasDepth,
                enable_depth_write = hasDepth && desc.DepthWrite,
            },
            target_info = new SDL_GPUGraphicsPipelineTargetInfo
            {
                color_target_descriptions = colors,
                num_color_targets = (uint)desc.Colors.Count,
                depth_stencil_format = desc.Depth.ToSdl(),
                has_depth_stencil_target = hasDepth,
            },
        };
        SDL_GPUGraphicsPipeline* pipeline = SDL3.SDL_CreateGPUGraphicsPipeline(_device, &info);
        string error = pipeline == null ? SDL3.SDL_GetError() ?? string.Empty : string.Empty;
        SDL3.SDL_ReleaseGPUShader(_device, vertex);
        SDL3.SDL_ReleaseGPUShader(_device, fragment);
        return pipeline == null
            ? Result.Failure<GpuPipeline>($"The GPU would not make the pipeline: {error}")
            : Result.Success(new GpuPipeline(_pipelines.Add(new PipelineObject((nint)pipeline, IsCompute: false))));
    }

    public Result<GpuPipeline> CreatePipeline(GpuShader compute)
    {
        ThrowIfNotRender();

        byte[] entryPoint = Encoding.UTF8.GetBytes(compute.EntryPoint + '\0');
        fixed (byte* code = compute.Code.Span)
        fixed (byte* entry = entryPoint)
        {
            SDL_GPUComputePipelineCreateInfo info = new()
            {
                code = code,
                code_size = (nuint)compute.Code.Length,
                entrypoint = entry,
                format = ShaderFormat.ToSdl(),
                num_samplers = compute.Samplers,
                num_readonly_storage_textures = compute.StorageTextures,
                num_readonly_storage_buffers = compute.StorageBuffers,
                num_readwrite_storage_textures = compute.ReadWriteStorageTextures,
                num_readwrite_storage_buffers = compute.ReadWriteStorageBuffers,
                num_uniform_buffers = compute.UniformBuffers,
                threadcount_x = compute.ThreadCountX,
                threadcount_y = compute.ThreadCountY,
                threadcount_z = compute.ThreadCountZ,
            };
            SDL_GPUComputePipeline* pipeline = SDL3.SDL_CreateGPUComputePipeline(_device, &info);
            return pipeline == null
                ? Result.Failure<GpuPipeline>($"The GPU would not make the compute pipeline: {SDL3.SDL_GetError()}")
                : Result.Success(new GpuPipeline(_pipelines.Add(new PipelineObject((nint)pipeline, IsCompute: true))));
        }
    }

    public void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty)
        {
            return;
        }

        (nint staged, uint stagedOffset) = Stage(data);
        _transfers.Add(new Transfer(staged, stagedOffset, (uint)data.Length, buffer, offset, default));
    }

    public void Upload(in GpuTextureRegion region, ReadOnlySpan<byte> data)
    {
        ThrowIfNotRender();

        if (data.IsEmpty)
        {
            return;
        }

        (nint staged, uint stagedOffset) = Stage(data);
        _transfers.Add(new Transfer(staged, stagedOffset, (uint)data.Length, default, 0, region));
    }

    public void Release(GpuBuffer buffer)
    {
        ThrowIfNotRender();

        if (buffer != GpuBuffer.None)
        {
            _releasing.Add((Kind.Buffer, buffer.Id));
        }
    }

    public void Release(GpuTexture texture)
    {
        ThrowIfNotRender();

        if (_textures[texture.Id].Window is { } window)
        {
            _windows.Remove(window.Id);
            _releasing.Add((Kind.Window, texture.Id));
            return;
        }

        if (texture != GpuTexture.None)
        {
            _releasing.Add((Kind.Texture, texture.Id));
        }
    }

    public void Release(GpuSampler sampler)
    {
        ThrowIfNotRender();

        if (sampler != GpuSampler.None)
        {
            _releasing.Add((Kind.Sampler, sampler.Id));
        }
    }

    public void Release(GpuPipeline pipeline)
    {
        ThrowIfNotRender();

        if (pipeline != GpuPipeline.None)
        {
            _releasing.Add((_pipelines[pipeline.Id].IsCompute ? Kind.ComputePipeline : Kind.GraphicsPipeline, pipeline.Id));
        }
    }

    public Result<byte[]> Read(GpuBuffer buffer, uint bytes)
    {
        ThrowIfNotRender();

        nint handle = _buffers[buffer.Id];
        if (handle == 0 || bytes == 0)
        {
            return Result.Failure<byte[]>($"Buffer {buffer.Id} is not one the GPU made, or none of it was asked for.");
        }

        SDL_GPUTransferBuffer* transfer = CreateTransferBuffer(SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, bytes);
        SDL_GPUCommandBuffer* commandBuffer = SDL3.SDL_AcquireGPUCommandBuffer(_device);
        SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
        SDL_GPUBufferRegion source = new() { buffer = (SDL_GPUBuffer*)handle, size = bytes };
        SDL_GPUTransferBufferLocation destination = new() { transfer_buffer = transfer };
        SDL3.SDL_DownloadFromGPUBuffer(copyPass, &source, &destination);
        SDL3.SDL_EndGPUCopyPass(copyPass);
        return Download(commandBuffer, transfer, bytes);
    }

    public Result<byte[]> Read(GpuTexture texture)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[texture.Id];
        if (stored.Handle == 0)
        {
            return Result.Failure<byte[]>($"Texture {texture.Id} is not one the GPU made, or is a window's, whose image cannot be read: draw into a texture of your own and read that.");
        }

        uint bytes = SDL3.SDL_CalculateGPUTextureFormatSize(stored.Format.ToSdl(), stored.Width, stored.Height, 1);
        SDL_GPUTransferBuffer* transfer = CreateTransferBuffer(SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, bytes);
        SDL_GPUCommandBuffer* commandBuffer = SDL3.SDL_AcquireGPUCommandBuffer(_device);
        SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
        SDL_GPUTextureRegion source = new() { texture = (SDL_GPUTexture*)stored.Handle, w = stored.Width, h = stored.Height, d = 1 };
        SDL_GPUTextureTransferInfo destination = new() { transfer_buffer = transfer, pixels_per_row = stored.Width, rows_per_layer = stored.Height };
        SDL3.SDL_DownloadFromGPUTexture(copyPass, &source, &destination);
        SDL3.SDL_EndGPUCopyPass(copyPass);
        return Download(commandBuffer, transfer, bytes);
    }

    public Task<Result<GpuImage>> CaptureAsync(GpuTexture window)
    {
        ThrowIfNotRender();

        TextureObject stored = _textures[window.Id];
        if (stored.Window is not { Native: not 0 })
        {
            return Task.FromResult(Result.Failure<GpuImage>($"Texture {window.Id} is no window's, or its window never has an image (headless)."));
        }

        if (stored.Format is not (GpuFormat.Rgba8Unorm or GpuFormat.Rgba8Srgb or GpuFormat.Bgra8Unorm or GpuFormat.Bgra8Srgb))
        {
            return Task.FromResult(Result.Failure<GpuImage>($"The window's images are {stored.Format}, which is not read back."));
        }

        if (!_captures.TryGetValue(window.Id, out TaskCompletionSource<Result<GpuImage>>? capture))
        {
            capture = new TaskCompletionSource<Result<GpuImage>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captures[window.Id] = capture;
        }

        return capture.Task;
    }

    private static InvalidOperationException Failed(string what) => new($"The GPU could not make {what}: {SDL3.SDL_GetError()}");

    /// <summary>
    /// Whether SDL is loaded and started, as the platform gem starts it: its events are up, whatever else is.
    /// </summary>
    private static bool IsSdlStarted()
    {
        try
        {
            return SDL3.SDL_WasInit(SDL_InitFlags.SDL_INIT_EVENTS).HasFlag(SDL_InitFlags.SDL_INIT_EVENTS);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// SDL ties a window and the command buffers that draw into it to the thread that made them: the render thread.
    /// </summary>
    private void ThrowIfNotRender()
    {
        if (!_threads.IsCurrent(EngineThread.Render))
        {
            throw new InvalidOperationException("The GPU is the render thread's: use it in Render, or hand the work to the render thread (Threads.Post or InvokeAsync).");
        }
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
        SDL_GPUCommandBuffer* commandBuffer = SDL3.SDL_AcquireGPUCommandBuffer(_device);
        if (commandBuffer == null)
        {
            Debugging.Error($"The frame was not drawn: SDL had no command buffer: {SDL3.SDL_GetError()}");
            UnmapStaging();
            _transfers.Clear();
            _images.Clear();
            _commands.Clear();
            FreeReleased();
            return;
        }

        UnmapStaging();
        RunTransfers(commandBuffer);
        foreach (ref readonly GpuCommand command in _commands.Commands)
        {
            Run(commandBuffer, command);
        }

        CopyCaptures(commandBuffer);
        _fences[slot] = (nint)SDL3.SDL_SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
        if (_fences[slot] == 0)
        {
            Debugging.Error($"SDL did not submit the frame: {SDL3.SDL_GetError()}");
        }

        FinishCaptures((SDL_GPUFence*)_fences[slot]);

        _frameNumber++;
        _images.Clear();
        _commands.Clear();
        FreeReleased();
    }

    /// <summary>
    /// Copies each capture begun this frame into its window's image, which the frame then shows, and downloads it. After
    /// every command, so outside any pass.
    /// </summary>
    private void CopyCaptures(SDL_GPUCommandBuffer* commandBuffer)
    {
        for (int i = 0; i < _capturing.Count; i++)
        {
            Capture capture = _capturing[i];
            SDL_GPUBlitInfo blit = new()
            {
                source = new SDL_GPUBlitRegion { texture = (SDL_GPUTexture*)capture.Copy, w = capture.Width, h = capture.Height },
                destination = new SDL_GPUBlitRegion { texture = (SDL_GPUTexture*)capture.Image, w = capture.Width, h = capture.Height },
                load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
                filter = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
            };
            SDL3.SDL_BlitGPUTexture(commandBuffer, &blit);

            SDL_GPUTransferBuffer* transfer = CreateTransferBuffer(SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD, capture.Width * capture.Height * 4);
            SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
            SDL_GPUTextureRegion source = new() { texture = (SDL_GPUTexture*)capture.Copy, w = capture.Width, h = capture.Height, d = 1 };
            SDL_GPUTextureTransferInfo destination = new() { transfer_buffer = transfer, pixels_per_row = capture.Width, rows_per_layer = capture.Height };
            SDL3.SDL_DownloadFromGPUTexture(copyPass, &source, &destination);
            SDL3.SDL_EndGPUCopyPass(copyPass);
            _capturing[i] = capture with { Transfer = (nint)transfer };
        }
    }

    /// <summary>
    /// Waits for the frame the captures were in, then hands each its pixels and lets its texture and download go.
    /// </summary>
    private void FinishCaptures(SDL_GPUFence* fence)
    {
        if (_capturing.Count == 0)
        {
            return;
        }

        bool isDone = fence != null && SDL3.SDL_WaitForGPUFences(_device, wait_all: true, &fence, 1);
        foreach (Capture capture in _capturing)
        {
            capture.Done.SetResult(isDone ? Pixels(capture) : Result.Failure<GpuImage>($"The frame it was in never finished: {SDL3.SDL_GetError()}"));
            SDL3.SDL_ReleaseGPUTransferBuffer(_device, (SDL_GPUTransferBuffer*)capture.Transfer);
            SDL3.SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)capture.Copy);
        }

        _capturing.Clear();
    }

    /// <summary>
    /// A capture's downloaded pixels as RGBA, opaque: a window shows no alpha, whatever was written to it.
    /// </summary>
    private Result<GpuImage> Pixels(in Capture capture)
    {
        byte* mapped = (byte*)SDL3.SDL_MapGPUTransferBuffer(_device, (SDL_GPUTransferBuffer*)capture.Transfer, cycle: false);
        if (mapped == null)
        {
            return Result.Failure<GpuImage>($"The capture never arrived: {SDL3.SDL_GetError()}");
        }

        byte[] pixels = new ReadOnlySpan<byte>(mapped, (int)(capture.Width * capture.Height * 4)).ToArray();
        SDL3.SDL_UnmapGPUTransferBuffer(_device, (SDL_GPUTransferBuffer*)capture.Transfer);
        bool isBgra = capture.Format is GpuFormat.Bgra8Unorm or GpuFormat.Bgra8Srgb;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (isBgra)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            pixels[i + 3] = byte.MaxValue;
        }

        return Result.Success(new GpuImage((int)capture.Width, (int)capture.Height, pixels));
    }

    /// <summary>
    /// Waits for the frame last submitted from <paramref name="slot"/>, then releases what was freed during it.
    /// </summary>
    private void ReleaseFrame(int slot)
    {
        SDL_GPUFence* fence = (SDL_GPUFence*)_fences[slot];
        if (fence != null)
        {
            if (!SDL3.SDL_WaitForGPUFences(_device, wait_all: true, &fence, 1))
            {
                Debugging.Error($"Waiting for a frame failed: {SDL3.SDL_GetError()}");
            }

            SDL3.SDL_ReleaseGPUFence(_device, fence);
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
            if (handle != 0)
            {
                freed.Add((kind, handle));
            }
        }

        _releasing.Clear();
    }

    private void Release(Kind kind, nint handle)
    {
        switch (kind)
        {
            case Kind.Buffer:
                SDL3.SDL_ReleaseGPUBuffer(_device, (SDL_GPUBuffer*)handle);
                break;
            case Kind.Texture:
                SDL3.SDL_ReleaseGPUTexture(_device, (SDL_GPUTexture*)handle);
                break;
            case Kind.Sampler:
                SDL3.SDL_ReleaseGPUSampler(_device, (SDL_GPUSampler*)handle);
                break;
            case Kind.GraphicsPipeline:
                SDL3.SDL_ReleaseGPUGraphicsPipeline(_device, (SDL_GPUGraphicsPipeline*)handle);
                break;
            case Kind.ComputePipeline:
                SDL3.SDL_ReleaseGPUComputePipeline(_device, (SDL_GPUComputePipeline*)handle);
                break;
            case Kind.TransferBuffer:
                SDL3.SDL_ReleaseGPUTransferBuffer(_device, (SDL_GPUTransferBuffer*)handle);
                break;
            case Kind.Window:
                SDL3.SDL_ReleaseWindowFromGPUDevice(_device, (SDL_Window*)handle);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "There is no such kind of GPU object.");
        }
    }

    /// <summary>
    /// Claims a window for the GPU, presenting as the settings ask, and returns its images' format. A window given back
    /// a moment ago is still the GPU's until the frames in flight are done with it, so a refused claim waits for them
    /// and tries once more.
    /// </summary>
    private GpuFormat Claim(SDL_Window* window)
    {
        SDL_WindowID id = SDL3.SDL_GetWindowID(window);
        if (!SDL3.SDL_ClaimWindowForGPUDevice(_device, window))
        {
            for (int slot = 0; slot < FramesInFlight; slot++)
            {
                ReleaseFrame(slot);
            }

            if (!SDL3.SDL_ClaimWindowForGPUDevice(_device, window))
            {
                throw new InvalidOperationException($"The GPU cannot draw into window {id}: {SDL3.SDL_GetError()}");
            }
        }

        SDL_GPUPresentMode present = PresentMode(window);
        if (!SDL3.SDL_SetGPUSwapchainParameters(_device, window, SDL_GPUSwapchainComposition.SDL_GPU_SWAPCHAINCOMPOSITION_SDR, present))
        {
            Debugging.Warn($"Window {id} presents as SDL chose, not {present}: {SDL3.SDL_GetError()}");
        }

        GpuFormat format = SDL3.SDL_GetGPUSwapchainTextureFormat(_device, window).ToMagic();
        Debugging.Verbose($"Window {id} is a render target: {format}, presenting with {present}.");
        return format;
    }

    /// <summary>
    /// Vsync, or when the settings turn it off, uncapped: immediate (tearing allowed) where the window has it, else
    /// mailbox.
    /// </summary>
    private SDL_GPUPresentMode PresentMode(SDL_Window* window)
    {
        if (_settings.Vsync)
        {
            return SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
        }

        return SDL3.SDL_WindowSupportsGPUPresentMode(_device, window, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE) ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_IMMEDIATE
            : SDL3.SDL_WindowSupportsGPUPresentMode(_device, window, SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX) ? SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_MAILBOX
            : SDL_GPUPresentMode.SDL_GPU_PRESENTMODE_VSYNC;
    }

    /// <summary>
    /// A vertex or fragment shader object, kept only while a pipeline is made of it; null when SDL refuses it.
    /// </summary>
    private SDL_GPUShader* CreateShader(GpuShader shader)
    {
        byte[] entryPoint = Encoding.UTF8.GetBytes(shader.EntryPoint + '\0');
        fixed (byte* code = shader.Code.Span)
        fixed (byte* entry = entryPoint)
        {
            SDL_GPUShaderCreateInfo info = new()
            {
                code = code,
                code_size = (nuint)shader.Code.Length,
                entrypoint = entry,
                format = ShaderFormat.ToSdl(),
                stage = shader.Stage == GpuStage.Vertex ? SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX : SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT,
                num_samplers = shader.Samplers,
                num_storage_textures = shader.StorageTextures,
                num_storage_buffers = shader.StorageBuffers,
                num_uniform_buffers = shader.UniformBuffers,
            };
            return SDL3.SDL_CreateGPUShader(_device, &info);
        }
    }

    private SDL_GPUTransferBuffer* CreateTransferBuffer(SDL_GPUTransferBufferUsage usage, uint bytes)
    {
        SDL_GPUTransferBufferCreateInfo info = new() { usage = usage, size = bytes };
        SDL_GPUTransferBuffer* transfer = SDL3.SDL_CreateGPUTransferBuffer(_device, &info);
        return transfer == null ? throw Failed($"a {bytes}-byte transfer buffer") : transfer;
    }

    /// <summary>
    /// Submits a command buffer of downloads into <paramref name="transfer"/>, waits for it, and copies the bytes out.
    /// </summary>
    private Result<byte[]> Download(SDL_GPUCommandBuffer* commandBuffer, SDL_GPUTransferBuffer* transfer, uint bytes)
    {
        try
        {
            SDL_GPUFence* fence = SDL3.SDL_SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
            if (fence == null)
            {
                return Result.Failure<byte[]>($"SDL did not submit the read: {SDL3.SDL_GetError()}");
            }

            bool isDone = SDL3.SDL_WaitForGPUFences(_device, wait_all: true, &fence, 1);
            SDL3.SDL_ReleaseGPUFence(_device, fence);
            byte* mapped = isDone ? (byte*)SDL3.SDL_MapGPUTransferBuffer(_device, transfer, cycle: false) : null;
            if (mapped == null)
            {
                return Result.Failure<byte[]>($"The read never arrived: {SDL3.SDL_GetError()}");
            }

            byte[] read = new ReadOnlySpan<byte>(mapped, (int)bytes).ToArray();
            SDL3.SDL_UnmapGPUTransferBuffer(_device, transfer);
            return Result.Success(read);
        }
        finally
        {
            SDL3.SDL_ReleaseGPUTransferBuffer(_device, transfer);
        }
    }

    /// <summary>
    /// Copies <paramref name="data"/> into staging, 16-byte aligned; returns the transfer buffer and offset the next
    /// copy pass reads it from.
    /// </summary>
    private (nint Buffer, uint Offset) Stage(ReadOnlySpan<byte> data)
    {
        uint bytes = (uint)data.Length;
        uint offset = (_stagingUsed + 15u) & ~15u;
        if (_stagingMapped != null && offset + bytes > _stagingCapacity)
        {
            UnmapStaging();
            GrowStaging(offset + bytes);
        }

        if (_stagingMapped == null)
        {
            if (bytes > _stagingCapacity)
            {
                GrowStaging(bytes);
            }

            _stagingMapped = (byte*)SDL3.SDL_MapGPUTransferBuffer(_device, _staging, cycle: true);
            if (_stagingMapped == null)
            {
                throw new InvalidOperationException($"SDL would not map staging memory: {SDL3.SDL_GetError()}");
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
        _staging = CreateTransferBuffer(SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD, _stagingCapacity);
        Debugging.Verbose($"GPU staging grew to {_stagingCapacity / 1024} KiB.");
    }

    private void UnmapStaging()
    {
        if (_stagingMapped == null)
        {
            return;
        }

        SDL3.SDL_UnmapGPUTransferBuffer(_device, _staging);
        _stagingMapped = null;
        _stagingUsed = 0;
    }

    /// <summary>
    /// Every upload queued since the last submit, in order, in one copy pass.
    /// </summary>
    private void RunTransfers(SDL_GPUCommandBuffer* commandBuffer)
    {
        if (_transfers.Count == 0)
        {
            return;
        }

        SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
        foreach (Transfer transfer in _transfers)
        {
            if (transfer.Region.Texture == GpuTexture.None)
            {
                SDL_GPUTransferBufferLocation source = new() { transfer_buffer = (SDL_GPUTransferBuffer*)transfer.Staged, offset = transfer.StagedOffset };
                SDL_GPUBufferRegion destination = new() { buffer = (SDL_GPUBuffer*)_buffers[transfer.Buffer.Id], offset = transfer.BufferOffset, size = transfer.Bytes };
                SDL3.SDL_UploadToGPUBuffer(copyPass, &source, &destination, cycle: false);
                continue;
            }

            SDL_GPUTextureRegion region = TextureRegion(transfer.Region);
            SDL_GPUTextureTransferInfo texels = new() { transfer_buffer = (SDL_GPUTransferBuffer*)transfer.Staged, offset = transfer.StagedOffset, pixels_per_row = region.w, rows_per_layer = region.h };
            SDL3.SDL_UploadToGPUTexture(copyPass, &texels, &region, cycle: false);
        }

        SDL3.SDL_EndGPUCopyPass(copyPass);
        _transfers.Clear();
    }

    /// <summary>
    /// A region with its size filled in: a width, height or depth of 0 is the whole level.
    /// </summary>
    private SDL_GPUTextureRegion TextureRegion(in GpuTextureRegion region)
    {
        TextureObject stored = _textures[region.Texture.Id];
        int level = (int)region.Level;
        return new SDL_GPUTextureRegion
        {
            texture = (SDL_GPUTexture*)stored.Handle,
            mip_level = region.Level,
            layer = region.Layer,
            x = region.X,
            y = region.Y,
            z = region.Z,
            w = region.Width != 0 ? region.Width : Math.Max(1, stored.Width >> level),
            h = region.Height != 0 ? region.Height : Math.Max(1, stored.Height >> level),
            d = region.Depth != 0 ? region.Depth : Math.Max(1, stored.Depth >> level),
        };
    }

    /// <summary>
    /// One recorded command, as its SDL call.
    /// </summary>
    private void Run(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        if (_isSkipping && command.Type != GpuCommandType.EndRenderPass)
        {
            return;
        }

        switch (command.Type)
        {
            case GpuCommandType.BeginRenderPass:
                BeginRenderPass(commandBuffer, command);
                break;
            case GpuCommandType.EndRenderPass:
                if (_renderPass != null)
                {
                    SDL3.SDL_EndGPURenderPass(_renderPass);
                }

                _renderPass = null;
                _isSkipping = false;
                break;
            case GpuCommandType.BeginComputePass:
                BeginComputePass(commandBuffer, command);
                break;
            case GpuCommandType.EndComputePass:
                SDL3.SDL_EndGPUComputePass(_computePass);
                _computePass = null;
                break;
            case GpuCommandType.SetViewport:
            {
                SDL_GPUViewport viewport = new() { x = command.Area.X, y = command.Area.Y, w = command.Area.Width, h = command.Area.Height, max_depth = 1 };
                SDL3.SDL_SetGPUViewport(_renderPass, &viewport);
                break;
            }

            case GpuCommandType.SetScissor:
            {
                // Inside the target's corner: a scissor that starts before it is an error on some backends.
                int x = Math.Max(command.Area.X, 0);
                int y = Math.Max(command.Area.Y, 0);
                SDL_Rect scissor = new() { x = x, y = y, w = Math.Max(command.Area.Right - x, 0), h = Math.Max(command.Area.Bottom - y, 0) };
                SDL3.SDL_SetGPUScissor(_renderPass, &scissor);
                break;
            }

            case GpuCommandType.BindPipeline:
                BindPipeline(command.Pipeline);
                break;
            case GpuCommandType.BindVertexBuffers:
                BindVertexBuffers(command);
                break;
            case GpuCommandType.BindIndexBuffer:
            {
                SDL_GPUBufferBinding index = new() { buffer = (SDL_GPUBuffer*)_buffers[command.Buffer.Id] };
                SDL3.SDL_BindGPUIndexBuffer(_renderPass, &index, command.Wide ? SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_32BIT : SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_16BIT);
                break;
            }

            case GpuCommandType.BindStorageBuffers:
                BindStorageBuffers(command);
                break;
            case GpuCommandType.BindStorageTextures:
                BindStorageTextures(command);
                break;
            case GpuCommandType.BindTextures:
                BindTextures(command);
                break;
            case GpuCommandType.PushConstants:
                PushConstants(commandBuffer, command);
                break;
            case GpuCommandType.Draw:
                SDL3.SDL_DrawGPUPrimitives(_renderPass, command.Count, command.Instances, command.First, command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexed:
                SDL3.SDL_DrawGPUIndexedPrimitives(_renderPass, command.Count, command.Instances, command.First, command.VertexOffset, command.FirstInstance);
                break;
            case GpuCommandType.DrawIndexedIndirect:
                SDL3.SDL_DrawGPUIndexedPrimitivesIndirect(_renderPass, (SDL_GPUBuffer*)_buffers[command.Buffer.Id], command.Offset, command.Count);
                break;
            case GpuCommandType.Dispatch:
                SDL3.SDL_DispatchGPUCompute(_computePass, command.GroupsX, command.GroupsY, command.GroupsZ);
                break;
            case GpuCommandType.DispatchIndirect:
                SDL3.SDL_DispatchGPUComputeIndirect(_computePass, (SDL_GPUBuffer*)_buffers[command.Buffer.Id], command.Offset);
                break;
            case GpuCommandType.CopyTexture:
                CopyTexture(commandBuffer, command);
                break;
            case GpuCommandType.CopyBuffer:
                CopyBuffer(commandBuffer, command);
                break;
            case GpuCommandType.Blit:
                Blit(commandBuffer, command);
                break;
            case GpuCommandType.GenerateMipmaps:
                SDL3.SDL_GenerateMipmapsForGPUTexture(commandBuffer, (SDL_GPUTexture*)_textures[_commands.BindingsOf(command)[0].Texture.Id].Handle);
                break;
            case GpuCommandType.PushDebugGroup:
                PushDebugGroup(commandBuffer, command);
                break;
            case GpuCommandType.PopDebugGroup:
                SDL3.SDL_PopGPUDebugGroup(commandBuffer);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Type, "There is no such GPU command.");
        }
    }

    private void BeginRenderPass(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> colors = _commands.BindingsOf(command);
        SDL_GPUColorTargetInfo* targets = stackalloc SDL_GPUColorTargetInfo[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            SDL_GPUTexture* texture = Texture(commandBuffer, colors[i].Texture);
            if (texture == null)
            {
                _isSkipping = true;
                return;
            }

            targets[i] = new SDL_GPUColorTargetInfo
            {
                texture = texture,
                mip_level = colors[i].Level,
                layer_or_depth_plane = colors[i].Layer,
                clear_color = new SDL_FColor { r = command.Clear.R, g = command.Clear.G, b = command.Clear.B, a = command.Clear.A },
                load_op = command.Load.ToSdl(),
                store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            };
        }

        if (command.Depth == GpuTexture.None)
        {
            _renderPass = SDL3.SDL_BeginGPURenderPass(commandBuffer, targets, (uint)colors.Length, null);
            return;
        }

        SDL_GPUDepthStencilTargetInfo depth = new()
        {
            texture = Texture(commandBuffer, command.Depth),
            load_op = command.Load.ToSdl(),
            store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
            stencil_load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
            stencil_store_op = SDL_GPUStoreOp.SDL_GPU_STOREOP_DONT_CARE,
            layer = (byte)command.DepthLayer,
        };
        _renderPass = SDL3.SDL_BeginGPURenderPass(commandBuffer, targets, (uint)colors.Length, &depth);
    }

    private void BeginComputePass(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> writes = _commands.BindingsOf(command);
        int textureCount = 0;
        foreach (GpuBinding write in writes)
        {
            textureCount += write.Texture != GpuTexture.None ? 1 : 0;
        }

        SDL_GPUStorageTextureReadWriteBinding* textures = stackalloc SDL_GPUStorageTextureReadWriteBinding[textureCount];
        SDL_GPUStorageBufferReadWriteBinding* buffers = stackalloc SDL_GPUStorageBufferReadWriteBinding[writes.Length - textureCount];
        int texture = 0;
        int buffer = 0;
        foreach (GpuBinding write in writes)
        {
            if (write.Texture != GpuTexture.None)
            {
                textures[texture++] = new SDL_GPUStorageTextureReadWriteBinding { texture = (SDL_GPUTexture*)_textures[write.Texture.Id].Handle, mip_level = write.Level, layer = write.Layer };
                continue;
            }

            buffers[buffer++] = new SDL_GPUStorageBufferReadWriteBinding { buffer = (SDL_GPUBuffer*)_buffers[write.Buffer.Id] };
        }

        _computePass = SDL3.SDL_BeginGPUComputePass(commandBuffer, textures, (uint)texture, buffers, (uint)buffer);
    }

    private void BindPipeline(GpuPipeline pipeline)
    {
        PipelineObject stored = _pipelines[pipeline.Id];
        if (stored.IsCompute)
        {
            SDL3.SDL_BindGPUComputePipeline(_computePass, (SDL_GPUComputePipeline*)stored.Handle);
            return;
        }

        SDL3.SDL_BindGPUGraphicsPipeline(_renderPass, (SDL_GPUGraphicsPipeline*)stored.Handle);
    }

    private void BindVertexBuffers(in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUBufferBinding* buffers = stackalloc SDL_GPUBufferBinding[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            buffers[i] = new SDL_GPUBufferBinding { buffer = (SDL_GPUBuffer*)_buffers[run[i].Buffer.Id] };
        }

        SDL3.SDL_BindGPUVertexBuffers(_renderPass, command.Slot, buffers, (uint)run.Length);
    }

    private void BindStorageBuffers(in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUBuffer** buffers = stackalloc SDL_GPUBuffer*[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            buffers[i] = (SDL_GPUBuffer*)_buffers[run[i].Buffer.Id];
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL3.SDL_BindGPUVertexStorageBuffers(_renderPass, command.Slot, buffers, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL3.SDL_BindGPUFragmentStorageBuffers(_renderPass, command.Slot, buffers, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL3.SDL_BindGPUComputeStorageBuffers(_computePass, command.Slot, buffers, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void BindStorageTextures(in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUTexture** textures = stackalloc SDL_GPUTexture*[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            textures[i] = (SDL_GPUTexture*)_textures[run[i].Texture.Id].Handle;
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL3.SDL_BindGPUVertexStorageTextures(_renderPass, command.Slot, textures, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL3.SDL_BindGPUFragmentStorageTextures(_renderPass, command.Slot, textures, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL3.SDL_BindGPUComputeStorageTextures(_computePass, command.Slot, textures, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void BindTextures(in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUTextureSamplerBinding* textures = stackalloc SDL_GPUTextureSamplerBinding[run.Length];
        for (int i = 0; i < run.Length; i++)
        {
            textures[i] = new SDL_GPUTextureSamplerBinding
            {
                texture = (SDL_GPUTexture*)_textures[run[i].Texture.Id].Handle,
                sampler = (SDL_GPUSampler*)_samplers[run[i].Sampler.Id],
            };
        }

        switch (command.Stage)
        {
            case GpuStage.Vertex:
                SDL3.SDL_BindGPUVertexSamplers(_renderPass, command.Slot, textures, (uint)run.Length);
                break;
            case GpuStage.Fragment:
                SDL3.SDL_BindGPUFragmentSamplers(_renderPass, command.Slot, textures, (uint)run.Length);
                break;
            case GpuStage.Compute:
                SDL3.SDL_BindGPUComputeSamplers(_computePass, command.Slot, textures, (uint)run.Length);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
        }
    }

    private void PushConstants(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<byte> bytes = _commands.BytesOf(command);
        fixed (byte* data = bytes)
        {
            switch (command.Stage)
            {
                case GpuStage.Vertex:
                    SDL3.SDL_PushGPUVertexUniformData(commandBuffer, 0, (nint)data, (uint)bytes.Length);
                    break;
                case GpuStage.Fragment:
                    SDL3.SDL_PushGPUFragmentUniformData(commandBuffer, 0, (nint)data, (uint)bytes.Length);
                    break;
                case GpuStage.Compute:
                    SDL3.SDL_PushGPUComputeUniformData(commandBuffer, 0, (nint)data, (uint)bytes.Length);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Stage, "There is no such shader stage.");
            }
        }
    }

    private void CopyTexture(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUTextureLocation source = new() { texture = (SDL_GPUTexture*)_textures[run[0].Texture.Id].Handle, mip_level = run[0].Level, layer = run[0].Layer };
        SDL_GPUTextureLocation destination = new() { texture = (SDL_GPUTexture*)_textures[run[1].Texture.Id].Handle, mip_level = run[1].Level, layer = run[1].Layer };
        SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
        SDL3.SDL_CopyGPUTextureToTexture(copyPass, &source, &destination, (uint)command.Area.Width, (uint)command.Area.Height, 1, cycle: false);
        SDL3.SDL_EndGPUCopyPass(copyPass);
    }

    private void CopyBuffer(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUBufferLocation source = new() { buffer = (SDL_GPUBuffer*)_buffers[run[0].Buffer.Id], offset = command.First };
        SDL_GPUBufferLocation destination = new() { buffer = (SDL_GPUBuffer*)_buffers[run[1].Buffer.Id], offset = command.Offset };
        SDL_GPUCopyPass* copyPass = SDL3.SDL_BeginGPUCopyPass(commandBuffer);
        SDL3.SDL_CopyGPUBufferToBuffer(copyPass, &source, &destination, command.Count, cycle: false);
        SDL3.SDL_EndGPUCopyPass(copyPass);
    }

    /// <summary>
    /// Whole levels, the source scaled to the destination; a window's image is acquired as a render pass's is, and the
    /// blit skipped while there is none.
    /// </summary>
    private void Blit(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<GpuBinding> run = _commands.BindingsOf(command);
        SDL_GPUTexture* destination = Texture(commandBuffer, run[1].Texture);
        if (destination == null)
        {
            return;
        }

        TextureObject from = _textures[run[0].Texture.Id];
        System.Drawing.Size to = Size(run[1].Texture);
        SDL_GPUBlitInfo info = new()
        {
            source = new SDL_GPUBlitRegion
            {
                texture = (SDL_GPUTexture*)from.Handle,
                mip_level = run[0].Level,
                layer_or_depth_plane = run[0].Layer,
                w = Math.Max(1, from.Width >> (int)run[0].Level),
                h = Math.Max(1, from.Height >> (int)run[0].Level),
            },
            destination = new SDL_GPUBlitRegion
            {
                texture = destination,
                mip_level = run[1].Level,
                layer_or_depth_plane = run[1].Layer,
                w = (uint)Math.Max(1, to.Width >> (int)run[1].Level),
                h = (uint)Math.Max(1, to.Height >> (int)run[1].Level),
            },
            load_op = SDL_GPULoadOp.SDL_GPU_LOADOP_DONT_CARE,
            filter = ((GpuFilter)command.Slot).ToSdl(),
        };
        SDL3.SDL_BlitGPUTexture(commandBuffer, &info);
    }

    private void PushDebugGroup(SDL_GPUCommandBuffer* commandBuffer, in GpuCommand command)
    {
        ReadOnlySpan<byte> name = _commands.BytesOf(command);
        byte* terminated = stackalloc byte[name.Length + 1];
        name.CopyTo(new Span<byte>(terminated, name.Length));
        terminated[name.Length] = 0;
        SDL3.SDL_PushGPUDebugGroup(commandBuffer, terminated);
    }

    /// <summary>
    /// A texture's SDL object. A window's image is acquired the first time the frame draws into it, and is null when
    /// there is none this frame (minimised, or headless).
    /// </summary>
    private SDL_GPUTexture* Texture(SDL_GPUCommandBuffer* commandBuffer, GpuTexture texture)
    {
        TextureObject stored = _textures[texture.Id];
        if (stored.Window is not { } window)
        {
            return (SDL_GPUTexture*)stored.Handle;
        }

        if (_images.TryGetValue(texture.Id, out nint acquired))
        {
            return (SDL_GPUTexture*)acquired;
        }

        uint width = 0;
        uint height = 0;
        SDL_GPUTexture* image = window.Native == 0 ? null : Acquire(commandBuffer, window, &width, &height);
        if (image != null && _captures.Remove(texture.Id, out TaskCompletionSource<Result<GpuImage>>? capture))
        {
            image = BeginCapture(image, stored.Format, width, height, capture);
        }

        _images[texture.Id] = (nint)image;
        return image;
    }

    /// <summary>
    /// The window's image this frame, and its size; null when it has none (minimised).
    /// </summary>
    private static SDL_GPUTexture* Acquire(SDL_GPUCommandBuffer* commandBuffer, WindowObject window, uint* width, uint* height)
    {
        SDL_GPUTexture* image = null;
        if (!SDL3.SDL_WaitAndAcquireGPUSwapchainTexture(commandBuffer, (SDL_Window*)window.Native, &image, width, height))
        {
            Debugging.Warn($"Window {window.Id}'s image was not acquired: {SDL3.SDL_GetError()}");
            return null;
        }

        return image;
    }

    /// <summary>
    /// A texture like the window's <paramref name="image"/>, which the frame draws into in its place, then copies into it
    /// and reads back (<see cref="CopyCaptures"/>); the image itself when no such texture can be made, the capture
    /// failing.
    /// </summary>
    private SDL_GPUTexture* BeginCapture(SDL_GPUTexture* image, GpuFormat format, uint width, uint height, TaskCompletionSource<Result<GpuImage>> done)
    {
        SDL_GPUTextureCreateInfo info = new()
        {
            type = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
            format = format.ToSdl(),
            usage = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_COLOR_TARGET | SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
            width = width,
            height = height,
            layer_count_or_depth = 1,
            num_levels = 1,
            sample_count = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1,
        };
        SDL_GPUTexture* copy = SDL3.SDL_CreateGPUTexture(_device, &info);
        if (copy == null)
        {
            done.SetResult(Result.Failure<GpuImage>($"The GPU could not make a {width} x {height} copy of the window's image: {SDL3.SDL_GetError()}"));
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
    private readonly record struct Transfer(nint Staged, uint StagedOffset, uint Bytes, GpuBuffer Buffer, uint BufferOffset, GpuTextureRegion Region);

    /// <summary>
    /// A texture, or a window's render target, which has no SDL texture of its own (no <see cref="Handle"/>): its
    /// texture is the image acquired each frame.
    /// </summary>
    private readonly record struct TextureObject(nint Handle, GpuFormat Format, uint Width, uint Height, uint Depth, WindowObject? Window = null);

    /// <summary>
    /// The window a render target draws into: SDL's window (none, headless) and the platform's id for it.
    /// </summary>
    private readonly record struct WindowObject(nint Native, uint Id);

    /// <summary>
    /// A capture begun this frame: the window's image, the texture drawn into in its place, their size and format, where
    /// it is downloaded to, and who waits for it.
    /// </summary>
    private readonly record struct Capture(nint Image, nint Copy, uint Width, uint Height, GpuFormat Format, nint Transfer, TaskCompletionSource<Result<GpuImage>> Done);

    private readonly record struct PipelineObject(nint Handle, bool IsCompute);

    /// <summary>
    /// SDL objects by handle id: dense, id 0 never used, freed ids used again.
    /// </summary>
    private sealed class Table<T>
        where T : struct
    {
        private readonly List<T> _items = [default];
        private readonly List<bool> _isLive = [false];
        private readonly Stack<uint> _free = [];

        public IEnumerable<T> Live
        {
            get
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_isLive[i])
                    {
                        yield return _items[i];
                    }
                }
            }
        }

        /// <summary>
        /// What <paramref name="id"/> holds; the default for an id that holds nothing.
        /// </summary>
        public T this[uint id] => id < _items.Count && _isLive[(int)id] ? _items[(int)id] : default;

        public uint Add(T item)
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
        public T Free(uint id)
        {
            if (id == 0 || id >= _items.Count || !_isLive[(int)id])
            {
                return default;
            }

            T item = _items[(int)id];
            _items[(int)id] = default;
            _isLive[(int)id] = false;
            _free.Push(id);
            return item;
        }
    }
}
