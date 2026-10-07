using System.Buffers;

namespace DeferredRendererGem;

/// <summary>
/// The frame's lights and glows into their buffers, whole, every frame: what the lighting passes read as
/// <c>Lights</c> and the glow pass as <c>Glows</c>. Nothing here decides which light reaches what.
/// </summary>
internal static class Lights
{
    /// <summary>
    /// The frame's point and spot lights into the lights buffer, in the order they come and all of them: the count
    /// the frame constants carry (<see cref="LightingConstants.From"/>) is how many rows this wrote. Each says whether
    /// it may cast a shadow; the shadow passes decide which do.
    /// </summary>
    public static void UploadLights(RenderContext ctx, ReadOnlySpan<LightInstance> lights)
    {
        GpuLight[] rows = ArrayPool<GpuLight>.Shared.Rent(Math.Max(1, lights.Length));
        int count = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].Kind != LightKind.Directional)
                rows[count++] = GpuLight.From(lights[i]);
        }

        if (count > 0)
            ctx.Lights.Upload<GpuLight>(ctx.Gpu, rows.AsSpan(0, count));

        ArrayPool<GpuLight>.Shared.Return(rows);
    }

    /// <summary>
    /// The frame's glows (<see cref="Magic.Contexts.Components.Glow"/>), one <see cref="GpuGlow"/> row each; the
    /// count is what the glow pass draws six vertices per row of.
    /// </summary>
    public static void UploadGlows(RenderContext ctx, ReadOnlySpan<GpuGlow> glows)
    {
        ctx.GlowCount = (uint)glows.Length;
        if (glows.IsEmpty)
            return;

        ctx.Glows.Upload(ctx.Gpu, glows);
    }
}
