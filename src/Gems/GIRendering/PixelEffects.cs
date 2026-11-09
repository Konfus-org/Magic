using System.Linq;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The effects run over the lit picture before the output, the air's and then the picture's, in order: each a material
/// whose shader implements the pixel effect contract, its row packed as a surface material's is, and passes of the
/// entries specialised to its shader: one for each level of its own target it declares, then its apply, which reads the
/// picture the effects before it left and writes the other of two, the last copied back into the HDR picture. An
/// effect with levels has two targets by its place in the lists, which swap each frame, so the one it filled last frame
/// is its history, and one that declares jitter has the camera jittered while it runs. An effect is left out while its
/// material, its textures or its passes load, and when one of them failed.
/// </summary>
internal sealed class PixelEffects(AssetManager assets, GpuResources resources)
{
    /// <summary>
    /// The places in the lists a frame runs, the weather's and the post processing's.
    /// </summary>
    private const int Places = 2 * MaterialSlots.Capacity;

    /// <summary>
    /// The most levels an effect's target has: the storage levels the heap keeps of a texture.
    /// </summary>
    private const int MostLevels = 16;

    /// <summary>
    /// The names of the two targets of the effect at each place, by the parity of the frame that fills it.
    /// </summary>
    private static readonly string[] _targetNames = [.. Enumerable.Range(0, 2 * Places).Select(i => $"Effect {i}")];

    private readonly ShaderEntry _applyShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "ApplyPixelEffect");

    private readonly ShaderEntry _levelShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "FillEffectLevel");

    /// <summary>
    /// Each entry specialised to each effect shader, kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<(ShaderEntry Entry, ShaderTypeArgument Shader), ShaderEntry> _specialised = [];

    /// <summary>
    /// The effects a frame runs, in order: a reusable buffer for <see cref="Record"/>.
    /// </summary>
    private readonly List<RunningEffect> _running = [];

    /// <summary>
    /// The material the effect at each place was last frame, 0 where none ran: whether its history is its own.
    /// </summary>
    private readonly ulong[] _lastFrame = new ulong[Places];

    /// <summary>
    /// Runs the effects of each of <paramref name="lists"/>, in order, on frame <paramref name="frameIndex"/>, their
    /// rays through <paramref name="world"/> on a GPU that traces them.
    /// </summary>
    public void Record(in RenderView view, uint frameIndex, ReadOnlySpan<MaterialSlots> lists, TracedWorld? world)
    {
        RenderGraph graph = view.Graph;

        // The rows, entries and targets of the effects ready to run, in order.
        Span<GpuMaterial> rows = stackalloc GpuMaterial[Places];
        _running.Clear();
        int passCount = 0;
        for (int place = 0; place < lists.Length * MaterialSlots.Capacity; place++)
        {
            MaterialInstance slot = lists[place / MaterialSlots.Capacity][place % MaterialSlots.Capacity];
            bool ranLastFrame = _lastFrame[place] == slot.Handle.Id;
            _lastFrame[place] = 0;
            GpuState state = GetState(
                graph,
                slot,
                out GpuMaterial row,
                out ShaderEntry apply,
                out ShaderEntry level,
                out EffectNeeds needs
            );
            if (state != GpuState.Ready)
                continue;

            int levels = Math.Min(
                needs.Levels,
                Math.Min(MostLevels, TextureDescription.GetFullLevels(view.Width, view.Height))
            );

            Result<(GpuTexture Target, GpuTexture History)> targets = GetTargets(view, place, frameIndex, levels);
            if (!targets.Ok)
            {
                Log.Error($"The effect at {place} in the lists is not drawn: {targets.Error}");

                continue;
            }

            (GpuTexture target, GpuTexture history) = targets.Value;
            rows[_running.Count] = row;
            _running.Add(new RunningEffect(apply, level, levels, target, history, view.HasHistory && ranLastFrame));
            _lastFrame[place] = slot.Handle.Id;
            passCount += levels + 1;
        }

        int count = _running.Count;
        if (count == 0)
            return;

        Result<GpuBuffer> table = graph.Buffer(GpuDescriptions.GetBuffer("Effect rows", Places * GpuMaterial.Bytes));
        Result<GpuBuffer> passTable = graph.Buffer(
            GpuDescriptions.GetBuffer("Effect passes", Places * (MostLevels + 1) * Unsafe.SizeOf<GpuEffectPass>())
        );
        Result<GpuTexture> otherPicture = graph.Texture(
            GpuDescriptions.GetTexture(
                "Effect picture",
                view.Width,
                view.Height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (Result made in (ReadOnlySpan<Result>)[table, passTable, otherPicture, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The picture's effects are not drawn: {made.Error}");

                return;
            }
        }

        graph.Upload(table.Value, rows[..count]);
        Span<GpuEffectPass> passes = graph.Upload<GpuEffectPass>(passTable.Value, passCount);

        // Each effect's levels into its target, then its apply from one picture into the other, which the next reads.
        ReadOnlySpan<GpuResource> surface =
        [
            view.Constants,
            table.Value,
            passTable.Value,
            view.Visibility,
            view.Depth,
            view.GlassDepth,
            view.GBufferNormal,
            view.GBufferBase,
            view.GBufferSurface,
            view.Velocity,
        ];

        // What an effect's rays read of the world, on a GPU that traces them.
        ReadOnlySpan<GpuResource> rays = world is { } traced
            ? [traced.HitMaterials, resources.Meshes, view.Instances]
            : [];
        (GpuTexture picture, GpuTexture other) = (view.Hdr, otherPicture.Value);
        int pass = 0;
        for (int effect = 0; effect < count; effect++)
        {
            RunningEffect running = _running[effect];
            GpuEffectPass shared = new()
            {
                Rows = table.Value.Id,
                Row = (uint)effect,
                Picture = picture.Id,
                Target = running.Target.Id,
                History = running.History.Id,
                HasHistory = running.HasHistory ? 1u : 0u,
                Sampler = sampler.Value.Id,
            };
            for (int level = 0; level < running.Levels; level++)
            {
                passes[pass] = shared with { Level = (uint)level, Output = running.Target.Id };
                ComputePass fill = graph
                    .Compute(
                        "Fill effect level",
                        running.Level,
                        new GpuConstants(view.Constants.Id, passTable.Value.Id, (uint)pass),
                        RenderStage.Post
                    )
                    .Reads(surface)
                    .Reads(rays)
                    .Reads(picture, running.History);
                if (level > 0)
                    fill.Reads(running.Target, level - 1);

                fill.Writes(running.Target, level)
                    .DispatchOver(Math.Max(view.Width >> level, 1), Math.Max(view.Height >> level, 1));
                pass++;
            }

            passes[pass] = shared with { Output = other.Id };
            ComputePass applied = graph
                .Compute(
                    "Apply effect",
                    running.Apply,
                    new GpuConstants(view.Constants.Id, passTable.Value.Id, (uint)pass),
                    RenderStage.Post
                )
                .Reads(surface)
                .Reads(rays)
                .Reads(picture);
            if (running.Levels > 0)
                applied.Reads(running.Target, running.History);

            applied.Writes(other).DispatchOver(view.Width, view.Height);
            pass++;
            (picture, other) = (other, picture);
        }

        if (picture != view.Hdr)
            graph.Blit(picture, view.Hdr, RenderStage.Post);
    }

    /// <summary>
    /// Whether an effect of <paramref name="lists"/> that runs this frame asks for the camera's jitter.
    /// </summary>
    public bool HasJitter(RenderGraph graph, ReadOnlySpan<MaterialSlots> lists)
    {
        foreach (MaterialSlots list in lists)
        {
            foreach (MaterialInstance slot in list)
            {
                GpuState state = GetState(graph, slot, out _, out _, out _, out EffectNeeds needs);
                if (state == GpuState.Ready && needs.HasJitter)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an effect of <paramref name="lists"/> still loads its material, its textures or its passes. Each is asked
    /// for, so the first ask starts them all.
    /// </summary>
    public bool IsLoading(RenderGraph graph, ReadOnlySpan<MaterialSlots> lists)
    {
        bool isLoading = false;
        foreach (MaterialSlots list in lists)
        {
            foreach (MaterialInstance slot in list)
            {
                isLoading |= GetState(graph, slot, out _, out _, out _, out _) == GpuState.Loading;
            }
        }

        return isLoading;
    }

    /// <summary>
    /// The two targets of the effect at <paramref name="place"/> with <paramref name="levels"/>: the one frame
    /// <paramref name="frameIndex"/> fills, by its parity, and the one the frame before filled; none without levels.
    /// </summary>
    private static Result<(GpuTexture Target, GpuTexture History)> GetTargets(
        in RenderView view,
        int place,
        uint frameIndex,
        int levels
    )
    {
        if (levels == 0)
            return Result.Success((GpuTexture.None, GpuTexture.None));

        int filled = (2 * place) + (int)(frameIndex % 2);
        Result<GpuTexture> target = view.Graph.Texture(GetTarget(_targetNames[filled], view, levels));
        Result<GpuTexture> history = view.Graph.Texture(GetTarget(_targetNames[filled ^ 1], view, levels));
        if (!target.Ok || !history.Ok)
            return Result.Failure(target.Ok ? history.Error : target.Error);

        return Result.Success((target.Value, history.Value));
    }

    private static TextureDescription GetTarget(string name, in RenderView view, int levels) =>
        GpuDescriptions.GetTexture(
            name,
            view.Width,
            view.Height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled,
            levels
        );

    /// <summary>
    /// Where the effect in <paramref name="slot"/> is: ready to run once its material and its textures are on the GPU
    /// and its passes built, with its row, its passes and what it needs; loading while one of them loads; failed, never
    /// to run, when one failed, when it traces rays on a GPU that doesn't, or when the slot is empty.
    /// </summary>
    private GpuState GetState(
        RenderGraph graph,
        in MaterialInstance slot,
        out GpuMaterial row,
        out ShaderEntry apply,
        out ShaderEntry level,
        out EffectNeeds needs
    )
    {
        (row, apply, level, needs) = (default, default, default, default);
        if (slot.Handle == Handle<Material>.None)
            return GpuState.Failed;

        ResourceState materialState = resources.GetEffect(slot, out row, out ShaderTypeArgument shader, out needs);
        if (materialState != ResourceState.Ready)
            return materialState == ResourceState.Loading ? GpuState.Loading : GpuState.Failed;

        if (needs.HasRays && !graph.Features.HasRayQuery)
            return GpuState.Failed;

        apply = GetSpecialised(_applyShader, shader);
        level = GetSpecialised(_levelShader, shader);
        GpuState applied = graph.GetState(apply);
        GpuState levels = needs.Levels == 0 ? GpuState.Ready : graph.GetState(level);

        return applied == GpuState.Ready ? levels : applied;
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
    /// An effect a frame runs: its entries, the levels of its target, its target and its history, none without levels,
    /// and whether the history holds what it filled last frame.
    /// </summary>
    private readonly record struct RunningEffect(
        ShaderEntry Apply,
        ShaderEntry Level,
        int Levels,
        GpuTexture Target,
        GpuTexture History,
        bool HasHistory
    );
}
