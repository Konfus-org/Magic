using System.Linq;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.Deferred;

/// <summary>
/// The effects run over the lit picture before the output, the air's and then the picture's, in order: each a material
/// whose shader implements the pixel effect contract, its row packed as a surface material's is, and passes of the
/// entries specialised to its shader: one for each level of its own target it declares, then its apply, which reads the
/// picture the effects before it left and writes the other of two. An effect with levels has two targets by its place
/// in the lists, which swap each frame, so the one it filled last frame is its history, and one that declares jitter
/// has the camera jittered while it runs. One that upscales resolves the picture to the output's size, the window's,
/// and the effects after it work at that size, so the output draws from the last of them; with none, the last picture
/// is copied back into the HDR picture. An effect is left out while its material, its textures or its passes load, and
/// when one of them failed.
/// </summary>
internal sealed class PixelEffects
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

    /// <summary>
    /// The effects a frame runs, in order: a reusable buffer for <see cref="Record"/>.
    /// </summary>
    private readonly List<RunningEffect> _running = [];

    /// <summary>
    /// The material the effect at each place was last frame and the size it worked at, none where none ran: whether its
    /// history is its own.
    /// </summary>
    private readonly (ulong Material, (int Width, int Height) Size)[] _lastFrame = new (ulong, (int, int))[Places];

    /// <summary>
    /// Runs the view's effects that are ready, in order, on frame <paramref name="frameIndex"/>, their rays through
    /// <paramref name="traced"/> where there is one, and gives the picture they finished: the HDR picture, or one of the
    /// output's size where an effect upscaled it.
    /// </summary>
    public GpuTexture Record(in RenderView view, TracedWorld? traced, uint frameIndex)
    {
        RenderGraph graph = view.Graph;

        // What an effect that upscales resolves the picture to: the window's size, which the view never exceeds, or the
        // view's where there is no window.
        (int Width, int Height) pictureSize = (view.Gpu.Width, view.Gpu.Height);
        (int Width, int Height) output = pictureSize;
        if (!graph.WindowTarget.IsNone)
        {
            TextureDescription window = graph.GetDescription(graph.WindowTarget);
            output = (window.Width, window.Height);
        }

        // The entries and targets of the effects ready to run, in order, each at the size it works at: the output's
        // from the effect that upscales on. An effect's history is its own where it ran last frame at that size.
        _running.Clear();
        int passCount = 0;
        int upscaledCount = 0;
        (int Width, int Height) size = pictureSize;
        ReadOnlySpan<GpuEffect> effects = view.Gpu.Effects.Span;
        for (int place = 0; place < effects.Length; place++)
        {
            (ulong material, GpuState state, ShaderEntry apply, ShaderEntry level, EffectNeeds needs) = effects[place];
            (ulong Material, (int Width, int Height) Size) lastFrame = _lastFrame[place];
            _lastFrame[place] = default;
            if (state != GpuState.Ready)
                continue;

            (int Width, int Height) effectSize = needs.Upscales ? output : size;
            int levels = Math.Min(
                needs.Levels,
                Math.Min(MostLevels, TextureDescription.GetFullLevels(effectSize.Width, effectSize.Height))
            );

            Result<(GpuTexture Target, GpuTexture History)> targets = GetTargets(
                view,
                place,
                frameIndex,
                levels,
                effectSize
            );
            if (!targets.Ok)
            {
                Log.Error($"The effect at {place} in the lists is not drawn: {targets.Error}");

                continue;
            }

            (GpuTexture target, GpuTexture history) = targets.Value;
            bool hasHistory = view.Gpu.HasHistory && lastFrame == (material, effectSize);
            _running.Add(new RunningEffect(place, apply, level, levels, effectSize, target, history, hasHistory));
            _lastFrame[place] = (material, effectSize);
            passCount += levels + 1;
            size = effectSize;
            if (effectSize != pictureSize)
                upscaledCount++;
        }

        int count = _running.Count;
        if (count == 0)
            return view.Gpu.Hdr;

        // Two pictures at each size the effects work at, the HDR picture one of the picture's, asked for while used.
        Result<GpuBuffer> passTable = graph.Buffer(
            GpuDescriptions.GetBuffer("Effect passes", Places * (MostLevels + 1) * Unsafe.SizeOf<GpuEffectPass>())
        );
        Result<GpuTexture> otherPicture = GetPicture(graph, "Effect picture", pictureSize, count > upscaledCount);
        Result<GpuTexture> upscaled = GetPicture(graph, "Upscaled picture", output, upscaledCount > 0);
        Result<GpuTexture> upscaledOther = GetPicture(graph, "Other upscaled picture", output, upscaledCount > 1);
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        foreach (Result made in (ReadOnlySpan<Result>)[passTable, otherPicture, upscaled, upscaledOther, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The picture's effects are not drawn: {made.Error}");

                return view.Gpu.Hdr;
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
            view.Gpu.GlassDepth,
            view.Gpu.GBufferNormal,
            view.Gpu.GBufferBase,
            view.Gpu.GBufferSurface,
            view.Gpu.Velocity,
        ];

        // What an effect's rays read of the world, on a GPU that traces them.
        ReadOnlySpan<GpuResource> rays = traced is { } world
            ? [world.HitMaterials, view.Gpu.Meshes, view.Gpu.Instances]
            : [];
        (GpuTexture picture, GpuTexture other) = (view.Gpu.Hdr, otherPicture.Value);
        size = pictureSize;
        int pass = 0;
        for (int effect = 0; effect < count; effect++)
        {
            // The effect that upscales writes the first picture of the output's size, the one after it the other.
            RunningEffect running = _running[effect];
            bool isUpscaling = running.Size != size;
            if (isUpscaling)
                other = upscaled.Value;

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
                    .Reads(rays)
                    .Reads(picture, running.History);
                if (level > 0)
                    fill.Reads(running.Target, level - 1);

                fill.Writes(running.Target, level)
                    .DispatchOver(Math.Max(running.Size.Width >> level, 1), Math.Max(running.Size.Height >> level, 1));
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
                .Reads(rays)
                .Reads(picture);
            if (running.Levels > 0)
                applied.Reads(running.Target, running.History);

            applied.Writes(other).DispatchOver(running.Size.Width, running.Size.Height);
            pass++;
            (picture, other) = (other, isUpscaling ? upscaledOther.Value : picture);
            size = running.Size;
        }

        if (size != pictureSize)
            return picture;

        if (picture != view.Gpu.Hdr)
            graph.Blit(picture, view.Gpu.Hdr, RenderStage.Post);

        return view.Gpu.Hdr;
    }

    /// <summary>
    /// The picture under <paramref name="name"/> at <paramref name="size"/> where it is needed, else none.
    /// </summary>
    private static Result<GpuTexture> GetPicture(
        RenderGraph graph,
        string name,
        (int Width, int Height) size,
        bool isNeeded
    ) =>
        isNeeded
            ? graph.Texture(
                GpuDescriptions.GetTexture(
                    name,
                    size.Width,
                    size.Height,
                    GpuFormat.R16G16B16A16Sfloat,
                    TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
                )
            )
            : Result.Success(GpuTexture.None);

    /// <summary>
    /// The two targets of the effect at <paramref name="place"/> with <paramref name="levels"/>, the first at
    /// <paramref name="size"/>: the one frame <paramref name="frameIndex"/> fills, by its parity, and the one the frame
    /// before filled; none without levels.
    /// </summary>
    private static Result<(GpuTexture Target, GpuTexture History)> GetTargets(
        in RenderView view,
        int place,
        uint frameIndex,
        int levels,
        (int Width, int Height) size
    )
    {
        if (levels == 0)
            return Result.Success((GpuTexture.None, GpuTexture.None));

        int filled = (2 * place) + (int)(frameIndex % 2);
        Result<GpuTexture> target = view.Graph.Texture(GetTarget(_targetNames[filled], size, levels));
        Result<GpuTexture> history = view.Graph.Texture(GetTarget(_targetNames[filled ^ 1], size, levels));
        if (!target.Ok || !history.Ok)
            return Result.Failure(target.Ok ? history.Error : target.Error);

        return Result.Success((target.Value, history.Value));
    }

    private static TextureDescription GetTarget(string name, (int Width, int Height) size, int levels) =>
        GpuDescriptions.GetTexture(
            name,
            size.Width,
            size.Height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled,
            levels
        );

    /// <summary>
    /// An effect a frame runs: its entries, the levels of its target, the size it works at, its target and its history,
    /// none without levels, and whether the history holds what it filled last frame.
    /// </summary>
    private readonly record struct RunningEffect(
        int Place,
        ShaderEntry Apply,
        ShaderEntry Level,
        int Levels,
        (int Width, int Height) Size,
        GpuTexture Target,
        GpuTexture History,
        bool HasHistory
    );
}
