using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Systems.Rendering;

/// <summary>
/// Entities into the GPU tables: one instance per part of an entity's model, each with its mesh, material and draw group,
/// and the uploads of what changed (instance rows, the cell and page tables, the draw-group template).
/// </summary>
internal static class Instancing
{
    /// <summary>Registers one entity's model (one instance per part, chained) and returns the handle that names it: its first part's slot.</summary>
    public static uint Add(RenderContext ctx, in InstanceDesc desc)
    {
        (uint MeshSlot, int MaterialSlot)[] parts = Meshes.Acquire(ctx, desc.Model);
        InstanceFlags flags = desc.Flags.HasFlag(RenderFlags.NoSizeCull) ? InstanceFlags.NoSizeCull : InstanceFlags.None;
        InstanceTable instances = ctx.Instances;
        uint handle = 0, previous = InstanceTable.End;
        for (int i = 0; i < parts.Length; i++)
        {
            uint meshSlot = parts[i].MeshSlot;
            Handle<Material> source = desc.Materials[Math.Clamp(parts[i].MaterialSlot, 0, MaterialSlots.Capacity - 1)];
            MaterialSlot material = Drawn(ctx, meshSlot, Materials.Acquire(ctx, source));
            uint group = Group(ctx, material.Class, meshSlot);
            uint slot = instances.Add(desc.Model.Id, meshSlot, ctx.Meshes.Bounds(meshSlot), source, material, flags, desc.World, group, desc.Static);
            if (i == 0)
                handle = slot; // unique while the entity lives
            else
                instances.Link(previous, slot);

            previous = slot;
        }

        return handle;
    }

    /// <summary>New world matrices, straight from the ECS columns; static instances are skipped, unchanged matrices send nothing.</summary>
    public static void Move(RenderContext ctx, ReadOnlySpan<RenderInstance> moved, ReadOnlySpan<WorldTransform> worlds)
    {
        InstanceTable instances = ctx.Instances;
        for (int i = 0; i < moved.Length; i++)
        {
            if (moved[i].Static)
                continue;

            // The chain ends at End, which no slot reaches.
            for (uint slot = moved[i].Handle; slot < instances.HighWater; slot = instances.NextPart(slot))
                instances.Move(slot, worlds[i].Value);
        }
    }

    public static void Remove(RenderContext ctx, uint handle)
    {
        InstanceTable instances = ctx.Instances;
        if (!instances.IsAlive(handle))
            return;

        ulong model = instances.ModelOf(handle);
        for (uint slot = handle; slot < instances.HighWater;)
        {
            uint next = instances.NextPart(slot);
            ctx.Buckets.Release(instances.ClassOf(slot), instances.MeshSlotOf(slot));
            Pipelines.Release(ctx, instances.ClassOf(slot));
            Materials.Release(ctx, instances.MaterialOf(slot));
            instances.Remove(slot);
            slot = next;
        }

        Meshes.Release(ctx, model);
    }

    /// <summary>A material changed class: every instance takes its material's slot and class again, moving draw group when its class changed.</summary>
    public static void Reclass(RenderContext ctx)
    {
        InstanceTable instances = ctx.Instances;
        for (uint slot = 0; slot < instances.HighWater; slot++)
        {
            if (!instances.IsAlive(slot))
                continue;

            uint meshSlot = instances.MeshSlotOf(slot);
            MaterialSlot material = Drawn(ctx, meshSlot, Materials.SlotOf(ctx, instances.MaterialOf(slot)));
            PipelineClass previous = instances.ClassOf(slot);
            uint group = instances.BucketGroupOf(slot);
            if (previous != material.Class)
            {
                ctx.Buckets.Release(previous, meshSlot);
                group = Group(ctx, material.Class, meshSlot);
                Pipelines.Release(ctx, previous);
            }

            instances.Reclass(slot, material, group);
        }
    }

    /// <summary>Uploads every page with a changed row, the cell and page tables and the draw-group template when they changed.</summary>
    public static void Flush(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        FlushInstances(ctx);

        InstanceTable instances = ctx.Instances;
        if (instances.CellsDirty)
        {
            instances.CellBuffer.Ensure(gpu, (uint)instances.CellCount * GpuCell.Size);
            gpu.Upload(instances.CellBuffer.Handle, 0, instances.Cells);
            instances.CellsDirty = false;
        }

        if (instances.PagesDirty)
        {
            instances.PageBuffer.Ensure(gpu, (uint)instances.PageCount * GpuPage.Size);
            gpu.Upload(instances.PageBuffer.Handle, 0, instances.Pages);
            instances.PagesDirty = false;
        }

        Buckets buckets = ctx.Buckets;
        if (buckets.Dirty)
        {
            buckets.Template.Ensure(gpu, (uint)buckets.TemplateRows.Length * DrawArgs.Size);
            gpu.Upload(buckets.Template.Handle, 0, buckets.TemplateRows);
            buckets.Dirty = false;
        }
    }

    /// <summary>
    /// What an instance is drawn with: its own material, or the mesh failure when its model did not load (mesh slot 0,
    /// the failure cube). The instance still holds its own material, so nothing else changes when it is removed.
    /// </summary>
    private static MaterialSlot Drawn(RenderContext ctx, uint meshSlot, MaterialSlot own)
    {
        return meshSlot == 0 ? ctx.Materials.SlotOf(MaterialTable.MeshFailureSlot) : own;
    }

    /// <summary>The draw group of a class and mesh, taking a reference to it and to the class's pipeline; a class seen for the first time starts compiling its pipeline.</summary>
    private static uint Group(RenderContext ctx, PipelineClass cls, uint meshSlot)
    {
        Pipelines.Acquire(ctx, cls);
        return ctx.Buckets.Acquire(cls, meshSlot, ctx.Meshes.Range(meshSlot));
    }

    /// <summary>Every run of adjacent dirty pages in one upload each; a grown buffer takes every row again.</summary>
    private static void FlushInstances(RenderContext ctx)
    {
        InstanceTable instances = ctx.Instances;
        if (!instances.AnyDirty)
            return;

        IRendering gpu = ctx.Gpu;
        bool grown = instances.CullBuffer.Ensure(gpu, instances.HighWater * GpuInstance.Size);
        grown |= instances.XformBuffer.Ensure(gpu, instances.HighWater * GpuInstanceXform.Size);
        Span<bool> dirty = instances.DirtyPages;
        if (grown)
            dirty.Fill(true);

        int pages = dirty.Length;
        for (int first = 0; first < pages; first++)
        {
            if (!dirty[first])
                continue;

            int last = first;
            while (last + 1 < pages && dirty[last + 1])
                last++;
            dirty[first..(last + 1)].Clear();

            int from = first * InstanceTable.PageSize;
            int count = Math.Min((last + 1) * InstanceTable.PageSize, (int)instances.HighWater) - from;
            if (count > 0)
            {
                gpu.Upload(instances.CullBuffer.Handle, (uint)from * GpuInstance.Size, instances.Rows.Slice(from, count));
                gpu.Upload(instances.XformBuffer.Handle, (uint)from * GpuInstanceXform.Size, instances.Xforms.Slice(from, count));
            }

            first = last;
        }

        instances.AnyDirty = false;
    }
}
