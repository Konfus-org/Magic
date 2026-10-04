using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// The engine's own pipelines by the shaders they are built from, so a changed built-in shader, or an include it reaches,
/// rebuilds them the way a material's pipelines rebuild: in the frame the new text arrives, on the render thread (a
/// hitch while developing shaders), the old pipelines kept and the compiler's message logged when the new text does
/// not compile.
/// </summary>
internal static class BuiltIns
{
    /// <summary>
    /// Registers a family of pipelines: <paramref name="rebuild"/> makes them again when any of <paramref name="paths"/> changes.
    /// </summary>
    public static void Register(RenderContext ctx, Action<RenderContext> rebuild, params ReadOnlySpan<string> paths)
    {
        ulong[] ids = new ulong[paths.Length];
        for (int i = 0; i < paths.Length; i++)
            ids[i] = Shaders.IdOf(ctx, paths[i]);

        ctx.BuiltIns.Add((ids, rebuild));
    }

    /// <summary>
    /// Rebuilds every family one of <paramref name="changed"/> is built from. Called while the changed shaders' text is in hand.
    /// </summary>
    public static void Rebuild(RenderContext ctx, IReadOnlySet<ulong> changed)
    {
        foreach ((ulong[] ids, Action<RenderContext> rebuild) in ctx.BuiltIns)
        {
            bool affected = false;
            foreach (ulong id in ids)
                affected |= changed.Contains(id);
            if (!affected)
                continue;

            try
            {
                rebuild(ctx);
            }
            catch (InvalidOperationException ex)
            {
                Debugging.Log.Error($"A built-in shader changed but does not compile; the old pipelines stay: {ex.Message}");
            }
        }
    }
}
