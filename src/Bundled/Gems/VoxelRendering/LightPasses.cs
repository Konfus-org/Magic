using System.Drawing;
using System.Runtime.InteropServices;
using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Contexts.Rendering;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The renderer's step for the point and spot lights (<see cref="IRendering.CullLights"/>): each view's lights culled,
/// sorted by depth and binned by depth slice and tile (Shaders/Renderer/Lights.slang) into buffers of its own, kept from
/// frame to frame and made again when the view's size or the lights it holds change. A view no frame culled is let go
/// as the frame ends. Render thread.
/// </summary>
internal sealed class LightPasses : IDisposable
{
    // Lights.slang's tile size, depth slices and the most visible lights its sort takes.
    private const int Tile = 16;
    private const uint Bins = 1024;
    private const int MaxVisible = 4096;

    // The counts: found, the groups done, visible; and the bytes of a dispatch's groups.
    private const uint Counts = 3;
    private const uint DispatchBytes = 12;

    /// <summary>
    /// Groups a dispatch runs along x at most, as every API allows: Core/Compute's MaxGroupsX.
    /// </summary>
    private const uint MaxGroupsX = 65535;

    private const GpuBufferUsage Usage = GpuBufferUsage.ComputeRead
        | GpuBufferUsage.ComputeWrite
        | GpuBufferUsage.Arguments;

    private readonly IGpuDriver _gpu;
    private readonly Passes _passes;
    private readonly Dictionary<int, ViewBuffers> _views = [];
    private readonly HashSet<int> _drawn = [];
    private readonly List<int> _gone = [];
    private readonly ShaderEntry _binLights;
    private readonly ShaderEntry _buildLightTiles;

    // The lights a view holds, and the slots of the world's lights, for the frame.
    private uint _capacity;
    private uint _slots;

    internal LightPasses(IGpuDriver gpu, Passes passes, Assets assets)
    {
        _gpu = gpu;
        _passes = passes;
        Handle<Shader> module = assets.Find<Shader>("Shaders/Renderer/Lights.slang");
        _binLights = new ShaderEntry(module, "BinLights");
        _buildLightTiles = new ShaderEntry(module, "BuildLightTiles");
    }

    public void Dispose()
    {
        foreach (ViewBuffers view in _views.Values)
        {
            view.Release(_gpu);
        }
    }

    /// <summary>
    /// Starts a frame whose views hold <paramref name="capacity"/> visible lights each, rounded up to a whole word of
    /// a tile's bits, at most what the sort takes, of the world's <paramref name="slots"/>.
    /// </summary>
    internal void BeginFrame(int capacity, int slots)
    {
        _capacity = (uint)((Math.Clamp(capacity, 1, MaxVisible) + 31) / 32 * 32);
        _slots = (uint)slots;
        _drawn.Clear();
    }

    /// <summary>
    /// Ends the frame: the buffers of each view it did not cull are let go.
    /// </summary>
    internal void EndFrame()
    {
        _gone.Clear();
        foreach (int id in _views.Keys)
        {
            if (!_drawn.Contains(id)) _gone.Add(id);
        }

        foreach (int id in _gone)
        {
            _views[id].Release(_gpu);
            _views.Remove(id);
        }
    }

    /// <summary>
    /// Culls the view's lights: a thread a light slot, or a slice or word of the tiles to empty, whichever is more; then
    /// a group a visible light, as many as the first pass's last group wrote.
    /// </summary>
    internal LightList Cull(RenderView view)
    {
        ViewBuffers b = GetBuffers(view);
        LightList list = new(b.Lights, b.Bins, b.Tiles);
        LightCulling c = new(list, b.Keys, b.Visible, b.Counts, b.Args);
        // Groups past the most a dispatch runs along x go on in y (Core/Compute's GetLinearGroup).
        uint groups = (Math.Max(Math.Max(_slots, Bins), b.TileWords) + 63) / 64;
        _passes.Dispatch(
            view,
            _binLights,
            c,
            new GpuGroups(Math.Min(groups, MaxGroupsX), (groups + MaxGroupsX - 1) / MaxGroupsX),
            [],
            [b.Lights, b.Bins, b.Tiles, b.Keys, b.Visible, b.Counts, b.Args]);
        _passes.Dispatch(
            view,
            _buildLightTiles,
            c,
            b.Args,
            0,
            [b.Lights, b.Counts],
            [b.Bins, b.Tiles]);

        return list;
    }

    /// <summary>
    /// The view's buffers, made again when its size or the lights it holds changed; the view counts as culled this
    /// frame.
    /// </summary>
    private ViewBuffers GetBuffers(RenderView view)
    {
        _drawn.Add(view.Id);
        if (_views.TryGetValue(view.Id, out ViewBuffers? kept) && kept.Size == view.Size && kept.Capacity == _capacity)
            return kept;

        kept?.Release(_gpu);
        uint tiles = (uint)(((view.Size.Width + Tile - 1) / Tile) * ((view.Size.Height + Tile - 1) / Tile));
        uint tileWords = tiles * (_capacity / 32);
        ViewBuffers made = new(view.Size, _capacity, tileWords)
        {
            Lights = _gpu.CreateZeroedBuffer(Usage, _capacity * 4),
            Bins = _gpu.CreateZeroedBuffer(Usage, Bins * 2 * 4),
            Tiles = _gpu.CreateZeroedBuffer(Usage, tileWords * 4),
            Keys = _gpu.CreateZeroedBuffer(Usage, _capacity * 4),
            Visible = _gpu.CreateZeroedBuffer(Usage, _capacity * 4),
            Counts = _gpu.CreateZeroedBuffer(Usage, Counts * 4),
            Args = _gpu.CreateZeroedBuffer(Usage, DispatchBytes),
        };
        _views[view.Id] = made;

        return made;
    }

    /// <summary>
    /// The light passes' constants, as Lights.slang's LightCullConstants.
    /// </summary>
    [ShaderStruct("Shaders/Renderer/Lights.slang", "LightCullConstants")]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LightCulling(
        LightList List,
        GpuBuffer Keys,
        GpuBuffer Visible,
        GpuBuffer Counts,
        GpuBuffer Args);

    /// <summary>
    /// One view's light buffers, for the size and the lights they were made for, and how many words its tiles hold.
    /// </summary>
    private sealed class ViewBuffers(Size size, uint capacity, uint tileWords)
    {
        internal Size Size { get; } = size;

        internal uint Capacity { get; } = capacity;

        internal uint TileWords { get; } = tileWords;

        internal required GpuBuffer Lights { get; init; }

        internal required GpuBuffer Bins { get; init; }

        internal required GpuBuffer Tiles { get; init; }

        internal required GpuBuffer Keys { get; init; }

        internal required GpuBuffer Visible { get; init; }

        internal required GpuBuffer Counts { get; init; }

        internal required GpuBuffer Args { get; init; }

        internal void Release(IGpuDriver gpu)
        {
            foreach (GpuBuffer buffer in (ReadOnlySpan<GpuBuffer>)[Lights, Bins, Tiles, Keys, Visible, Counts, Args])
            {
                gpu.Release(buffer);
            }
        }
    }
}
