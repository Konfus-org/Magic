using Magic.Utils;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// Says what failed, where it failed: every instance drawn as a failure is reported as an error at its place
/// (<see cref="Debugging.UI.Error"/>, so it shows near a camera while the debug UI's on-screen text does). The glow
/// says something is broken; this says why, in a few words: the log has the rest (which file, the compiler's message). Only failures the CPU decides are here: a material, shader, texture or model that did not
/// load, and a surface that does not compile. A light tile that overflows is known to the GPU alone, which writes its
/// own text (<c>Lighting/Lighting.comp.hlsl</c>).
/// </summary>
internal static class FailureLabels
{
    /// <summary>
    /// Once a frame. Each failure is reported for this frame only, so its text goes when it is repaired; the parts of
    /// one model fail alike and sit in one place, which is one error.
    /// </summary>
    public static void Show(RenderContext ctx)
    {
        if (!Debugging.UI.Enabled)
            return;

        InstanceTable instances = ctx.Instances;
        ReadOnlySpan<GpuInstance> rows = instances.Rows;
        foreach (uint slot in instances.Failed)
            Debugging.UI.Error(Reason(ctx, slot), Center(rows[(int)slot]), seconds: 0f);

        // A surface that does not compile is a failure of its class, not of a material: its instances draw with the
        // forced failure pipeline. Looking for them means looking at every instance, so only while a class is broken.
        if (!AnyBrokenClass(ctx))
            return;

        for (uint slot = 0; slot < instances.HighWater; slot++)
        {
            if (!instances.IsAlive(slot) || !ctx.Pipelines.TryGet(instances.ClassOf(slot), out BuiltPipeline built) || built.Pipeline.IsValid || built.Error is null)
                continue;

            Debugging.UI.Error("Shader does not compile", Center(rows[(int)slot]), seconds: 0f);
        }
    }

    private static Vector3 Center(in GpuInstance row)
    {
        return new Vector3(row.Sphere.X, row.Sphere.Y, row.Sphere.Z);
    }

    /// <summary>
    /// Why the instance at <paramref name="slot"/> is drawn as a failure.
    /// </summary>
    private static string Reason(RenderContext ctx, uint slot)
    {
        InstanceTable instances = ctx.Instances;
        if (instances.MeshSlotOf(slot) == 0)
            return $"Model {instances.ModelOf(slot)} missing";

        MaterialState? state = ctx.Materials.States[instances.Rows[(int)slot].MaterialSlot];
        return state?.Error ?? "Material failed";
    }

    private static bool AnyBrokenClass(RenderContext ctx)
    {
        foreach ((_, BuiltPipeline built) in ctx.Pipelines.Entries)
        {
            if (!built.Pipeline.IsValid && built.Error is not null)
                return true;
        }

        return false;
    }
}
