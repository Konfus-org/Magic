using System.Numerics;
using System.Runtime.InteropServices;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// Every point and spot light in the world as the GPU culls them, a row per entity slot (<see cref="GpuSceneLight"/>),
/// written when a light changes and at no other time: the views find which reach them, sort them by depth and bin them
/// on the GPU each frame, however many there are. Render thread.
/// </summary>
internal sealed class SceneLights(IGpu gpu) : IDisposable
{
    private readonly List<int> _changed = [];

    public void Dispose()
    {
        Rows.Dispose();
        ChangedLights.Dispose();
    }

    internal GpuArray<GpuSceneLight> Rows { get; } = new(gpu, GpuBufferUsage.ComputeRead, 256);

    /// <summary>
    /// A bit for each slot that changed this frame (<see cref="Changed"/>), by which the shadow pages a light casts
    /// into are drawn again; cleared the next frame.
    /// </summary>
    internal GpuArray<uint> ChangedLights { get; } = new(gpu, GpuBufferUsage.ComputeRead, 64);

    /// <summary>
    /// The slots there are, a GPU thread each where the views cull them.
    /// </summary>
    internal int Capacity => Rows.Capacity;

    /// <summary>
    /// The slots of the lights that came, went, moved or changed this frame, whose shadows are drawn again.
    /// </summary>
    internal IReadOnlyList<int> Changed => _changed;

    /// <summary>
    /// Takes in the lights that changed since the last frame, then uploads them.
    /// </summary>
    internal void Update(WorldCopy copy)
    {
        foreach (int slot in _changed)
        {
            ChangedLights.Set(slot >> 5, 0);
        }

        _changed.Clear();
        Delta<LightCopy> lights = copy.Lights;
        foreach (int slot in lights.Removed)
        {
            Rows.Set(slot, default);
            _changed.Add(slot);
        }

        foreach (ref readonly Delta<LightCopy>.Change change in lights.Changed)
        {
            (int slot, LightCopy light) = change;
            Rows.Set(slot, new GpuSceneLight
            {
                Position = light.Position,
                Range = light.Range,
                Radius = light.Radius,
                Intensity = light.Intensity,
                SpotScale = light.SpotScale,
                Forward = light.Forward,
                SpotOffset = light.SpotOffset,
                Flags = (uint)(GpuSceneLightFlags.Live | (light.IsSpot ? GpuSceneLightFlags.Spot : GpuSceneLightFlags.None) | (light.Shadowless ? GpuSceneLightFlags.Shadowless : GpuSceneLightFlags.None)),
            });
            _changed.Add(slot);
        }

        ChangedLights.Grow((Capacity + 31) / 32);
        foreach (int slot in _changed)
        {
            ChangedLights.Set(slot >> 5, ChangedLights.Rows[slot >> 5] | (1u << (slot & 31)));
        }

        Rows.Flush();
        ChangedLights.Flush();
    }
}

/// <summary>
/// A point or spot light in the world's table (Shaders/Include/Structs.hlsli's GpuSceneLight): where it is; how far it
/// reaches; its intensity in candela times its colour; the size of its bulb; for a spot its axis and the scale and
/// offset of its cone's falloff; and <see cref="GpuSceneLightFlags"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneLight
{
    internal Vector3 Position;
    internal float Range;
    internal Vector3 Intensity;
    internal float Radius;
    internal Vector3 Forward;
    internal float SpotScale;
    internal float SpotOffset;
    internal uint Flags;
    internal uint Padding0;
    internal uint Padding1;
}

/// <summary>
/// What a <see cref="GpuSceneLight"/> is.
/// </summary>
[Flags]
internal enum GpuSceneLightFlags : uint
{
    None = 0,

    /// <summary>
    /// There is a light in the slot.
    /// </summary>
    Live = 1,

    Spot = 2,

    /// <summary>
    /// It casts no shadow (Core's <c>PointLight.Shadowless</c>).
    /// </summary>
    Shadowless = 4,
}
