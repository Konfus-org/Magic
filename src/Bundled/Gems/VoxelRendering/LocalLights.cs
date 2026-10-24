using System.Numerics;

namespace Magic.VoxelRendering;

/// <summary>
/// The world's point and spot lights as the GPU lights by them, kept up to date from the copy's changes: a row per
/// entity slot (<see cref="GpuLocalLight"/>), written when its light changes; a slot whose light is gone is written as
/// one that reaches nowhere, which every view's culling leaves out. Render thread.
/// </summary>
internal sealed class LocalLights(GpuTables tables)
{
    /// <summary>
    /// Takes in what changed among the lights since the last frame into the lights' table.
    /// </summary>
    internal void Update(Renderables copy)
    {
        Delta<LocalLightCopy> lights = copy.Lights;
        tables.LocalLights.Grow(lights.Length);
        foreach (int slot in lights.Removed)
        {
            tables.LocalLights.Set(slot, default);
        }

        foreach (Delta<LocalLightCopy>.Change change in lights.Changed)
        {
            tables.LocalLights.Set(change.Slot, ToRow(change.Value));
        }
    }

    /// <summary>
    /// A light's row: a spot's fade runs from its outer cone's cosine to its inner's (Lagarde &amp; de Rousiers 2014), a
    /// point light's is always whole.
    /// </summary>
    private static GpuLocalLight ToRow(in LocalLightCopy light)
    {
        bool isSpot = light.CosOuter > -1;
        float scale = isSpot ? 1 / MathF.Max(light.CosInner - light.CosOuter, 1e-4f) : 0;

        return new GpuLocalLight
        {
            Position = light.Transform.Position,
            Range = MathF.Max(light.Range, 0),
            Intensity = light.Intensity,
            Radius = MathF.Max(light.Radius, 0),
            Forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, light.Transform.Rotation)),
            SpotScale = scale,
            SpotOffset = isSpot ? -light.CosOuter * scale : 1,
            Flags = (uint)(light.IsShadowless ? GpuLocalLightFlags.Shadowless : GpuLocalLightFlags.None),
        };
    }
}
