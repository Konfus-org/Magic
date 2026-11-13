using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.World;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Threading;
using Magic.Utils;

namespace Magic.DeferredRendering.View;

/// <summary>
/// Resolves the effects a frame runs over the lit picture, each at its place.
/// </summary>
/// <remarks>
/// Each effect gets its material's row, packed from its shader's struct as a surface material's is, the entries of its
/// passes specialised to its shader, and what it declares it needs. An effect is ready once its material, its textures
/// and its passes are, and failed when one of them failed.
/// </remarks>
internal sealed class Effects(AssetManager assets, IShaderCompiler shaders, Threads threads)
{
    /// <summary>
    /// The places in the list a frame runs, the post processing's.
    /// </summary>
    private const int Places = MaterialSlots.Capacity;

    /// <summary>
    /// The words of a row, as <c>Scene/Materials.slang</c> lays it out: the shader word, then the params.
    /// </summary>
    private const int RowWords = 16;

    private readonly ShaderEntry _applyShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "ApplyPixelEffect");

    private readonly ShaderEntry _levelShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "FillEffectLevel");

    /// <summary>
    /// Each entry specialised to each effect shader, kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<(ShaderEntry Entry, ShaderTypeArgument Shader), ShaderEntry> _specialised = [];

    /// <summary>
    /// Each effect's material asked for, by its id, null while it loads, and why not when it did not load.
    /// </summary>
    private readonly Dictionary<ulong, Result<Material>?> _materials = [];

    /// <summary>
    /// Each effect shader's struct as reflection lays it out, by the shader's id, null while it is reflected.
    /// </summary>
    /// <remarks>
    /// Its fields are default when it was not reflected, and its effects fail.
    /// </remarks>
    private readonly Dictionary<ulong, EffectLayout?> _layouts = [];

    /// <summary>
    /// The param of each slot resolved, its material's or its own, that names no field of its shader's struct, null
    /// for none.
    /// </summary>
    /// <remarks>
    /// It is found once, and again after any asset is read again.
    /// </remarks>
    private readonly Dictionary<MaterialInstance, string?> _misfits = [];

    /// <summary>
    /// The frame's effects and their material rows, by place, written again each frame.
    /// </summary>
    private readonly GpuEffect[] _resolved = new GpuEffect[Places];
    private readonly uint[] _rows = new uint[Places * RowWords];

    /// <summary>
    /// Resolves each place of <paramref name="list"/>, its textures got through <paramref name="textures"/>, and
    /// uploads their rows.
    /// </summary>
    public Result<(ReadOnlyMemory<GpuEffect> Effects, GpuBuffer Rows)> Resolve(
        RenderGraph graph,
        MaterialSlots list,
        TextureLookup textures
    )
    {
        Result<GpuBuffer> rows = graph.Buffer(new("Effect rows", _rows.Length * sizeof(uint)));
        if (!rows.Ok)
            return Result.Failure(rows.Error);

        Array.Clear(_rows);
        for (int place = 0; place < Places; place++)
        {
            Span<uint> words = _rows.AsSpan((place * RowWords) + 1, RowWords - 1);
            _resolved[place] = Resolve(graph, list[place], textures, words);
        }

        if (!graph.Upload(rows.Value, _rows.AsSpan()))
            return Result.Failure("the effect rows were not staged.");

        return Result.Success<(ReadOnlyMemory<GpuEffect>, GpuBuffer)>((_resolved, rows.Value));
    }

    /// <summary>
    /// Whether any of <paramref name="list"/>'s effects is still loading, its material, its textures or its passes.
    /// </summary>
    public bool IsLoading(RenderGraph graph, MaterialSlots list, TextureLookup textures)
    {
        Span<uint> words = stackalloc uint[RowWords - 1];
        bool isLoading = false;
        foreach (MaterialInstance slot in list)
        {
            isLoading |= Resolve(graph, slot, textures, words).State == GpuState.Loading;
        }

        return isLoading;
    }

    /// <summary>
    /// Lets go of what was made from asset <paramref name="id"/>, read again, so the next ask loads it anew.
    /// </summary>
    /// <remarks>
    /// That is a material, and an effect shader's struct, which is reflected again now.
    /// </remarks>
    public void Forget(ulong id)
    {
        _materials.Remove(id);
        _misfits.Clear();
        if (_layouts.ContainsKey(id))
        {
            _layouts[id] = null;
            ReflectAsync(new Handle<Shader>(id)).FireAndForget();
        }
    }

    /// <summary>
    /// Resolves the effect of <paramref name="slot"/>, and packs its params into <paramref name="words"/>.
    /// </summary>
    private GpuEffect Resolve(RenderGraph graph, in MaterialInstance slot, TextureLookup textures, Span<uint> words)
    {
        if (slot.Handle == Handle<Material>.None)
            return new GpuEffect(0, GpuState.Failed, default, default, default);

        GpuState material = Pack(slot, textures, words, out EffectLayout layout, out string? failure);

        // The material, its shader or a part failed. A failed part, a texture or a param that fits no field, would run
        // the effect wrong, so the effect fails too, with the part named.
        if (failure is not null)
        {
            string path = assets.GetPath(slot.Handle) ?? $"id {slot.Handle.Id}";
            graph.Fail(RenderFailure.PostEffect, "An effect is not drawn", $"Material {path} failed: {failure}.");

            return new GpuEffect(slot.Handle.Id, GpuState.Failed, default, default, default);
        }

        if (material != GpuState.Ready)
            return new GpuEffect(slot.Handle.Id, material, default, default, default);

        ShaderEntry apply = GetSpecialised(_applyShader, layout.Shader);
        ShaderEntry level = GetSpecialised(_levelShader, layout.Shader);
        GpuState applied = graph.GetState(apply);
        GpuState levels = layout.Needs.Levels == 0 ? GpuState.Ready : graph.GetState(level);

        return new GpuEffect(slot.Handle.Id, applied == GpuState.Ready ? levels : applied, apply, level, layout.Needs);
    }

    /// <summary>
    /// Packs the slot's params into <paramref name="words"/>, and gets whether its material, its shader's struct and
    /// its textures are on the GPU.
    /// </summary>
    /// <remarks>
    /// It also gives its shader's struct as reflection laid it out, the default struct while it loads, and the part
    /// that failed, null for none: the material, its shader, a texture, or a param that fits no field.
    /// </remarks>
    private GpuState Pack(
        in MaterialInstance slot,
        TextureLookup textures,
        Span<uint> words,
        out EffectLayout layout,
        out string? failure
    )
    {
        layout = default;
        failure = null;
        if (!_materials.TryGetValue(slot.Handle.Id, out Result<Material>? asked))
        {
            _materials[slot.Handle.Id] = null;
            LoadAsync(slot.Handle).FireAndForget();

            return GpuState.Loading;
        }

        if (asked is not { } loaded)
            return GpuState.Loading;

        if (!loaded.Ok)
        {
            failure = "it did not load";

            return GpuState.Failed;
        }

        Material material = loaded.Value;
        if (!_layouts.TryGetValue(material.Shader.Id, out EffectLayout? reflecting))
        {
            _layouts[material.Shader.Id] = null;
            ReflectAsync(material.Shader).FireAndForget();

            return GpuState.Loading;
        }

        if (reflecting is not { } reflected)
            return GpuState.Loading;

        layout = reflected;
        if (layout.Fields.IsDefault)
        {
            failure = "its shader";

            return GpuState.Failed;
        }

        MaterialOverrides overrides = slot.Overrides;
        GpuPacked packed = GpuPacker.Pack(layout.Fields, new SlotParams(material, overrides, textures), words);

        // A param of the material or of the slot that names no field, looked for once for the slot.
        string? misfit = packed.Misfit;
        if (misfit is null && !_misfits.TryGetValue(slot, out misfit))
        {
            misfit = GpuPacker.FindUnnamed(
                layout.Fields,
                material.Params.Keys.Concat(overrides.Params.Select(param => param.Key))
            );
            _misfits[slot] = misfit;
        }

        if (packed.FailedTexture != 0)
        {
            Handle<Texture> texture = new(packed.FailedTexture);
            failure = $"its texture {assets.GetPath(texture) ?? $"id {texture.Id}"}";
        }
        else if (misfit is not null)
            failure = $"its param {misfit}, which fits no field of its shader";

        return packed.State;
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

    private async Task LoadAsync(Handle<Material> handle)
    {
        Result<Material> loaded = await assets.LoadAsync(handle, null, CancellationToken.None).ConfigureAwait(false);
        await threads.Enter(EngineThread.Render);
        _materials[handle.Id] = loaded;
        if (!loaded.Ok)
            Log.Error($"Effect {assets.GetPath(handle) ?? $"id {handle.Id}"} is not drawn: {loaded.Error}");
    }

    /// <summary>
    /// Reflects the struct of <paramref name="shader"/>, which an effect runs with, once: named for its file, as
    /// Material/Exposure.slang declares Exposure.
    /// </summary>
    /// <remarks>
    /// It leaves it failed when it can't, or the struct is more than a row holds.
    /// </remarks>
    private async Task ReflectAsync(Handle<Shader> shader)
    {
        string path = assets.GetPath(shader) ?? $"id {shader.Id}";
        ShaderTypeArgument type = new(shader, Path.GetFileNameWithoutExtension(path));
        Result<EffectLayout> layout;
        try
        {
            layout = await ReflectLayoutAsync(type).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            layout = Result.Failure($"its reflection threw: {ex}");
        }

        await threads.Enter(EngineThread.Render);
        if (!layout.Ok)
            Log.Error($"Effect shader {path} is not drawn: {layout.Error}");

        _layouts[shader.Id] = layout.Ok ? layout.Value : new EffectLayout(default, type, default);
    }

    /// <summary>
    /// Reflects the struct <paramref name="type"/> names, as an effect's row lays it out, with what it declares it
    /// needs.
    /// </summary>
    /// <returns>The layout, or why not, as when it is more than a row holds.</returns>
    private async Task<Result<EffectLayout>> ReflectLayoutAsync(ShaderTypeArgument type)
    {
        Result<ImmutableArray<ShaderField>> fields = await shaders
            .ReflectAsync(type, CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> levels = await shaders
            .ReflectConstantAsync(type, "Levels", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> history = await shaders
            .ReflectConstantAsync(type, "History", CancellationToken.None)
            .ConfigureAwait(false);
        Result<long> jitters = await shaders
            .ReflectConstantAsync(type, "Jitters", CancellationToken.None)
            .ConfigureAwait(false);
        if (!fields.Ok)
            return Result.Failure(fields.Error);

        const int ParamBytes = (RowWords - 1) * sizeof(uint);
        int bytes = fields.Value.Select(field => (int)(field.Offset + field.Size)).DefaultIfEmpty().Max();
        if (bytes > ParamBytes)
            return Result.Failure($"its params are {bytes} bytes, over a row's {ParamBytes}");

        return Result.Success(
            new EffectLayout(
                fields.Value,
                type,
                new EffectNeeds(
                    levels.Ok ? (int)levels.Value : 0,
                    history.Ok && history.Value != 0,
                    jitters.Ok && jitters.Value != 0
                )
            )
        );
    }

    /// <summary>
    /// An effect shader's struct as its rows are packed: its fields as reflection lays them out, default when it could
    /// not be, the struct its passes are specialised to, and what it declares it needs.
    /// </summary>
    private readonly record struct EffectLayout(
        ImmutableArray<ShaderField> Fields,
        ShaderTypeArgument Shader,
        EffectNeeds Needs
    );

    /// <summary>
    /// A slot's params over its material's, with its textures got through the world's lookup.
    /// </summary>
    private readonly struct SlotParams(Material material, MaterialOverrides overrides, TextureLookup textures)
        : IGpuParams
    {
        public bool TryGetParam(string name, [MaybeNullWhen(false)] out MaterialParam param) =>
            overrides.TryGet(name, out param) || material.Params.TryGetValue(name, out param);

        public GpuState GetTexture(MaterialParam param, out uint texture, out uint sampler) =>
            textures(param, out texture, out sampler);
    }
}
