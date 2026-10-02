using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;

namespace Magic.Systems.Rendering;

/// <summary>
/// Models into the <see cref="MeshTable"/>: each one's meshes placed in the mega buffers the first time it is asked
/// for, given back when its last instance goes, and the uploads. A model that did not load is the placeholder part
/// alone: the failure cube.
/// </summary>
internal static class Meshes
{
    /// <summary>The parts of a model, loading and placing it the first time; anything that cannot be drawn is the placeholder part alone.</summary>
    public static (uint MeshSlot, int MaterialSlot)[] Acquire(RenderContext ctx, Handle<Model> handle)
    {
        if (!handle.IsValid)
            return MeshTable.Placeholder;

        MeshTable table = ctx.Meshes;
        if (table.TryAcquire(handle.Id, out ModelEntry entry))
            return entry.Parts;

        entry = Place(table, ctx.Assets.Load(handle));
        table.Add(handle.Id, entry);
        return entry.Parts;
    }

    public static void Release(RenderContext ctx, ulong id)
    {
        if (!ctx.Meshes.Release(id, out ModelEntry entry))
            return;

        foreach (uint slot in entry.Slots)
            ctx.Meshes.Remove(slot);
    }

    /// <summary>Uploads the geometry placed since the last frame.</summary>
    public static void Flush(RenderContext ctx)
    {
        IRendering gpu = ctx.Gpu;
        MeshTable table = ctx.Meshes;
        foreach ((uint firstVertex, uint firstIndex, Vertex[] vertices, uint[] indices) in table.Pending)
        {
            gpu.Upload<Vertex>(table.VertexBuffer, firstVertex * Vertex.Size, vertices);
            gpu.Upload<uint>(table.IndexBuffer, firstIndex * 4, indices);
        }

        table.Pending.Clear();
    }

    /// <summary>The model's meshes in slots and its parts on them; a model that did not load (null) is the placeholder part alone.</summary>
    private static ModelEntry Place(MeshTable table, Model? model)
    {
        if (model is null)
            return new ModelEntry([], MeshTable.Placeholder); // the asset manager logged why

        uint[] slots = new uint[model.Meshes.Length];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = table.Place(model.Meshes[i]);

        (uint, int)[] parts = new (uint, int)[model.Parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            uint meshSlot = slots.Length == 0 ? 0 : slots[Math.Clamp(model.Parts[i].MeshIndex, 0, slots.Length - 1)];
            parts[i] = (meshSlot, model.Parts[i].MaterialSlot);
        }

        return new ModelEntry(slots, parts.Length == 0 ? MeshTable.Placeholder : parts);
    }
}
