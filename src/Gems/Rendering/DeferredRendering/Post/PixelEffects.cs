using System.Linq;
using System.Runtime.CompilerServices;
using Magic.Assets;
using Magic.Debugging;
using Magic.DeferredRendering.View;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering.Post;

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
    /// The material the effect at each place was last frame, 0 where none ran: whether its history is its own.
    /// </summary>
    private readonly ulong[] _lastFrame = new ulong[MaterialSlots.Capacity];

    /// <summary>
    /// Runs the view's effects that are ready, in order, on frame <paramref name="frameIndex"/>.
    /// </summary>
    public void Record(RenderGraph graph, in GpuView gpu, uint frameIndex)
    {
        Result<GpuBuffer> passTable = graph.Buffer(
            new("Effect passes", MaterialSlots.Capacity * (MostLevels + 1) * Unsafe.SizeOf<GpuEffectPass>())
        );
        Result<GpuTexture> otherPicture = graph.Texture(
            new(
                "Effect picture",
                gpu.Width,
                gpu.Height,
                GpuFormat.R16G16B16A16Sfloat,
                TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
            )
        );
        Result<GpuSampler> sampler = graph.Sampler(
            new SamplerDescription { Wrap = TextureWrap.Clamp, MipFilter = TextureFilter.Nearest }
        );
        if (Result.FindFailure(passTable, otherPicture, sampler) is { Ok: false } failed)
        {
            Log.Error($"The picture's effects are not drawn: {failed.Error}");

            return;
        }

        ReadOnlySpan<GpuResource> surface =
        [
            gpu.Frame,
            gpu.EffectRows,
            passTable.Value,
            gpu.Depth,
            gpu.GBufferNormal,
            gpu.GBufferBase,
            gpu.GBufferSurface,
            gpu.Velocity,
        ];

        // Each effect that is ready: its levels into its target, then its apply from one picture into the other, which
        // the next reads.
        Span<GpuEffectPass> passes = stackalloc GpuEffectPass[MaterialSlots.Capacity * (MostLevels + 1)];
        int pass = 0;
        (GpuTexture picture, GpuTexture other) = (gpu.Hdr, otherPicture.Value);
        ReadOnlySpan<GpuEffect> effects = gpu.Effects.Span;
        for (int place = 0; place < effects.Length; place++)
        {
            (ulong material, GpuState state, ShaderEntry apply, ShaderEntry levelEntry, EffectNeeds needs) = effects[
                place
            ];
            bool ranLastFrame = _lastFrame[place] == material;
            _lastFrame[place] = 0;
            if (state != GpuState.Ready)
                continue;

            int levels = Math.Min(
                needs.Levels,
                Math.Min(MostLevels, TextureDescription.GetFullLevels(gpu.Width, gpu.Height))
            );

            // Its target and, with a history, the one the frame before filled, the two swapping by the frame's parity;
            // none without levels. Without a history it fills the same one each frame.
            int filled = (2 * place) + (needs.HasHistory ? (int)(frameIndex % 2) : 0);
            Result<GpuTexture> target =
                levels == 0
                    ? Result.Success(GpuTexture.None)
                    : graph.Texture(GetTarget(_targetNames[filled], gpu, levels));
            Result<GpuTexture> history =
                levels == 0 || !needs.HasHistory
                    ? Result.Success(GpuTexture.None)
                    : graph.Texture(GetTarget(_targetNames[filled ^ 1], gpu, levels));
            if (!target.Ok || !history.Ok)
            {
                Log.Error(
                    $"The effect at {place} in the list is not drawn: {(target.Ok ? history.Error : target.Error)}"
                );

                continue;
            }

            _lastFrame[place] = material;
            GpuEffectPass shared = new()
            {
                Rows = gpu.EffectRows.Id,
                Row = (uint)place,
                Picture = picture.Id,
                Target = target.Value.Id,
                History = history.Value.Id,
                HasHistory = needs.HasHistory && gpu.HasHistory && ranLastFrame ? 1u : 0u,
                Sampler = sampler.Value.Id,
            };
            for (int level = 0; level < levels; level++)
            {
                passes[pass] = shared with { Level = (uint)level, Output = target.Value.Id };
                ComputePass fill = graph
                    .Compute(
                        "Fill effect level",
                        levelEntry,
                        new GpuConstants(gpu.Frame.Id, passTable.Value.Id, (uint)pass),
                        RenderStages.Post
                    )
                    .Reads(surface)
                    .Reads(picture)
                    .Reads(history.Value);
                if (level > 0)
                    fill.Reads(target.Value, level - 1);

                fill.Writes(target.Value, level)
                    .DispatchOver(Math.Max(gpu.Width >> level, 1), Math.Max(gpu.Height >> level, 1));
                pass++;
            }

            passes[pass] = shared with { Output = other.Id };
            ComputePass applied = graph
                .Compute(
                    "Apply effect",
                    apply,
                    new GpuConstants(gpu.Frame.Id, passTable.Value.Id, (uint)pass),
                    RenderStages.Post
                )
                .Reads(surface)
                .Reads(picture);
            if (levels > 0)
                applied.Reads(target.Value).Reads(history.Value);

            applied.Writes(other).DispatchOver(gpu.Width, gpu.Height);
            pass++;
            (picture, other) = (other, picture);
        }

        graph.Upload(passTable.Value, passes[..pass]);
        if (picture != gpu.Hdr)
            graph.Blit(picture, gpu.Hdr, RenderStages.Post, TextureFilter.Linear);
    }

    private static TextureDescription GetTarget(string name, in GpuView gpu, int levels) =>
        new(
            name,
            gpu.Width,
            gpu.Height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled,
            levels
        );
}
