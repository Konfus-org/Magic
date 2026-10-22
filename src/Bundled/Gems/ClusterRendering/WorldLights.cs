namespace Magic.ClusterRendering;

/// <summary>
/// Every point and spot light in the world as the GPU culls them, a row per entity slot (<see cref="GpuWorldLight"/>),
/// written when a light changes and at no other time: the views find which reach them, sort them by depth and bin them
/// on the GPU each frame, however many there are. Render thread.
/// </summary>
internal sealed class WorldLights(GpuResources resources)
{
    private readonly List<int> _changed = [];
    private readonly GpuTable<GpuWorldLight> _rows = resources.MakeTable<GpuWorldLight>(GpuContract.WorldLights, 256);

    /// <summary>
    /// A bit for each slot that changed this frame (<see cref="Changed"/>), by which the shadow pages a light casts
    /// into are drawn again; cleared the next frame.
    /// </summary>
    private readonly GpuTable<uint> _changedBits = resources.MakeTable<uint>(GpuContract.ChangedLights, 64);

    /// <summary>
    /// The slots there are, a GPU thread each where the views cull them.
    /// </summary>
    internal int Capacity => _rows.Capacity;

    /// <summary>
    /// Words of the bitmask of the lights that changed.
    /// </summary>
    internal int ChangedWords => _changedBits.Capacity;

    /// <summary>
    /// The slots of the lights that came, went, moved or changed this frame, whose shadows are drawn again.
    /// </summary>
    internal IReadOnlyList<int> Changed => _changed;

    /// <summary>
    /// Takes in the lights that changed since the last frame into the lights' table.
    /// </summary>
    internal void Update(Renderables copy)
    {
        foreach (int slot in _changed)
        {
            _changedBits.Set(slot >> 5, 0);
        }

        _changed.Clear();
        Delta<LightCopy> lights = copy.Lights;
        foreach (int slot in lights.Removed)
        {
            _rows.Set(slot, default);
            _changed.Add(slot);
        }

        foreach (ref readonly Delta<LightCopy>.Change change in lights.Changed)
        {
            (int slot, LightCopy light) = change;
            _rows.Set(
                slot,
                new GpuWorldLight
                {
                    Position = light.Position,
                    Range = light.Range,
                    Radius = light.Radius,
                    Intensity = light.Intensity,
                    SpotScale = light.SpotScale,
                    Forward = light.Forward,
                    SpotOffset = light.SpotOffset,
                    Flags = (uint)(GpuWorldLightFlags.Live
                        | (light.IsSpot ? GpuWorldLightFlags.Spot : GpuWorldLightFlags.None)
                        | (light.Shadowless ? GpuWorldLightFlags.Shadowless : GpuWorldLightFlags.None)),
                });
            _changed.Add(slot);
        }

        _changedBits.Grow((Capacity + 31) / 32);
        foreach (int slot in _changed)
        {
            _changedBits.Set(slot >> 5, _changedBits.Rows[slot >> 5] | (1u << (slot & 31)));
        }
    }
}
