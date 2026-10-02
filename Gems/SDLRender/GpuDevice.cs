using Magic.Contexts.Settings;
using Magic.Interfaces;
using Magic.Utils;
using SDL3;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SDLRenderGem;

/// <summary>
/// The SDL GPU device and its frames: the one owner of frame fences (<see cref="Begin"/> waits for the frame that last used
/// this frame's slot and releases what was freed during it; <see cref="Submit"/> puts the new fence in the slot), deferred
/// release of every object, the windows claimed for it, and synchronous readback. Every SDL GPU call belongs to the thread
/// that created the device (<see cref="AssertMainThread"/>).
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    /// <summary>Frames the CPU may record ahead of the GPU; SDL is told the same.</summary>
    public const int FramesInFlight = 2;

    private readonly int _mainThread = Environment.CurrentManagedThreadId;
    private readonly RenderSettings _settings;
    private readonly Dictionary<uint, nint> _claimed = [];
    private readonly InFlight[] _slots = [new(), new()];
    private readonly List<(Kind Kind, nint Handle)> _released = []; // freed since the last submit
    private long _frameNumber;
    private bool _vsync;

    public GpuDevice(RenderSettings settings, bool debug, IFileSystem files, string gemsDirectory)
    {
        _settings = settings;
        _vsync = settings.Vsync;

        // dxcompiler.dll loads dxil.dll by bare name and, without it, produces unsigned DXIL that D3D12 rejects; the gem
        // folder is not on the search path, so it is loaded by full path first.
        string dxil = files.Combine(gemsDirectory, "dxil.dll");
        if (files.FileExists(dxil))
            NativeLibrary.Load(dxil);
        else
            Debugging.Log.Warn($"dxil.dll not found at {dxil}; DXIL shaders will be unsigned.");

        if (!ShaderCross.Init())
            throw new InvalidOperationException($"SDL_shadercross failed to initialise: {SDL.GetError()}");

        uint props = SDL.CreateProperties();
        try
        {
            SDL.SetBooleanProperty(props, SDL.Props.GPUDeviceCreateShadersSPIRVBoolean, true);
            SDL.SetBooleanProperty(props, SDL.Props.GPUDeviceCreateShadersDXILBoolean, true);
            SDL.SetBooleanProperty(props, SDL.Props.GPUDeviceCreateDebugModeBoolean, debug);
            if (!string.IsNullOrEmpty(settings.Backend))
                SDL.SetStringProperty(props, SDL.Props.GPUDeviceCreateNameString, settings.Backend);
            Handle = SDL.CreateGPUDeviceWithProperties(props);
        }
        finally
        {
            SDL.DestroyProperties(props);
        }

        if (Handle == 0)
            throw new InvalidOperationException($"SDL_CreateGPUDevice failed: {SDL.GetError()}");

        SDL.GPUShaderFormat formats = SDL.GetGPUShaderFormats(Handle);
        if (formats.HasFlag(SDL.GPUShaderFormat.SPIRV))
            ShaderFormat = SDL.GPUShaderFormat.SPIRV;
        else if (formats.HasFlag(SDL.GPUShaderFormat.DXIL))
            ShaderFormat = SDL.GPUShaderFormat.DXIL;
        else
            throw new InvalidOperationException($"the GPU device takes neither SPIR-V nor DXIL (formats: {formats}).");

        Driver = SDL.GetGPUDeviceDriver(Handle) ?? "?";
        uint deviceProps = SDL.GetGPUDeviceProperties(Handle);
        Name = deviceProps != 0 ? SDL.GetStringProperty(deviceProps, SDL.Props.GPUDeviceNameString, "?") : "?";
        SDL.GPUTextureUsageFlags depthUsage = SDL.GPUTextureUsageFlags.DepthStencilTarget | SDL.GPUTextureUsageFlags.Sampler;
        DepthFormat = SDL.GPUTextureSupportsFormat(Handle, SDL.GPUTextureFormat.D32Float, SDL.GPUTextureType.TextureType2D, depthUsage)
            ? SDL.GPUTextureFormat.D32Float
            : SDL.GPUTextureFormat.D24Unorm;

        SDL.SetGPUAllowedFramesInFlight(Handle, FramesInFlight);
        Staging = new Staging(this);

        Debugging.Log.Info($"GPU device: {Name} ({Driver}), shaders {ShaderFormat}, depth {DepthFormat}, debug {debug}, on thread {_mainThread}.");
    }

    public void Dispose()
    {
        if (Handle == 0)
            return;

        Staging.Dispose();
        WaitIdle();

        foreach (InFlight slot in _slots)
        {
            if (slot.Fence != 0)
                SDL.ReleaseGPUFence(Handle, slot.Fence);
            foreach ((Kind kind, nint handle) in slot.Released)
                Release(kind, handle);
            slot.Released.Clear();
        }

        foreach ((Kind kind, nint handle) in _released)
            Release(kind, handle);
        _released.Clear();

        foreach (nint window in _claimed.Values)
            SDL.ReleaseWindowFromGPUDevice(Handle, window);
        _claimed.Clear();

        SDL.DestroyGPUDevice(Handle);
        Handle = 0;
        ShaderCross.Quit();
    }

    public nint Handle { get; private set; }

    public SDL.GPUShaderFormat ShaderFormat { get; }

    public string Driver { get; }

    public string Name { get; }

    public SDL.GPUTextureFormat DepthFormat { get; }

    /// <summary>Where uploads are written until the next submit's copy pass moves them.</summary>
    public Staging Staging { get; }

    [Conditional("DEBUG")]
    public void AssertMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException($"SDL GPU calls belong to thread {_mainThread}; this is thread {Environment.CurrentManagedThreadId}.");
    }

    /// <summary>Throws with SDL's error text when an SDL call reported failure.</summary>
    public static void ThrowOnError(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException($"{what} failed: {SDL.GetError()}");
    }

    public static nint ThrowOnError(nint handle, string what)
    {
        if (handle == 0)
            throw new InvalidOperationException($"{what} failed: {SDL.GetError()}");
        return handle;
    }

    /// <summary>
    /// Starts a frame: waits until the frame that last used this slot is done, releases what was freed during it, and
    /// returns a command buffer. Every <see cref="Begin"/> is followed by one <see cref="Submit"/>.
    /// </summary>
    public nint BeginFrame()
    {
        AssertMainThread();
        if (_vsync != _settings.Vsync)
            ChangeVsync();

        InFlight slot = _slots[_frameNumber % FramesInFlight];
        if (slot.Fence != 0)
        {
            Span<nint> fences = [slot.Fence];
            ThrowOnError(SDL.WaitForGPUFences(Handle, true, fences, 1), "SDL_WaitForGPUFences");
            SDL.ReleaseGPUFence(Handle, slot.Fence);
            slot.Fence = 0;
        }

        foreach ((Kind kind, nint handle) in slot.Released)
            Release(kind, handle);
        slot.Released.Clear();

        return ThrowOnError(SDL.AcquireGPUCommandBuffer(Handle), "SDL_AcquireGPUCommandBuffer");
    }

    /// <summary>Submits the frame; its fence, and everything freed since the last submit, go into its slot.</summary>
    public void Submit(nint commandBuffer)
    {
        Staging.Unmap();
        nint fence = SDL.SubmitGPUCommandBufferAndAcquireFence(commandBuffer);
        if (fence == 0)
            Debugging.Log.Error($"SDL_SubmitGPUCommandBuffer failed: {SDL.GetError()}");

        InFlight slot = _slots[_frameNumber % FramesInFlight];
        slot.Fence = fence;
        slot.Released.AddRange(_released);
        _released.Clear();
        _frameNumber++;
    }

    public void WaitIdle()
    {
        SDL.WaitForGPUIdle(Handle);
    }

    /// <summary>
    /// The native window for an <see cref="Magic.Interfaces.IWindow.Handle"/>, claimed for this device the first time; 0
    /// when SDL no longer knows the id (the window closed: the claim is forgotten with it).
    /// </summary>
    public nint ClaimWindow(uint id)
    {
        AssertMainThread();
        nint window = SDL.GetWindowFromID(id);
        if (window == 0)
        {
            _claimed.Remove(id);
            return 0;
        }

        if (_claimed.TryGetValue(id, out nint claimed) && claimed == window)
            return claimed;

        ThrowOnError(SDL.ClaimWindowForGPUDevice(Handle, window), "SDL_ClaimWindowForGPUDevice");
        SDL.GPUPresentMode mode = SetPresentMode(window);

        _claimed[id] = window;
        Debugging.Log.Verbose($"Window {id} claimed for the GPU device ({mode}, swapchain {SDL.GetGPUSwapchainTextureFormat(Handle, window)}).");
        return window;
    }

    public nint CreateTransferBuffer(SDL.GPUTransferBufferUsage usage, uint size)
    {
        SDL.GPUTransferBufferCreateInfo info = new() { Usage = usage, Size = size };
        return ThrowOnError(SDL.CreateGPUTransferBuffer(Handle, in info), "SDL_CreateGPUTransferBuffer");
    }

    /// <summary>Released once the frames in flight now are done with it: it goes with the next submit's fence.</summary>
    public void Defer(Kind kind, nint handle)
    {
        if (handle != 0)
            _released.Add((kind, handle));
    }

    /// <summary>
    /// Copies GPU memory back, synchronously: <paramref name="download"/> records the copy into the transfer buffer, this
    /// submits, waits and returns the bytes. A screenshot, a probe or a debug check can afford it.
    /// </summary>
    public unsafe byte[] Read(uint bytes, Action<nint, nint> download)
    {
        AssertMainThread();

        nint transfer = CreateTransferBuffer(SDL.GPUTransferBufferUsage.Download, bytes);
        try
        {
            nint commandBuffer = ThrowOnError(SDL.AcquireGPUCommandBuffer(Handle), "SDL_AcquireGPUCommandBuffer");
            nint copyPass = SDL.BeginGPUCopyPass(commandBuffer);
            download(copyPass, transfer);
            SDL.EndGPUCopyPass(copyPass);

            nint fence = ThrowOnError(SDL.SubmitGPUCommandBufferAndAcquireFence(commandBuffer), "SDL_SubmitGPUCommandBufferAndAcquireFence");
            Span<nint> fences = [fence];
            ThrowOnError(SDL.WaitForGPUFences(Handle, true, fences, 1), "SDL_WaitForGPUFences");
            SDL.ReleaseGPUFence(Handle, fence);

            nint mapped = ThrowOnError(SDL.MapGPUTransferBuffer(Handle, transfer, false), "SDL_MapGPUTransferBuffer");
            byte[] result = GC.AllocateUninitializedArray<byte>((int)bytes);
            new ReadOnlySpan<byte>((void*)mapped, (int)bytes).CopyTo(result);
            SDL.UnmapGPUTransferBuffer(Handle, transfer);
            return result;
        }
        finally
        {
            SDL.ReleaseGPUTransferBuffer(Handle, transfer);
        }
    }

    public void Release(Kind kind, nint handle)
    {
        switch (kind)
        {
            case Kind.Buffer: SDL.ReleaseGPUBuffer(Handle, handle); break;
            case Kind.Texture: SDL.ReleaseGPUTexture(Handle, handle); break;
            case Kind.TransferBuffer: SDL.ReleaseGPUTransferBuffer(Handle, handle); break;
            case Kind.Sampler: SDL.ReleaseGPUSampler(Handle, handle); break;
            case Kind.GraphicsPipeline: SDL.ReleaseGPUGraphicsPipeline(Handle, handle); break;
            case Kind.ComputePipeline: SDL.ReleaseGPUComputePipeline(Handle, handle); break;
        }
    }

    /// <summary>Waits for the GPU and releases everything still deferred; the caller has released its own objects first.</summary>
    /// <summary>
    /// <see cref="RenderSettings.Vsync"/> changed (the settings window): every claimed window's swapchain follows. Called
    /// between frames, where SDL allows it.
    /// </summary>
    private void ChangeVsync()
    {
        _vsync = _settings.Vsync;
        foreach ((uint id, nint window) in _claimed)
            Debugging.Log.Info($"Window {id} now presents with {SetPresentMode(window)}.");
    }

    /// <summary>
    /// Vsync, or when it is off, uncapped: Immediate (tearing allowed) when the backend has it, else Mailbox, which D3D12
    /// still paces to the display's refresh. Returns the mode asked for.
    /// </summary>
    private SDL.GPUPresentMode SetPresentMode(nint window)
    {
        SDL.GPUPresentMode mode = _vsync ? SDL.GPUPresentMode.VSync
            : SDL.WindowSupportsGPUPresentMode(Handle, window, SDL.GPUPresentMode.Immediate) ? SDL.GPUPresentMode.Immediate
            : SDL.WindowSupportsGPUPresentMode(Handle, window, SDL.GPUPresentMode.Mailbox) ? SDL.GPUPresentMode.Mailbox
            : SDL.GPUPresentMode.VSync;
        if (!SDL.SetGPUSwapchainParameters(Handle, window, SDL.GPUSwapchainComposition.SDR, mode))
            Debugging.Log.Warn($"Could not set the swapchain to {mode}: {SDL.GetError()}");

        return mode;
    }

    public enum Kind : byte { Buffer, Texture, TransferBuffer, Sampler, GraphicsPipeline, ComputePipeline }

    /// <summary>One frame slot: the fence of the frame that last used it and what was freed while it was recorded.</summary>
    private sealed class InFlight
    {
        public nint Fence { get; set; }

        public List<(Kind Kind, nint Handle)> Released { get; } = [];
    }
}

/// <summary>
/// Staging memory for uploads: one transfer buffer, mapped on the first stage of a frame (cycled, so SDL hands out fresh
/// memory while a frame in flight still reads the old) and unmapped at submit. Ranges are handed out one after the other;
/// when a frame needs more, a buffer twice the size (or what the stage needs) replaces it on the spot and stays for the
/// frames after, the old one released with the frame.
/// </summary>
internal sealed class Staging : IDisposable
{
    private const uint InitialBytes = 1024 * 1024;

    private readonly GpuDevice _device;
    private nint _buffer;
    private uint _capacity;
    private nint _mapped;
    private uint _used;

    public Staging(GpuDevice device)
    {
        _device = device;
        _capacity = InitialBytes;
        _buffer = device.CreateTransferBuffer(SDL.GPUTransferBufferUsage.Upload, _capacity);
    }

    public void Dispose()
    {
        Unmap();
        _device.Defer(GpuDevice.Kind.TransferBuffer, _buffer);
        _buffer = 0;
    }

    /// <summary>Copies <paramref name="data"/> in; the transfer buffer and offset a copy pass reads it from, valid until the frame is submitted.</summary>
    public unsafe (nint TransferBuffer, uint Offset) Stage(ReadOnlySpan<byte> data)
    {
        _device.AssertMainThread();

        uint bytes = (uint)data.Length;
        uint aligned = (_used + 15u) & ~15u;
        if (_mapped != 0 && aligned + bytes > _capacity)
            Grow(aligned + bytes);

        if (_mapped == 0)
        {
            _mapped = GpuDevice.ThrowOnError(SDL.MapGPUTransferBuffer(_device.Handle, _buffer, true), "SDL_MapGPUTransferBuffer");
            _used = aligned = 0;
            if (bytes > _capacity)
                Grow(bytes);
        }

        data.CopyTo(new Span<byte>((void*)(_mapped + (nint)aligned), data.Length));
        _used = aligned + bytes;
        return (_buffer, aligned);
    }

    /// <summary>End of the frame's staging, before submit.</summary>
    public void Unmap()
    {
        if (_mapped == 0)
            return;

        SDL.UnmapGPUTransferBuffer(_device.Handle, _buffer);
        _mapped = 0;
    }

    /// <summary>A bigger buffer for the rest of this frame and the frames after; what was staged stays in the old one until it is done.</summary>
    private void Grow(uint needed)
    {
        Unmap();
        _device.Defer(GpuDevice.Kind.TransferBuffer, _buffer);
        _capacity = BitOperations.RoundUpToPowerOf2(Math.Max(needed, _capacity * 2));
        _buffer = _device.CreateTransferBuffer(SDL.GPUTransferBufferUsage.Upload, _capacity);
        _mapped = GpuDevice.ThrowOnError(SDL.MapGPUTransferBuffer(_device.Handle, _buffer, false), "SDL_MapGPUTransferBuffer");
        _used = 0;
        Debugging.Log.Verbose($"Staging grown to {_capacity / 1024} KiB.");
    }
}
