using Magic.Assets;
using Magic.DeferredRendering.World;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.View;

/// <summary>
/// The effects a frame runs over the lit picture, each resolved at its place: its material's row, packed as a surface
/// material's is, the entries of its passes specialised to its shader, and what it declares it needs. An effect is
/// ready once its material, its textures and its passes are, and failed when one of them failed.
/// </summary>
internal sealed class Effects(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// The places in the list a frame runs, the post processing's.
    /// </summary>
    private const int Places = MaterialSlots.Capacity;

    private readonly ShaderEntry _applyShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "ApplyPixelEffect");

    private readonly ShaderEntry _levelShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "FillEffectLevel");

    /// <summary>
    /// Each entry specialised to each effect shader, kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<(ShaderEntry Entry, ShaderTypeArgument Shader), ShaderEntry> _specialised = [];

    /// <summary>
    /// The frame's effects and their material rows, by place, written again each frame.
    /// </summary>
    private readonly GpuEffect[] _resolved = new GpuEffect[Places];
    private readonly GpuMaterial[] _rows = new GpuMaterial[Places];

    /// <summary>
    /// Each place of <paramref name="list"/> resolved, and their rows uploaded.
    /// </summary>
    public Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> Resolve(RenderGraph graph, MaterialSlots list)
    {
        Result<GpuBuffer> rows = graph.Buffer(new("Effect rows", Places * GpuMaterial.Bytes));
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        for (int place = 0; place < Places; place++)
        {
            (_resolved[place], _rows[place]) = Resolve(graph, list[place]);
        }

        if (!graph.Upload(rows.Value, _rows.AsSpan()))
            return Result.Failure("the effect rows were not staged.");

        return Result.Success<(ReadOnlyMemory<GpuEffect>, GpuBuffer)>((_resolved, rows.Value));
    }

    /// <summary>
    /// Whether any of <paramref name="list"/>'s effects is still loading, its material, its textures or its passes.
    /// </summary>
    public bool IsLoading(RenderGraph graph, MaterialSlots list)
    {
        bool isLoading = false;
        foreach (MaterialInstance slot in list)
        {
            isLoading |= Resolve(graph, slot).Effect.State == GpuState.Loading;
        }

        return isLoading;
    }

    private (GpuEffect Effect, GpuMaterial Row) Resolve(RenderGraph graph, in MaterialInstance slot)
    {
        if (slot.Handle == Handle<Material>.None)
            return (new GpuEffect(0, GpuState.Failed, default, default, default), default);

        GpuState material = resources.Pack(
            slot,
            out GpuMaterial row,
            out GpuResources.MaterialLayout layout,
            out (FailureKind Kind, ulong Asset, string? Param)? failure
        );

        // The material, its shader or a part failed. A failed part, a texture or a param that fits no field, would run
        // the effect wrong, so the effect fails too, with the part named.
        if (failure is (var kind, var asset, var param))
        {
            string path = assets.GetPath(slot.Handle) ?? $"id {slot.Handle.Id}";
            string part = kind switch
            {
                FailureKind.Texture => $"its texture {assets.GetPath(new Handle<Texture>(asset)) ?? $"id {asset}"}",
                FailureKind.Param => $"its param {param}, which fits no field of its shader",
                FailureKind.Shader => "its shader",
                FailureKind.Material or FailureKind.Model => "it did not load",
                _ => "it did not load",
            };
            graph.Fail(RenderFailure.PostEffect, "An effect is not drawn", $"Material {path} failed: {part}.");

            return (new GpuEffect(slot.Handle.Id, GpuState.Failed, default, default, default), row);
        }

        if (material != GpuState.Ready)
            return (new GpuEffect(slot.Handle.Id, material, default, default, default), row);

        ShaderEntry apply = GetSpecialised(_applyShader, layout.Shader);
        ShaderEntry level = GetSpecialised(_levelShader, layout.Shader);
        GpuState applied = graph.GetState(apply);
        GpuState levels = layout.Needs.Levels == 0 ? GpuState.Ready : graph.GetState(level);

        return (
            new GpuEffect(slot.Handle.Id, applied == GpuState.Ready ? levels : applied, apply, level, layout.Needs),
            row
        );
    }

    private ShaderEntry GetSpecialised(ShaderEntry entry, ShaderTypeArgument shader)
    {
        if (!_specialised.TryGetValue((entry, shader), out ShaderEntry specialised))
        {
            specialised = entry with { TypeArguments = [shader] };
            _specialised[(entry, shader)] = specialised;
        }

        return specialised;
    }
}
