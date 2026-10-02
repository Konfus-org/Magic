using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;
using SDL3;
using System.Diagnostics;

namespace SDLRenderGem;

/// <summary>
/// <see cref="IRendering"/> on SDL GPU: handles to SDL objects, uploads staged at the call and copied in one pass at the
/// start of <see cref="Submit"/>, and a walk over the frame's commands with one SDL call per command. It keeps no caches
/// and knows nothing of what it draws. A released handle stays valid until the end of the next submit (the commands of
/// that frame may still name it); its SDL object goes once no frame in flight uses it. Everything still alive is released
/// when the gem unloads. The device is made on the first call that needs it, so GPU validation follows <see cref="Debug"/>.
/// </summary>
internal sealed class SdlRendering : IGem, IRendering
{
    private static readonly SDL.GPUColorTargetBlendState AlphaBlend = new()
    {
        EnableBlend = true,
        SrcColorBlendFactor = SDL.GPUBlendFactor.SrcAlpha,
        DstColorBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha,
        ColorBlendOp = SDL.GPUBlendOp.Add,
        SrcAlphaBlendFactor = SDL.GPUBlendFactor.One,
        DstAlphaBlendFactor = SDL.GPUBlendFactor.OneMinusSrcAlpha,
        AlphaBlendOp = SDL.GPUBlendOp.Add,
    };

    private readonly Project _project;
    private readonly IFileSystem _files;
    private GpuDevice? _device;
    private readonly HandleTable<nint> _buffers = new();
    private readonly HandleTable<TextureObject> _textures = new();
    private readonly HandleTable<nint> _samplers = new();
    private readonly HandleTable<PipelineObject> _pipelines = new();
    private readonly List<Transfer> _transfers = [];              // queued uploads and copies, in call order
    private readonly List<(GpuDevice.Kind Kind, uint Id)> _releasing = []; // released since the last submit
    private readonly Dictionary<uint, (nint Texture, uint Width, uint Height)> _swapchains = []; // this submit's images
    private readonly Dictionary<uint, GpuTexture> _presented = [];  // by window, the texture last blitted onto it

    public SdlRendering(Project project, IFileSystem files)
    {
        _project = project;
        _files = files;
    }

    public void Dispose()
    {
        if (_device is null || _device.Handle == 0)
            return;

        _device.WaitIdle();
        foreach (nint buffer in _buffers.Live)
            _device.Release(GpuDevice.Kind.Buffer, buffer);
        foreach (TextureObject texture in _textures.Live)
            _device.Release(GpuDevice.Kind.Texture, texture.Handle);
        foreach (nint sampler in _samplers.Live)
            _device.Release(GpuDevice.Kind.Sampler, sampler);
        foreach (PipelineObject pipeline in _pipelines.Live)
            _device.Release(pipeline.Compute ? GpuDevice.Kind.ComputePipeline : GpuDevice.Kind.GraphicsPipeline, pipeline.Handle);

        _device.Dispose();
    }

    public bool Debug { get; set; }

    public string Device => Gpu.Name;

    public string ShaderFormat => Gpu.ShaderFormat.ToString();

    public GpuFormat DepthFormat => Gpu.DepthFormat.ToEngine();

    /// <summary>Made on first use, so it is made with whatever <see cref="Debug"/> Core set when it took this renderer.</summary>
    private GpuDevice Gpu => _device ??= new GpuDevice(_project.Settings.Render, Debug, _files, _project.EngineGems);

    public GpuBuffer CreateBuffer(GpuBufferUsage usage, uint bytes)
    {
        Gpu.AssertMainThread();
        SDL.GPUBufferCreateInfo info = new() { Usage = usage.ToSdl(), Size = bytes };
        nint handle = GpuDevice.ThrowOnError(SDL.CreateGPUBuffer(Gpu.Handle, in info), "SDL_CreateGPUBuffer");
        return new GpuBuffer(_buffers.Add(handle));
    }

    public GpuTexture CreateTexture(in TextureDesc desc)
    {
        Gpu.AssertMainThread();
        SDL.GPUTextureCreateInfo info = new()
        {
            Type = desc.Layers > 1 ? SDL.GPUTextureType.TextureType2DArray : SDL.GPUTextureType.TextureType2D,
            Format = desc.Format.ToSdl(),
            Usage = desc.Usage.ToSdl(),
            Width = desc.Width,
            Height = desc.Height,
            LayerCountOrDepth = desc.Layers,
            NumLevels = desc.Levels,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
        };
        nint handle = GpuDevice.ThrowOnError(SDL.CreateGPUTexture(Gpu.Handle, in info), "SDL_CreateGPUTexture");
        return new GpuTexture(_textures.Add(new TextureObject(handle, desc.Width, desc.Height)));
    }

    public GpuSampler CreateSampler(in SamplerDesc desc)
    {
        Gpu.AssertMainThread();
        SDL.GPUFilter filter = desc.Filter == GpuFilter.Nearest ? SDL.GPUFilter.Nearest : SDL.GPUFilter.Linear;
        SDL.GPUSamplerAddressMode address = desc.Address == GpuAddress.Repeat ? SDL.GPUSamplerAddressMode.Repeat : SDL.GPUSamplerAddressMode.ClampToEdge;
        SDL.GPUSamplerCreateInfo info = new()
        {
            MinFilter = filter,
            MagFilter = filter,
            MipmapMode = filter == SDL.GPUFilter.Nearest ? SDL.GPUSamplerMipmapMode.Nearest : SDL.GPUSamplerMipmapMode.Linear,
            AddressModeU = address,
            AddressModeV = address,
            AddressModeW = address,
            MaxAnisotropy = desc.Anisotropy,
            MinLod = 0f,
            MaxLod = 1000f,
        };
        nint handle = GpuDevice.ThrowOnError(SDL.CreateGPUSampler(Gpu.Handle, in info), "SDL_CreateGPUSampler");
        return new GpuSampler(_samplers.Add(handle));
    }

    public Result<CompiledShader> Compile(string hlsl, string name, GpuStage stage, string includeDirectory)
    {
        return Compiler.Compile(hlsl, name, stage, includeDirectory, Gpu.ShaderFormat);
    }

    /// <summary>A graphics pipeline; its two shader objects only live while it is made.</summary>
    public GpuPipeline CreatePipeline(PipelineDesc desc)
    {
        Gpu.AssertMainThread();
        nint vertex = CreateShader(desc.Vertex), fragment = 0;
        try
        {
            fragment = CreateShader(desc.Fragment);
            SDL.GPUVertexBufferDescription[] buffers = new SDL.GPUVertexBufferDescription[desc.Buffers.Length];
            for (int i = 0; i < buffers.Length; i++)
            {
                VertexBufferLayout layout = desc.Buffers[i];
                buffers[i] = new SDL.GPUVertexBufferDescription
                {
                    Slot = layout.Slot,
                    Pitch = layout.Pitch,
                    InputRate = layout.PerInstance ? SDL.GPUVertexInputRate.Instance : SDL.GPUVertexInputRate.Vertex,
                    InstanceStepRate = 0,
                };
            }

            SDL.GPUVertexAttribute[] attributes = new SDL.GPUVertexAttribute[desc.Attributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                VertexAttribute attribute = desc.Attributes[i];
                attributes[i] = new SDL.GPUVertexAttribute { Location = attribute.Location, BufferSlot = attribute.Slot, Format = attribute.Format.ToSdl(), Offset = attribute.Offset };
            }

            bool depth = desc.Depth != GpuFormat.Invalid;
            SDL.GPUColorTargetDescription[] targets = [new SDL.GPUColorTargetDescription { Format = desc.Color.ToSdl(), BlendState = desc.AlphaBlend ? AlphaBlend : default }];
            SDL.GPUGraphicsPipelineCreateInfo info = new()
            {
                VertexShader = vertex,
                FragmentShader = fragment,
                PrimitiveType = SDL.GPUPrimitiveType.TriangleList,
                RasterizerState = new SDL.GPURasterizerState
                {
                    FillMode = SDL.GPUFillMode.Fill,
                    CullMode = desc.Cull.ToSdl(),
                    FrontFace = desc.FrontFace == GpuFrontFace.Clockwise ? SDL.GPUFrontFace.Clockwise : SDL.GPUFrontFace.CounterClockwise,
                },
                MultisampleState = new SDL.GPUMultisampleState { SampleCount = SDL.GPUSampleCount.SampleCount1 },
                DepthStencilState = new SDL.GPUDepthStencilState
                {
                    CompareOp = desc.DepthCompare.ToSdl(),
                    EnableDepthTest = depth,
                    EnableDepthWrite = depth,
                },
                TargetInfo = new SDL.GPUGraphicsPipelineTargetInfo
                {
                    DepthStencilFormat = depth ? desc.Depth.ToSdl() : SDL.GPUTextureFormat.Invalid,
                    HasDepthStencilTarget = depth,
                },
            };
            nint handle = GpuDevice.ThrowOnError(SDL.CreateGPUGraphicsPipeline(Gpu.Handle, in info, buffers, attributes, targets), "SDL_CreateGPUGraphicsPipeline");
            return new GpuPipeline(_pipelines.Add(new PipelineObject(handle, Compute: false)));
        }
        finally
        {
            SDL.ReleaseGPUShader(Gpu.Handle, vertex);
            if (fragment != 0)
                SDL.ReleaseGPUShader(Gpu.Handle, fragment);
        }
    }

    public unsafe GpuPipeline CreateComputePipeline(CompiledShader shader)
    {
        Gpu.AssertMainThread();

        // A u8 literal is null-terminated and lives in the image: no marshalling, nothing to free.
        fixed (byte* entry = "main"u8)
        fixed (byte* code = shader.Code)
        {
            SDL.GPUComputePipelineCreateInfo info = new()
            {
                Code = (nint)code,
                CodeSize = (nuint)shader.Code.Length,
                Entrypoint = (nint)entry,
                Format = Gpu.ShaderFormat,
                NumSamplers = shader.Samplers,
                NumReadonlyStorageTextures = shader.StorageTextures,
                NumReadonlyStorageBuffers = shader.StorageBuffers,
                NumReadwriteStorageTextures = shader.ReadWriteStorageTextures,
                NumReadwriteStorageBuffers = shader.ReadWriteStorageBuffers,
                NumUniformBuffers = shader.UniformBuffers,
                ThreadcountX = shader.ThreadCountX,
                ThreadcountY = shader.ThreadCountY,
                ThreadcountZ = shader.ThreadCountZ,
            };
            nint handle = GpuDevice.ThrowOnError(SDL.CreateGPUComputePipeline(Gpu.Handle, in info), "SDL_CreateGPUComputePipeline");
            return new GpuPipeline(_pipelines.Add(new PipelineObject(handle, Compute: true)));
        }
    }

    public void Upload(GpuBuffer buffer, uint offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || !buffer.IsValid)
            return;

        (nint staged, uint stagedOffset) = Gpu.Staging.Stage(data);
        _transfers.Add(new Transfer(TransferKind.Buffer, staged, stagedOffset, buffer, offset, (uint)data.Length, default, default));
    }

    public void Upload(in TextureRegion region, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || !region.Texture.IsValid)
            return;

        (nint staged, uint stagedOffset) = Gpu.Staging.Stage(data);
        _transfers.Add(new Transfer(TransferKind.Texture, staged, stagedOffset, default, 0, (uint)data.Length, region, default));
    }

    public void Copy(in TextureRegion source, in TextureRegion destination)
    {
        _transfers.Add(new Transfer(TransferKind.TextureCopy, 0, 0, default, 0, 0, destination, source));
    }

    public void Release(GpuBuffer buffer)
    {
        if (buffer.IsValid)
            _releasing.Add((GpuDevice.Kind.Buffer, buffer.Id));
    }

    public void Release(GpuTexture texture)
    {
        if (texture.IsValid && !texture.IsWindow)
            _releasing.Add((GpuDevice.Kind.Texture, texture.Id));
    }

    public void Release(GpuSampler sampler)
    {
        if (sampler.IsValid)
            _releasing.Add((GpuDevice.Kind.Sampler, sampler.Id));
    }

    public void Release(GpuPipeline pipeline)
    {
        if (pipeline.IsValid)
            _releasing.Add((_pipelines[pipeline.Id].Compute ? GpuDevice.Kind.ComputePipeline : GpuDevice.Kind.GraphicsPipeline, pipeline.Id));
    }

    public GpuFormat WindowFormat(uint window)
    {
        nint native = Gpu.ClaimWindow(window);
        return native == 0 ? GpuFormat.Invalid : SDL.GetGPUSwapchainTextureFormat(Gpu.Handle, native).ToEngine();
    }

    public float Submit(RenderCommands commands)
    {
        Gpu.AssertMainThread();
        if (commands.Count == 0 && _transfers.Count == 0)
        {
            FreeReleased();
            return 0f;
        }

        long started = Stopwatch.GetTimestamp();
        nint commandBuffer = Gpu.BeginFrame();
        long waited = Stopwatch.GetTimestamp() - started; // for the frame that last used this slot

        RunTransfers(commandBuffer);
        waited += Execute(commandBuffer, commands);

        Gpu.Submit(commandBuffer);
        _swapchains.Clear();
        commands.Clear();
        FreeReleased();

        return (float)Stopwatch.GetElapsedTime(0, waited).TotalMilliseconds;
    }

    public Result<byte[]> Read(GpuBuffer buffer, uint bytes)
    {
        try
        {
            nint handle = _buffers[buffer.Id];
            return Result<byte[]>.Success(Gpu.Read(bytes, (copyPass, transfer) =>
            {
                SDL.GPUBufferRegion region = new() { Buffer = handle, Offset = 0, Size = bytes };
                SDL.GPUTransferBufferLocation location = new() { TransferBuffer = transfer, Offset = 0 };
                SDL.DownloadFromGPUBuffer(copyPass, in region, in location);
            }));
        }
        catch (InvalidOperationException ex)
        {
            return Result<byte[]>.Failure(ex.Message);
        }
    }

    public Result<CapturedFrame> Read(GpuTexture texture)
    {
        if (texture.IsWindow && !_presented.TryGetValue(texture.WindowHandle, out texture))
            return Result<CapturedFrame>.Failure("nothing has been shown in the window yet.");
        if (!texture.IsValid)
            return Result<CapturedFrame>.Failure("only a texture made by the renderer can be read back.");

        TextureObject stored = _textures[texture.Id];
        try
        {
            byte[] pixels = Gpu.Read(stored.Width * stored.Height * 4, (copyPass, transfer) =>
            {
                SDL.GPUTextureRegion region = new() { Texture = stored.Handle, W = stored.Width, H = stored.Height, D = 1 };
                SDL.GPUTextureTransferInfo destination = new() { TransferBuffer = transfer, Offset = 0, PixelsPerRow = stored.Width, RowsPerLayer = stored.Height };
                SDL.DownloadFromGPUTexture(copyPass, in region, in destination);
            });
            return Result<CapturedFrame>.Success(new CapturedFrame((int)stored.Width, (int)stored.Height, pixels));
        }
        catch (InvalidOperationException ex)
        {
            return Result<CapturedFrame>.Failure(ex.Message);
        }
    }

    /// <summary>A vertex or fragment shader object from compiled bytecode.</summary>
    private nint CreateShader(CompiledShader shader)
    {
        SDL.GPUShaderCreateInfo info = new()
        {
            Format = Gpu.ShaderFormat,
            Stage = shader.Stage == GpuStage.Vertex ? SDL.GPUShaderStage.Vertex : SDL.GPUShaderStage.Fragment,
            NumSamplers = shader.Samplers,
            NumStorageTextures = shader.StorageTextures,
            NumStorageBuffers = shader.StorageBuffers,
            NumUniformBuffers = shader.UniformBuffers,
        };
        return GpuDevice.ThrowOnError(SDL.CreateGPUShader(Gpu.Handle, in info, shader.Code, "main"), "SDL_CreateGPUShader");
    }

    /// <summary>Every upload and copy queued since the last submit, in order, in one copy pass.</summary>
    private void RunTransfers(nint commandBuffer)
    {
        if (_transfers.Count == 0)
            return;

        nint copyPass = SDL.BeginGPUCopyPass(commandBuffer);
        foreach (Transfer transfer in _transfers)
        {
            switch (transfer.Kind)
            {
                case TransferKind.Buffer:
                {
                    SDL.GPUTransferBufferLocation source = new() { TransferBuffer = transfer.Staged, Offset = transfer.StagedOffset };
                    SDL.GPUBufferRegion region = new() { Buffer = _buffers[transfer.Buffer.Id], Offset = transfer.BufferOffset, Size = transfer.Size };
                    SDL.UploadToGPUBuffer(copyPass, in source, in region, false);
                    break;
                }
                case TransferKind.Texture:
                {
                    SDL.GPUTextureRegion region = ToSdlRegion(transfer.Region);
                    SDL.GPUTextureTransferInfo source = new() { TransferBuffer = transfer.Staged, Offset = transfer.StagedOffset, PixelsPerRow = region.W, RowsPerLayer = region.H };
                    SDL.UploadToGPUTexture(copyPass, in source, in region, false);
                    break;
                }
                case TransferKind.TextureCopy:
                {
                    SDL.GPUTextureRegion from = ToSdlRegion(transfer.Source);
                    SDL.GPUTextureLocation source = new() { Texture = from.Texture, MipLevel = from.MipLevel, Layer = from.Layer, X = from.X, Y = from.Y };
                    SDL.GPUTextureLocation destination = new()
                    {
                        Texture = _textures[transfer.Region.Texture.Id].Handle,
                        MipLevel = transfer.Region.Level,
                        Layer = transfer.Region.Layer,
                        X = transfer.Region.X,
                        Y = transfer.Region.Y,
                    };
                    SDL.CopyGPUTextureToTexture(copyPass, in source, in destination, from.W, from.H, 1, false);
                    break;
                }
            }
        }

        SDL.EndGPUCopyPass(copyPass);
        _transfers.Clear();
    }

    /// <summary>A region with its size filled in: a width or height of 0 is the whole level.</summary>
    private SDL.GPUTextureRegion ToSdlRegion(in TextureRegion region)
    {
        TextureObject stored = _textures[region.Texture.Id];
        uint width = region.Width != 0 ? region.Width : Math.Max(1, stored.Width >> (int)region.Level);
        uint height = region.Height != 0 ? region.Height : Math.Max(1, stored.Height >> (int)region.Level);
        return new SDL.GPUTextureRegion { Texture = stored.Handle, MipLevel = region.Level, Layer = region.Layer, X = region.X, Y = region.Y, W = width, H = height, D = 1 };
    }

    /// <summary>
    /// The frame's commands, one SDL call each. A render pass into a window with no image this frame (minimised) is skipped
    /// to its end, and so is a blit to one. Returns the ticks spent waiting for swapchain images.
    /// </summary>
    private long Execute(nint commandBuffer, RenderCommands commands)
    {
        long waited = 0;
        nint render = 0, compute = 0;
        bool skipping = false;
        (uint Width, uint Height) target = default;
        ReadOnlySpan<GpuBinding> bindings = commands.Bindings;

        foreach (ref readonly RenderCommand command in commands.Commands)
        {
            if (skipping && command.Type != RenderCommandType.EndRenderPass)
                continue;

            switch (command.Type)
            {
                case RenderCommandType.BeginRenderPass:
                {
                    (nint color, uint width, uint height) = ResolveTexture(commandBuffer, command.Texture, ref waited);
                    if (color == 0)
                    {
                        skipping = true;
                        break;
                    }

                    target = (width, height);
                    SDL.GPULoadOp load = command.Load.ToSdl();
                    Span<SDL.GPUColorTargetInfo> colors =
                    [
                        new SDL.GPUColorTargetInfo
                        {
                            Texture = color,
                            ClearColor = new SDL.FColor { R = command.ClearColor.X, G = command.ClearColor.Y, B = command.ClearColor.Z, A = command.ClearColor.W },
                            LoadOp = load,
                            StoreOp = SDL.GPUStoreOp.Store,
                        },
                    ];
                    if (command.Depth.IsValid)
                    {
                        SDL.GPUDepthStencilTargetInfo depth = new()
                        {
                            Texture = _textures[command.Depth.Id].Handle,
                            ClearDepth = 0f, // reverse-Z: 0 is infinitely far
                            LoadOp = load,
                            StoreOp = SDL.GPUStoreOp.Store,
                            StencilLoadOp = SDL.GPULoadOp.DontCare,
                            StencilStoreOp = SDL.GPUStoreOp.DontCare,
                        };
                        render = SDL.BeginGPURenderPass(commandBuffer, colors, 1, in depth);
                    }
                    else
                        render = SDL.BeginGPURenderPass(commandBuffer, colors, 1, 0);
                    break;
                }
                case RenderCommandType.EndRenderPass:
                    if (render != 0)
                        SDL.EndGPURenderPass(render);
                    render = 0;
                    skipping = false;
                    break;
                case RenderCommandType.BeginComputePass:
                {
                    ReadOnlySpan<GpuBinding> writes = bindings.Slice(command.Run.Start, command.Run.Length);
                    int textureCount = 0;
                    foreach (GpuBinding write in writes)
                        textureCount += write.Texture.IsValid ? 1 : 0;

                    Span<SDL.GPUStorageTextureReadWriteBinding> textures = stackalloc SDL.GPUStorageTextureReadWriteBinding[textureCount];
                    Span<SDL.GPUStorageBufferReadWriteBinding> buffers = stackalloc SDL.GPUStorageBufferReadWriteBinding[writes.Length - textureCount];
                    int nextTexture = 0, nextBuffer = 0;
                    foreach (GpuBinding write in writes)
                    {
                        if (write.Texture.IsValid)
                            textures[nextTexture++] = new SDL.GPUStorageTextureReadWriteBinding { Texture = _textures[write.Texture.Id].Handle };
                        else
                            buffers[nextBuffer++] = new SDL.GPUStorageBufferReadWriteBinding { Buffer = _buffers[write.Buffer.Id] };
                    }

                    compute = SDL.BeginGPUComputePass(commandBuffer, textures, (uint)textures.Length, buffers, (uint)buffers.Length);
                    break;
                }
                case RenderCommandType.EndComputePass:
                    SDL.EndGPUComputePass(compute);
                    compute = 0;
                    break;
                case RenderCommandType.SetViewport:
                {
                    SDL.GPUViewport viewport = new() { X = command.Rect.X, Y = command.Rect.Y, W = command.Rect.Width, H = command.Rect.Height, MinDepth = 0f, MaxDepth = 1f };
                    SDL.SetGPUViewport(render, in viewport);
                    break;
                }
                case RenderCommandType.SetScissor:
                {
                    // Clipped to the target: a scissor outside it is an error on some backends.
                    int x0 = Math.Clamp(command.Rect.Left, 0, (int)target.Width), y0 = Math.Clamp(command.Rect.Top, 0, (int)target.Height);
                    int x1 = Math.Clamp(command.Rect.Right, x0, (int)target.Width), y1 = Math.Clamp(command.Rect.Bottom, y0, (int)target.Height);
                    SDL.Rect scissor = new() { X = x0, Y = y0, W = x1 - x0, H = y1 - y0 };
                    SDL.SetGPUScissor(render, in scissor);
                    break;
                }
                case RenderCommandType.BindPipeline:
                    if (compute != 0)
                        SDL.BindGPUComputePipeline(compute, _pipelines[command.Pipeline.Id].Handle);
                    else
                        SDL.BindGPUGraphicsPipeline(render, _pipelines[command.Pipeline.Id].Handle);
                    break;
                case RenderCommandType.BindVertexBuffers:
                {
                    ReadOnlySpan<GpuBinding> run = bindings.Slice(command.Run.Start, command.Run.Length);
                    Span<SDL.GPUBufferBinding> buffers = stackalloc SDL.GPUBufferBinding[run.Length];
                    for (int i = 0; i < run.Length; i++)
                        buffers[i] = new SDL.GPUBufferBinding { Buffer = _buffers[run[i].Buffer.Id], Offset = 0 };
                    SDL.BindGPUVertexBuffers(render, command.Slot, buffers, (uint)buffers.Length);
                    break;
                }
                case RenderCommandType.BindIndexBuffer:
                {
                    SDL.GPUBufferBinding index = new() { Buffer = _buffers[command.Buffer.Id], Offset = 0 };
                    SDL.BindGPUIndexBuffer(render, in index, command.Wide ? SDL.GPUIndexElementSize.IndexElementSize32Bit : SDL.GPUIndexElementSize.IndexElementSize16Bit);
                    break;
                }
                case RenderCommandType.BindStorageBuffers:
                {
                    ReadOnlySpan<GpuBinding> run = bindings.Slice(command.Run.Start, command.Run.Length);
                    Span<nint> buffers = stackalloc nint[run.Length];
                    for (int i = 0; i < run.Length; i++)
                        buffers[i] = _buffers[run[i].Buffer.Id];

                    if (command.Stage == GpuStage.Vertex)
                        SDL.BindGPUVertexStorageBuffers(render, command.Slot, buffers, (uint)buffers.Length);
                    else if (command.Stage == GpuStage.Fragment)
                        SDL.BindGPUFragmentStorageBuffers(render, command.Slot, buffers, (uint)buffers.Length);
                    else
                        SDL.BindGPUComputeStorageBuffers(compute, command.Slot, buffers, (uint)buffers.Length);
                    break;
                }
                case RenderCommandType.BindTextures:
                {
                    ReadOnlySpan<GpuBinding> run = bindings.Slice(command.Run.Start, command.Run.Length);
                    Span<SDL.GPUTextureSamplerBinding> textures = stackalloc SDL.GPUTextureSamplerBinding[run.Length];
                    for (int i = 0; i < run.Length; i++)
                        textures[i] = new SDL.GPUTextureSamplerBinding { Texture = _textures[run[i].Texture.Id].Handle, Sampler = _samplers[run[i].Sampler.Id] };

                    if (command.Stage == GpuStage.Vertex)
                        SDL.BindGPUVertexSamplers(render, command.Slot, textures, (uint)textures.Length);
                    else if (command.Stage == GpuStage.Fragment)
                        SDL.BindGPUFragmentSamplers(render, command.Slot, textures, (uint)textures.Length);
                    else
                        SDL.BindGPUComputeSamplers(compute, command.Slot, textures, (uint)textures.Length);
                    break;
                }
                case RenderCommandType.PushConstants:
                {
                    ReadOnlySpan<byte> bytes = commands.BytesOf(in command);
                    if (command.Stage == GpuStage.Vertex)
                        SDL.PushGPUVertexUniformData(commandBuffer, 0, bytes, (uint)bytes.Length);
                    else if (command.Stage == GpuStage.Fragment)
                        SDL.PushGPUFragmentUniformData(commandBuffer, 0, bytes, (uint)bytes.Length);
                    else
                        SDL.PushGPUComputeUniformData(commandBuffer, 0, bytes, (uint)bytes.Length);
                    break;
                }
                case RenderCommandType.Draw:
                    SDL.DrawGPUPrimitives(render, command.Count, command.Instances, command.First, command.FirstInstance);
                    break;
                case RenderCommandType.DrawIndexed:
                    SDL.DrawGPUIndexedPrimitives(render, command.Count, command.Instances, command.First, command.VertexOffset, command.FirstInstance);
                    break;
                case RenderCommandType.DrawIndexedIndirect:
                    SDL.DrawGPUIndexedPrimitivesIndirect(render, _buffers[command.Buffer.Id], command.Offset, command.Count);
                    break;
                case RenderCommandType.Dispatch:
                    SDL.DispatchGPUCompute(compute, command.Groups.X, command.Groups.Y, command.Groups.Z);
                    break;
                case RenderCommandType.DispatchIndirect:
                    SDL.DispatchGPUComputeIndirect(compute, _buffers[command.Buffer.Id], command.Offset);
                    break;
                case RenderCommandType.Blit:
                {
                    TextureRegion to = command.Destination;
                    (nint destination, uint width, uint height) = ResolveTexture(commandBuffer, to.Texture, ref waited);
                    if (destination == 0)
                        break;

                    // A region of size 0 is the whole level.
                    if (to.Width != 0 && to.Height != 0)
                        (width, height) = (to.Width, to.Height);
                    else
                        (width, height) = (Math.Max(1, width >> (int)to.Level), Math.Max(1, height >> (int)to.Level));

                    SDL.GPUBlitInfo blit = new()
                    {
                        Source = new SDL.GPUBlitRegion { Texture = _textures[command.Texture.Id].Handle, X = (uint)command.Rect.X, Y = (uint)command.Rect.Y, W = (uint)command.Rect.Width, H = (uint)command.Rect.Height },
                        Destination = new SDL.GPUBlitRegion { Texture = destination, MipLevel = to.Level, LayerOrDepthPlane = to.Layer, X = to.X, Y = to.Y, W = width, H = height },
                        LoadOp = SDL.GPULoadOp.DontCare,
                        Filter = SDL.GPUFilter.Linear,
                    };
                    SDL.BlitGPUTexture(commandBuffer, in blit);
                    if (to.Texture.IsWindow)
                        _presented[to.Texture.WindowHandle] = command.Texture;
                    break;
                }
            }
        }

        return waited;
    }

    /// <summary>A texture's SDL object and size; a window's swapchain image is acquired the first time the frame names it (0 when there is none).</summary>
    private (nint Texture, uint Width, uint Height) ResolveTexture(nint commandBuffer, GpuTexture texture, ref long waited)
    {
        if (!texture.IsWindow)
        {
            TextureObject stored = _textures[texture.Id];
            return (stored.Handle, stored.Width, stored.Height);
        }

        uint id = texture.WindowHandle;
        if (_swapchains.TryGetValue(id, out (nint Texture, uint Width, uint Height) acquired))
            return acquired;

        nint window = Gpu.ClaimWindow(id);
        if (window != 0)
        {
            long waiting = Stopwatch.GetTimestamp();
            bool ok = SDL.WaitAndAcquireGPUSwapchainTexture(commandBuffer, window, out nint swapchain, out uint width, out uint height);
            waited += Stopwatch.GetTimestamp() - waiting;
            if (!ok)
                Debugging.Log.Warn($"Swapchain acquire for window {id} failed: {SDL.GetError()}");
            else
                acquired = (swapchain, width, height); // 0 when minimised or occluded: nothing to draw into this frame
        }

        _swapchains[id] = acquired;
        return acquired;
    }

    /// <summary>What was released since the last submit leaves the handle tables; the SDL objects go once no frame in flight uses them.</summary>
    private void FreeReleased()
    {
        foreach ((GpuDevice.Kind kind, uint id) in _releasing)
        {
            switch (kind)
            {
                case GpuDevice.Kind.Buffer:
                    Gpu.Defer(kind, _buffers.Free(id));
                    break;
                case GpuDevice.Kind.Texture:
                    Gpu.Defer(kind, _textures.Free(id).Handle);

                    // The id is used again: a window that showed this texture has nothing to read back until the next blit.
                    foreach ((uint window, GpuTexture shown) in _presented)
                    {
                        if (shown.Id == id)
                            _presented.Remove(window); // removing while enumerating a Dictionary is allowed
                    }
                    break;
                case GpuDevice.Kind.Sampler:
                    Gpu.Defer(kind, _samplers.Free(id));
                    break;
                default:
                    Gpu.Defer(kind, _pipelines.Free(id).Handle);
                    break;
            }
        }

        _releasing.Clear();
    }

    private enum TransferKind : byte { Buffer, Texture, TextureCopy }

    /// <summary>One queued upload (staged bytes into a buffer or a texture region) or texture copy (<see cref="Source"/> into <see cref="Region"/>).</summary>
    private readonly record struct Transfer(TransferKind Kind, nint Staged, uint StagedOffset, GpuBuffer Buffer, uint BufferOffset, uint Size, TextureRegion Region, TextureRegion Source);

    private readonly record struct TextureObject(nint Handle, uint Width, uint Height);

    private readonly record struct PipelineObject(nint Handle, bool Compute);
}

/// <summary>SDL objects by handle id: dense, id 0 never used, freed ids used again.</summary>
internal sealed class HandleTable<T> where T : struct
{
    private readonly List<T> _items = [default];
    private readonly List<bool> _alive = [false];
    private readonly Stack<uint> _free = [];

    public T this[uint id] => id < _items.Count ? _items[(int)id] : default;

    public IEnumerable<T> Live => _items.Where((_, i) => _alive[i]);

    public uint Add(T item)
    {
        if (_free.TryPop(out uint id))
        {
            _items[(int)id] = item;
            _alive[(int)id] = true;
            return id;
        }

        _items.Add(item);
        _alive.Add(true);
        return (uint)(_items.Count - 1);
    }

    /// <summary>Forgets the id, handing back what it held.</summary>
    public T Free(uint id)
    {
        if (id == 0 || id >= _items.Count || !_alive[(int)id])
            return default;

        T item = _items[(int)id];
        _items[(int)id] = default;
        _alive[(int)id] = false;
        _free.Push(id);
        return item;
    }
}
