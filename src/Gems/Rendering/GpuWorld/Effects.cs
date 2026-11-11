using Magic.Assets;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.GpuWorld;

/// <summary>
/// The effects a frame runs over the lit picture, each resolved at its place: its material's row, packed as a surface
/// material's is, the entries of its passes specialised to its shader, and what it declares it needs. An effect is
/// ready once its material, its textures and its passes are, and failed when one of them failed.
/// </summary>
internal sealed class Effects(AssetManager assets, GpuResources resources, IShaderCompiler shaders, Threads threads)
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
    /// What each effect shader's struct declares it needs, by the shader's id, null while it is reflected.
    /// </summary>
    private readonly Dictionary<ulong, EffectNeeds?> _needs = [];

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
        Result<GpuBuffer> rows = graph.Buffer(GpuDescriptions.GetBuffer("Effect rows", Places * GpuMaterial.Bytes));
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        for (int place = 0; place < Places; place++)
        {
            (_resolved[place], _rows[place]) = Resolve(graph, list[place]);
        }

        graph.Upload(rows.Value, _rows.AsSpan());

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

        GpuState material = resources.Pack(slot, out GpuMaterial row, out GpuResources.MaterialLayout layout);
        if (material != GpuState.Ready)
            return (new GpuEffect(slot.Handle.Id, material, default, default, default), row);

        // What it needs is reflected on its first ask, and it waits for that.
        ShaderTypeArgument shader = layout.Shader;
        if (!_needs.TryGetValue(shader.Module.Id, out EffectNeeds? declared))
        {
            _needs[shader.Module.Id] = null;
            ReflectNeedsAsync(shader).FireAndForget();
        }

        if (declared is not { } needs)
            return (new GpuEffect(slot.Handle.Id, GpuState.Loading, default, default, default), row);

        ShaderEntry apply = GetSpecialised(_applyShader, shader);
        ShaderEntry level = GetSpecialised(_levelShader, shader);
        GpuState applied = graph.GetState(apply);
        GpuState levels = needs.Levels == 0 ? GpuState.Ready : graph.GetState(level);

        return (new GpuEffect(slot.Handle.Id, applied == GpuState.Ready ? levels : applied, apply, level, needs), row);
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

    /// <summary>
    /// Reflects what the effect shader's struct declares it needs, once: none of what it declares nothing of.
    /// </summary>
    private async Task ReflectNeedsAsync(ShaderTypeArgument shader)
    {
        Result<long> levels = await shaders
            .ReflectConstantAsync(shader, "Levels", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> history = await shaders
            .ReflectConstantAsync(shader, "History", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> jitters = await shaders
            .ReflectConstantAsync(shader, "Jitters", CancellationToken.None)
            .ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        _needs[shader.Module.Id] = new EffectNeeds(
            levels.Ok ? (int)levels.Value : 0,
            history.Ok && history.Value != 0,
            jitters.Ok && jitters.Value != 0
        );
    }
}
