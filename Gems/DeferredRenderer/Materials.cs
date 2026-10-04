using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Utils;

using Magic.Extensions;
using System.Numerics;

namespace DeferredRendererGem;

/// <summary>
/// Materials into the <see cref="MaterialTable"/>: each one's surface, pipeline class and textures, and its packed record,
/// written when the material is packed.
/// A material that cannot be built (the file did not load, its shader is not an asset, its surface was rejected, a
/// texture it samples did not load) keeps its slot but is pointed at the failure surface with the failure kind in its
/// record, so it draws as a glowing checker and is repaired in place by the same reload path as a healthy one. No shader
/// ever sees a broken record or a broken texture reference.
/// </summary>
internal static class Materials
{
    private static readonly Dictionary<string, Param> NoParams = [];

    /// <summary>
    /// Packs the two built-in slots: 0, the default surface with its defaults, and the mesh failure.
    /// </summary>
    public static void AddBuiltIn(RenderContext ctx)
    {
        Store(ctx, Pack(ctx, ctx.Materials.States.Add(), new Material { Shader = new Handle<Shader>(ctx.Materials.DefaultSurface) }, []));
        Store(ctx, Pack(ctx, ctx.Materials.States.Add(), null, [], MaterialTable.FailureMesh));
    }

    /// <summary>
    /// The slot for a material, loading and packing it the first time; the default for no material at all.
    /// </summary>
    public static MaterialSlot Acquire(RenderContext ctx, Handle<Material> handle)
    {
        MaterialTable table = ctx.Materials;
        if (!handle.IsValid)
            return table.SlotOf(0);
        if (table.TryAcquire(handle.Id, out uint slot))
            return table.SlotOf(slot);

        slot = table.States.Add();
        Store(ctx, Pack(ctx, slot, Preloads.Get<Material>(ctx, handle.Id), []));
        table.Add(handle.Id, slot);
        return table.SlotOf(slot);
    }

    /// <summary>
    /// The slot a material already has, without taking a reference; the default for one it does not.
    /// </summary>
    public static MaterialSlot SlotOf(RenderContext ctx, Handle<Material> handle)
    {
        return ctx.Materials.SlotOf(handle.IsValid && ctx.Materials.TryGet(handle.Id, out uint slot) ? slot : 0);
    }

    public static void Release(RenderContext ctx, Handle<Material> handle)
    {
        MaterialTable table = ctx.Materials;
        if (!table.Release(handle.Id, out uint slot))
            return;

        foreach (Handle<Texture> texture in table.States[slot]?.Textures ?? [])
            Textures.Release(ctx, texture);

        table.States.Remove(slot);
        table.Record(slot).Clear();
        table.Dirty = true;
    }

    /// <summary>
    /// The material file changed: reload and repack in place. True when its pipeline class changed.
    /// </summary>
    public static bool Reload(RenderContext ctx, ulong id)
    {
        if (!ctx.Materials.TryGet(id, out uint slot))
            return false;

        return Repack(ctx, slot, Preloads.Get<Material>(ctx, id));
    }

    /// <summary>
    /// Surface shaders changed: every material using one of them, and every failed one (it may be whole now), repacks.
    /// True when any pipeline class changed.
    /// </summary>
    public static bool RepackShaders(RenderContext ctx, IReadOnlySet<ulong> shaders)
    {
        bool changed = false;
        Slots<MaterialState> states = ctx.Materials.States;
        for (uint slot = 0; slot < states.Count; slot++)
        {
            if (states[slot] is not { } state)
                continue;

            ulong surface = state.Material?.Shader.IsValid == true ? state.Material.Shader.Id : ctx.Materials.DefaultSurface;
            if (shaders.Contains(surface) || state.Failure != 0)
                changed |= Repack(ctx, slot, state.Material);
        }

        return changed;
    }

    /// <summary>
    /// Textures were loaded again: every material holding one repacks (a texture that failed, or stopped failing, changes
    /// its surface) and so resolves its texture references again. True when any pipeline class changed.
    /// </summary>
    public static bool RepackTextures(RenderContext ctx)
    {
        bool changed = false;
        Slots<MaterialState> states = ctx.Materials.States;
        for (uint slot = 0; slot < states.Count; slot++)
        {
            if (states[slot] is { Textures.Length: > 0 } state)
                changed |= Repack(ctx, slot, state.Material);
        }

        return changed;
    }

    /// <summary>
    /// Uploads the records, when one was written.
    /// </summary>
    public static void Flush(RenderContext ctx)
    {
        MaterialTable table = ctx.Materials;
        if (!table.Dirty)
            return;

        table.Dirty = false;
        uint bytes = (uint)table.States.Count * GpuMaterial.Size;
        table.Records.Ensure(ctx.Gpu, bytes);
        ctx.Gpu.Upload(table.Records.Handle, 0, table.RecordBytes.AsSpan(0, (int)bytes));
        table.GiRow((uint)Math.Max(0, table.States.Count - 1));
        table.GiRecords.Ensure(ctx.Gpu, (uint)table.States.Count * GpuGiMaterial.Size);
        ctx.Gpu.Upload<GpuGiMaterial>(table.GiRecords.Handle, 0, table.GiRows.AsSpan(0, table.States.Count));
    }

    /// <summary>
    /// Packs the slot again from <paramref name="material"/>; true when its class changed.
    /// </summary>
    private static bool Repack(RenderContext ctx, uint slot, Material? material)
    {
        MaterialState? before = ctx.Materials.States[slot];
        uint forced = slot == MaterialTable.MeshFailureSlot ? MaterialTable.FailureMesh : 0;
        MaterialState after = Pack(ctx, slot, material, before?.Textures ?? [], forced);
        Store(ctx, after);
        return after.Class != before?.Class;
    }

    /// <summary>
    /// Puts a packed state in its slot and writes its record.
    /// </summary>
    private static void Store(RenderContext ctx, MaterialState state)
    {
        MaterialTable table = ctx.Materials;
        table.States[state.Slot] = state;
        Build(ctx, state, table.Record(state.Slot));
        table.GiRow(state.Slot) = GiRowOf(ctx, state, table.Record(state.Slot));
        table.Dirty = true;
    }

    /// <summary>
    /// What a material gives the GI, read back out of its packed record through the surface's parameters marked
    /// <c>GiColor</c> and <c>GiEmissive</c>: a grey for a material that failed or whose surface marks nothing.
    /// </summary>
    private static GpuGiMaterial GiRowOf(RenderContext ctx, MaterialState state, ReadOnlySpan<byte> record)
    {
        GpuGiMaterial row = new() { Albedo = new Vector4(0.5f, 0.5f, 0.5f, 1f) };
        if (state.Failure != 0 || Shaders.Surface(ctx, state.Class.Surface) is not { } surface)
            return row;

        foreach (ParamField field in surface.Layout.Fields)
        {
            if (!field.IsFloat || field.Semantic is not ("GiColor" or "GiEmissive"))
                continue;

            ReadOnlySpan<float> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(record.Slice(field.Offset, field.Size));
            Vector4 value = new(values[0], values.Length > 1 ? values[1] : values[0], values.Length > 2 ? values[2] : values[0], 1f);
            if (field.Semantic == "GiColor")
                row.Albedo = value;
            else
                row.Emissive = value;
        }

        return row;
    }

    /// <summary>
    /// A material's state: its surface, class and textures, or the failure surface and why. The new textures are acquired
    /// before <paramref name="previous"/> are released, so one the material keeps never drops to zero and reloads. A
    /// material with a failed texture keeps holding its textures, so a repaired one is seen when textures reload.
    /// </summary>
    /// <param name="forced">A failure kind the slot has whatever <paramref name="material"/> is (the mesh failure slot).</param>
    private static MaterialState Pack(RenderContext ctx, uint slot, Material? material, Handle<Texture>[] previous, uint forced = 0)
    {
        MaterialTable table = ctx.Materials;
        SurfaceVariant variant = SurfaceVariant.None;
        SurfaceSource? surface = null;
        uint failure = forced;
        string? error = null;
        if (forced == 0 && material is null)
        {
            failure = MaterialTable.FailureMissing; // the file did not load; the asset manager said why
            error = "Material missing";
        }
        else if (forced == 0 && material is not null)
        {
            if (material.Type == MaterialType.Masked)
                variant |= SurfaceVariant.Masked;
            if (material.DoubleSided)
                variant |= SurfaceVariant.DoubleSided;

            ulong surfaceId = material.Shader.IsValid ? material.Shader.Id : table.DefaultSurface;
            surface = Shaders.Surface(ctx, surfaceId);
            if (surface is null)
            {
                Shader? rejected = Shaders.Get(ctx, new Handle<Shader>(surfaceId));
                failure = rejected is null ? MaterialTable.FailureMissing : MaterialTable.FailureShader; // not an asset vs. rejected
                error = rejected is null ? $"Shader {surfaceId} missing" : "Not a surface shader";
            }
        }

        List<Handle<Texture>> textures = [];
        if (surface is not null && failure == 0 && material is not null)
        {
            foreach (string key in surface.Layout.UnknownKeys(material.Params))
                Debugging.Log.Warn($"{material.Path} sets '{key}', which {surface.Path} does not declare.");

            foreach (ParamField field in surface.Layout.Fields)
            {
                if (field.Type == ParamType.TextureRef && material.Params.TryGetValue(field.Name, out Param param) && param.Texture.IsValid)
                {
                    if (Textures.Acquire(ctx, param.Texture) == TextureTable.Failed)
                    {
                        failure = MaterialTable.FailureTexture;
                        error = $"Texture {param.Texture.Id} missing";
                    }

                    textures.Add(param.Texture);
                }
            }
        }

        if (failure != 0)
        {
            SurfaceSource? failureSurface = table.FailureSurface != 0 ? Shaders.Surface(ctx, table.FailureSurface) : null;
            if (failureSurface is not null)
                surface = failureSurface;
            else
            {
                // No failure surface either: the material's own surface when it has one (a failed texture is then
                // no texture), else the default surface.
                failure = 0;
                error = null;
                surface ??= Shaders.Surface(ctx, table.DefaultSurface);
            }
        }

        foreach (Handle<Texture> texture in previous)
            Textures.Release(ctx, texture);

        return new MaterialState(slot, material, new PipelineClass(surface?.Id ?? table.DefaultSurface, variant), failure, [.. textures], error);
    }

    /// <summary>
    /// The record bytes of a slot: zeros for a free one, the failure kind over the failure surface's defaults, or the material's parameters.
    /// </summary>
    private static void Build(RenderContext ctx, MaterialState? state, Span<byte> record)
    {
        record.Clear();
        if (state is null || Shaders.Surface(ctx, state.Class.Surface) is not { } surface)
            return;

        if (state.Failure == 0)
        {
            surface.Layout.Write(state.Material?.Params ?? NoParams, texture => Reference(ctx, texture), record);
            return;
        }

        // The failure surface's own layout, { uint kind }: its defaults, then the kind over them.
        surface.Layout.Write(NoParams, texture => Reference(ctx, texture), record);
        foreach (ParamField field in surface.Layout.Fields)
        {
            if (field.Name == "kind")
                BitConverter.TryWriteBytes(record[field.Offset..], state.Failure);
        }
    }

    /// <summary>
    /// What a record carries for a texture: its packed reference, or none. A failed texture never reaches a record as
    /// such: its material draws the failure surface instead, and without one the texture is simply absent.
    /// </summary>
    private static uint Reference(RenderContext ctx, Handle<Texture> texture)
    {
        uint packed = ctx.Textures.Lookup(texture);
        return packed == TextureTable.Failed ? TextureTable.None : packed;
    }
}
