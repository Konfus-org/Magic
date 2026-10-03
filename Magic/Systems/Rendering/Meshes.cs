using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;

namespace Magic.Systems.Rendering;

/// <summary>
/// Models into the <see cref="MeshTable"/>: each one's meshes placed in the mega buffers the first time it is asked
/// for, given back when its last instance goes, and the uploads. A model's lesser versions (its LODs, which the
/// asset manager knows) are placed with it, mesh for mesh. A model that did not load is the placeholder part alone:
/// the failure cube.
/// </summary>
internal static class Meshes
{
    /// <summary>
    /// The parts of a model, loading and placing it the first time; anything that cannot be drawn is the placeholder part alone.
    /// </summary>
    public static (uint MeshSlot, int MaterialSlot)[] Acquire(RenderContext ctx, Handle<Model> handle)
    {
        if (!handle.IsValid)
            return MeshTable.Placeholder;

        MeshTable table = ctx.Meshes;
        if (table.TryAcquire(handle.Id, out ModelEntry entry))
            return entry.Parts;

        entry = Place(ctx, handle, Preloads.Get<Model>(ctx, handle.Id));
        table.Add(handle.Id, entry);
        return entry.Parts;
    }

    /// <summary>
    /// The origin of a model that is placed; none for one that is not.
    /// </summary>
    public static Vector3 Origin(RenderContext ctx, Handle<Model> handle)
    {
        return ctx.Meshes.TryGet(handle.Id, out ModelEntry entry) ? entry.Origin : default;
    }

    public static void Release(RenderContext ctx, ulong id)
    {
        if (!ctx.Meshes.Release(id, out ModelEntry entry))
            return;

        foreach (uint slot in entry.Slots)
            ctx.Meshes.Remove(slot);
    }

    /// <summary>
    /// Uploads the geometry placed since the last frame.
    /// </summary>
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

    /// <summary>
    /// The model's meshes in slots and its parts on them; a model that did not load (null) is the placeholder part alone.
    /// </summary>
    private static ModelEntry Place(RenderContext ctx, Handle<Model> handle, Model? model)
    {
        if (model is null)
            return new ModelEntry([], MeshTable.Placeholder, default); // the asset manager logged why

        MeshTable table = ctx.Meshes;
        uint[] slots = new uint[model.Meshes.Length];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = table.Place(model.Meshes[i]);

        uint[] owned = [.. slots, .. PlaceLods(ctx, handle, model, slots)];

        (uint, int)[] parts = new (uint, int)[model.Parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            uint meshSlot = slots.Length == 0 ? 0 : slots[Math.Clamp(model.Parts[i].MeshIndex, 0, slots.Length - 1)];
            parts[i] = (meshSlot, model.Parts[i].MaterialSlot);
        }

        return new ModelEntry(owned, parts.Length == 0 ? MeshTable.Placeholder : parts, model.Origin);
    }

    /// <summary>
    /// Places the meshes of the model's lesser versions and tells each of the model's own meshes which are its: mesh i
    /// of a lesser version stands in for mesh i of the model, so one with another number of meshes is left out
    /// (logged). Answers the slots placed.
    /// </summary>
    private static List<uint> PlaceLods(RenderContext ctx, Handle<Model> handle, Model model, uint[] slots)
    {
        MeshTable table = ctx.Meshes;
        List<uint> placed = [];
        List<(float Threshold, uint MeshSlot)>[] lods = [.. slots.Select(_ => new List<(float, uint)>())];
        foreach ((float threshold, Handle<Model> lesser) in Preloads.Lods(ctx, handle))
        {
            if (Preloads.Get<Model>(ctx, lesser.Id) is not { } lod)
                continue; // the asset manager logged why

            if (lod.Meshes.Length != model.Meshes.Length)
            {
                Debugging.Log.Warn($"{lod.Path} is not used as a LOD of {model.Path}: it has {lod.Meshes.Length} mesh(es), the model {model.Meshes.Length}.");
                continue;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                uint slot = slots[i] == 0 ? 0 : table.Place(lod.Meshes[i]);
                if (slot == 0)
                    continue; // the buffers are full: this mesh keeps what it has

                placed.Add(slot);
                lods[i].Add((threshold, slot));
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            if (lods[i].Count > 0)
                table.SetLods(slots[i], [.. lods[i]]);
        }

        return placed;
    }
}
