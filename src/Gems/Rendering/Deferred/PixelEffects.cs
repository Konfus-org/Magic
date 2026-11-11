using System.Linq;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.Deferred;

/// <summary>
/// The effects run over the lit picture before the output, in order: each a material whose shader implements the pixel
/// effect contract, its row packed as a surface material's is, and passes of the entries specialised to its shader: one
/// for each level of its own target it declares, then its apply, which reads the picture the effects before it left and
/// writes the other of two, the last copied back into the HDR picture. An effect with levels has a target by its place
/// in the list; one that declares a history has two, which swap each frame, so the one it filled last frame is its
/// history. One that declares jitter has the camera jittered while it runs. An effect is left out while its material,
/// its textures or its passes load, and when one of them failed.
/// </summary>
internal sealed class PixelEffects
{
    /// <summary>
    /// The most levels an effect's target has: the storage levels the heap keeps of a texture.
    /// </summary>
    private const int MostLevels = 16;

    /// <summary>
    /// The names of the two targets of the effect at each place: one with a history fills them by the parity of the
    /// frame, one without fills the first.
    /// </summary>
    private static readonly string[] _targetNames =
    [
        .. Enumerable.Range(0, 2 * MaterialSlots.Capacity).Select(i => $"Effect {i}"),
    ];

    /// <summary>
    /// The effects a frame runs, in order: a reusable buffer for <see cref="Record"/>.
    /// </summary>
    private readonly List<RunningEffect> _running = [];

    /// <summary>
    /// The material the effect at each place was last frame, 0 where none ran: whether its history is its own.
    /// </summary>
    private readonly ulong[] _lastFrame = new ulong[MaterialSlots.Capacity];

    /// <summary>
    /// Runs the view's effects that are ready, in order, on frame <paramref name="frameIndex"/>.
    /// </summary>
    public void Record(in RenderView view, uint frameIndex)
    {
        RenderGraph graph = view.Graph;

        // The entries and targets of the effects ready to run, in order.
        _running.Clear();
        int passCount = 0;
        ReadOnlySpan<GpuEffect> effects = view.Gpu.Effects.Span;
        for (int place = 0; place < effects.Length; place++)
        {
            (ulong material, GpuState state, ShaderEntry apply, ShaderEntry level, EffectNeeds needs) = effects[place];
            bool ranLastFrame = _lastFrame[place] == material;
            _lastFrame[place] = 0;
            if (state != GpuState.Ready)
                continue;

            int levels = Math.Min(
                needs.Levels,
                Math.Min(MostLevels, TextureDescription.GetFullLevels(view.Gpu.Width, view.Gpu.Height))
            );

            // Its target and, with a history, the one the frame before filled, the two swapping by the frame's parity;
            // none without levels. Without a history it fills the same one each frame.
            int filled = (2 * place) + (needs.HasHistory ? (int)(frameIndex % 2) : 0);
            Result<GpuTexture> target =
                levels == 0
                    ? Result.Success(GpuTexture.None)
                    : graph.Texture(GetTarget(_targetNames[filled], view, levels));
            Result<GpuTexture> history =
                levels == 0 || !needs.HasHistory
                    ? Result.Success(GpuTexture.None)
                    : graph.Texture(GetTarget(_targetNames[filled ^ 1], view, levels));
            if (!target.Ok || !history.Ok)
            {
                Log.Error(
                    $"The effect at {place} in the list is not drawn: {(target.Ok ? history.Error : target.Error)}"
                );

                continue;
            }

            _running.Add(
                new RunningEffect(
                    place,
                    apply,
                    level,
                    levels,
                    target.Value,
                    history.Value,
                    needs.HasHistory && view.Gpu.HasHistory && ranLastFrame
                )
            );
            _lastFrame[place] = material;
            passCount += levels + 1;
        }

        int count = _running.Count;
        if (count == 0)
            return;

        Result<GpuBuffer> passTable = graph.Buffer(
            GpuDescriptions.GetBuffer(
                "Effect passes",
                MaterialSlots.Capacity * (MostLevels + 1) * Unsafe.SizeOf<GpuEffectPass>()
            )
        );
        Result<GpuTexture> otherPicture = graph.Texture(
            GpuDescriptions.GetTexture(
                "Effect picture",
                view.Gpu.Width,
                view.Gpu.Height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (Result made in (ReadOnlySpan<Result>)[passTable, otherPicture, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The picture's effects are not drawn: {made.Error}");

                return;
            }
        }

        Span<GpuEffectPass> passes = graph.Upload<GpuEffectPass>(passTable.Value, passCount);

        // Each effect's levels into its target, then its apply from one picture into the other, which the next reads.
        ReadOnlySpan<GpuResource> surface =
        [
            view.Gpu.Frame,
            view.Gpu.EffectRows,
            passTable.Value,
            view.Gpu.Depth,
            view.Gpu.GBufferNormal,
            view.Gpu.GBufferBase,
            view.Gpu.GBufferSurface,
            view.Gpu.Velocity,
        ];

        (GpuTexture picture, GpuTexture other) = (view.Gpu.Hdr, otherPicture.Value);
        int pass = 0;
        for (int effect = 0; effect < count; effect++)
        {
            RunningEffect running = _running[effect];
            GpuEffectPass shared = new()
            {
                Rows = view.Gpu.EffectRows.Id,
                Row = (uint)running.Place,
                Picture = picture.Id,
                Target = running.Target.Id,
                History = running.History.Id,
                HasHistory = running.HasHistory ? 1u : 0u,
                Sampler = sampler.Value.Id,
            };
            ReadOnlySpan<GpuResource> history = running.History == GpuTexture.None ? [] : [running.History];
            for (int level = 0; level < running.Levels; level++)
            {
                passes[pass] = shared with { Level = (uint)level, Output = running.Target.Id };
                ComputePass fill = graph
                    .Compute(
                        "Fill effect level",
                        running.Level,
                        new GpuConstants(view.Gpu.Frame.Id, passTable.Value.Id, (uint)pass),
                        RenderStage.Post
                    )
                    .Reads(surface)
                    .Reads(picture)
                    .Reads(history);
                if (level > 0)
                    fill.Reads(running.Target, level - 1);

                fill.Writes(running.Target, level)
                    .DispatchOver(Math.Max(view.Gpu.Width >> level, 1), Math.Max(view.Gpu.Height >> level, 1));
                pass++;
            }

            passes[pass] = shared with { Output = other.Id };
            ComputePass applied = graph
                .Compute(
                    "Apply effect",
                    running.Apply,
                    new GpuConstants(view.Gpu.Frame.Id, passTable.Value.Id, (uint)pass),
                    RenderStage.Post
                )
                .Reads(surface)
                .Reads(picture);
            if (running.Levels > 0)
                applied.Reads(running.Target).Reads(history);

            applied.Writes(other).DispatchOver(view.Gpu.Width, view.Gpu.Height);
            pass++;
            (picture, other) = (other, picture);
        }

        if (picture != view.Gpu.Hdr)
            graph.Blit(picture, view.Gpu.Hdr, RenderStage.Post, TextureFilter.Linear);
    }

    private static TextureDescription GetTarget(string name, in RenderView view, int levels) =>
        GpuDescriptions.GetTexture(
            name,
            view.Gpu.Width,
            view.Gpu.Height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled,
            levels
        );

    /// <summary>
    /// An effect a frame runs: its entries, the levels of its target, its target, none without levels, and its history,
    /// none unless it declares one, and whether the history holds what it filled last frame.
    /// </summary>
    private readonly record struct RunningEffect(
        int Place,
        ShaderEntry Apply,
        ShaderEntry Level,
        int Levels,
        GpuTexture Target,
        GpuTexture History,
        bool HasHistory
    );
}
