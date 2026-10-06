using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Utils;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// Models into the <see cref="MeshTable"/>: each one's meshes placed in the mega buffers the first time it is asked
/// for, given back when its last instance goes, and the uploads. A model's lesser versions (its LODs, which the
/// asset manager knows) are placed with it, mesh for mesh. A mesh of impostor cards (<see cref="ImpostorCard"/>: a
/// model's impostor, or a stand-in's small objects) has the atlases each card names acquired in the texture pools. A
/// model that did not load is the placeholder part alone: the failure cube.
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
        foreach (Handle<Texture> atlas in entry.Atlases)
            Textures.Release(ctx, atlas);
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
            return new ModelEntry([], MeshTable.Placeholder, default, []); // the asset manager logged why

        MeshTable table = ctx.Meshes;
        List<Handle<Texture>> atlases = [];
        uint[] slots = new uint[model.Meshes.Length];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = PlaceMesh(ctx, model.Meshes[i], atlases);

        uint[] owned = [.. slots, .. PlaceLods(ctx, handle, model, slots, atlases)];

        (uint, int)[] parts = new (uint, int)[model.Parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            uint meshSlot = slots.Length == 0 ? 0 : slots[Math.Clamp(model.Parts[i].MeshIndex, 0, slots.Length - 1)];
            parts[i] = (meshSlot, model.Parts[i].MaterialSlot);
        }

        return new ModelEntry(owned, parts.Length == 0 ? MeshTable.Placeholder : parts, model.Origin, [.. atlases]);
    }

    /// <summary>
    /// Places the meshes of the model's lesser versions and tells each of the model's own meshes which are its: mesh i
    /// of a lesser version stands in for mesh i of the model, so one with another number of meshes is left out
    /// (logged). Answers the slots placed.
    /// </summary>
    private static List<uint> PlaceLods(RenderContext ctx, Handle<Model> handle, Model model, uint[] slots, List<Handle<Texture>> atlases)
    {
        List<uint> placed = [];
        List<(float Threshold, uint MeshSlot)>[] lods = [.. slots.Select(_ => new List<(float, uint)>())];
        foreach (Lod level in Preloads.Lods(ctx, handle).Levels)
        {
            float threshold = level.Threshold;
            if (Preloads.Get<Model>(ctx, level.Asset) is not { } lod)
                continue; // the asset manager logged why

            if (lod.Meshes.Length != model.Meshes.Length)
            {
                Debugging.Log.Warn($"{lod.Path} is not used as a LOD of {model.Path}: it has {lod.Meshes.Length} mesh(es), the model {model.Meshes.Length}.");
                continue;
            }

            for (int i = 0; i < slots.Length; i++)
            {
                uint slot = slots[i] == 0 ? 0 : PlaceMesh(ctx, lod.Meshes[i], atlases);
                if (slot == 0)
                    continue; // the buffers are full, or the atlas could not be used: this mesh keeps what it has

                placed.Add(slot);
                lods[i].Add((threshold, slot));
            }
        }

        for (int i = 0; i < slots.Length; i++)
        {
            if (lods[i].Count > 0)
                ctx.Meshes.SetLods(slots[i], [.. lods[i]]);
        }

        return placed;
    }

    /// <summary>
    /// The mesh in a slot. A mesh of cards has, for each card, the two atlases of the model and mesh it names (the
    /// last level of that model's LODs with atlases, 2 per mesh) acquired into the pools (kept in
    /// <paramref name="atlases"/> to give back) and their references written over the card's tangent x and y, where
    /// the shaders read them (Include/Impostor.hlsli): as floats, which hold them exactly. Slot 0 when an atlas cannot
    /// be used, which leaves the mesh without it.
    /// </summary>
    private static uint PlaceMesh(RenderContext ctx, Mesh mesh, List<Handle<Texture>> atlases)
    {
        if (mesh.Vertices.Length == 0 || !ImpostorCard.IsCard(mesh.Vertices[0]))
            return ctx.Meshes.Place(mesh);

        Dictionary<(ulong Model, int Mesh), (uint Surface, uint Normal)> refs = [];
        Vertex[] vertices = new Vertex[mesh.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vertex vertex = mesh.Vertices[i];
            (ulong model, int index) = ImpostorCard.Source(vertex);
            if (!refs.TryGetValue((model, index), out (uint Surface, uint Normal) atlas))
                refs[(model, index)] = atlas = Atlas(ctx, model, index, atlases);
            if (atlas.Surface == TextureTable.Failed || atlas.Normal == TextureTable.Failed)
                return 0;

            vertices[i] = vertex with { Tangent = new Vector4(atlas.Surface, atlas.Normal, vertex.Tangent.Z, vertex.Tangent.W) };
        }

        return ctx.Meshes.Place(new Mesh { Vertices = vertices, Indices = mesh.Indices, Box = mesh.Box, Bounds = mesh.Bounds }, isImpostor: true);
    }

    /// <summary>
    /// The references of the two atlases the model's impostor has for its mesh, acquired into <paramref name="atlases"/>;
    /// failed ones when it has none (logged).
    /// </summary>
    private static (uint Surface, uint Normal) Atlas(RenderContext ctx, ulong model, int mesh, List<Handle<Texture>> atlases)
    {
        Lod[] impostors = [.. Preloads.Lods(ctx, new Handle<Model>(model)).Levels.Where(level => level.Atlases.Length > (2 * mesh) + 1)];
        if (impostors.Length == 0)
        {
            Debugging.Log.Warn($"A card shows mesh {mesh} of {ctx.Assets.PathOf(model)}, which has no impostor for it.");
            return (TextureTable.Failed, TextureTable.Failed);
        }

        Handle<Texture> surface = new(impostors[^1].Atlases[2 * mesh]), normal = new(impostors[^1].Atlases[(2 * mesh) + 1]);
        atlases.Add(surface);
        atlases.Add(normal);
        return (Textures.Acquire(ctx, surface), Textures.Acquire(ctx, normal));
    }
}
