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
    }

    public GpuArray<GpuSceneLight> Rows { get; } = new(gpu, GpuBufferUsage.ComputeRead, 256);

    /// <summary>
    /// The slots there are, a GPU thread each where the views cull them.
    /// </summary>
    public int Capacity => Rows.Capacity;

    /// <summary>
    /// The slots of the lights that came, went, moved or changed this frame, whose shadows are drawn again.
    /// </summary>
    public IReadOnlyList<int> Changed => _changed;

    /// <summary>
    /// Takes in the lights that changed since the last frame, then uploads them.
    /// </summary>
    public void Update(Snapshot snapshot)
    {
        _changed.Clear();
        Slots<LightCopy> lights = snapshot.Lights;
        foreach (int slot in lights.Removed)
        {
            Rows.Set(slot, default);
            _changed.Add(slot);
        }

        foreach (int slot in lights.Changed)
        {
            LightCopy light = lights[slot];
            Vector3 high = light.Position.ToVector3();
            Rows.Set(slot, new GpuSceneLight
            {
                High = high,
                Range = light.Range,
                Low = (light.Position - high).ToVector3(),
                Radius = light.Radius,
                Intensity = light.Intensity,
                SpotScale = light.SpotScale,
                Forward = light.Forward,
                SpotOffset = light.SpotOffset,
                Flags = (uint)(GpuSceneLightFlags.Live | (light.IsSpot ? GpuSceneLightFlags.Spot : GpuSceneLightFlags.None) | (light.Shadowless ? GpuSceneLightFlags.Shadowless : GpuSceneLightFlags.None)),
            });
            _changed.Add(slot);
        }

        Rows.Flush();
    }
}

/// <summary>
/// A point or spot light in the world's table (Shaders/Include/Structs.hlsli's GpuSceneLight): where it is, as a float
/// and what it is past that, so it is exact relative to the camera however far from the origin; how far it reaches and
/// the size of its bulb; its intensity in candela times its colour; for a spot its axis and the scale and offset of its
/// cone's falloff; and <see cref="GpuSceneLightFlags"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneLight
{
    public Vector3 High;
    public float Range;
    public Vector3 Low;
    public float Radius;
    public Vector3 Intensity;
    public float SpotScale;
    public Vector3 Forward;
    public float SpotOffset;
    public uint Flags;
    public uint Padding0;
    public uint Padding1;
    public uint Padding2;
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
