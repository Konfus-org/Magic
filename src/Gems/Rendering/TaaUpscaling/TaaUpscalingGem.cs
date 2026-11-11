using Magic.Assets;
using Magic.Debugging;
using Magic.GpuWorld;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.TaaUpscaling;

/// <summary>
/// Takes the finished picture to the size it is shown at with temporal anti-aliasing: each frame it makes the
/// <see cref="Upscaling"/>, so the camera jitters, and the renderer has it record one pass once the view's output is
/// drawn. The pass builds each shown pixel from the output's jittered texels around it and blends it with last
/// frame's resolved picture where its surface was, so the gem keeps two resolved pictures and swaps them every frame,
/// with no history on the first frame at a size or of a world; the newer is what the window shows. It makes nothing
/// while its shader builds. It is public because nothing in its assembly makes it: Magic does, by
/// reflection, when a project lists the gem.
/// </summary>
public sealed class TaaUpscalingGem : IGem, IRenderInto<Upscaling>
{
    private readonly ShaderEntry _shader;

    /// <summary>
    /// The one upscaling every frame makes, so a frame makes no delegate.
    /// </summary>
    private readonly Upscaling _upscaling;

    /// <summary>
    /// The picture the frame drawn last resolved, the next one's history, and its size.
    /// </summary>
    private (GpuTexture Picture, int Width, int Height) _resolved;

    public TaaUpscalingGem(AssetManager assets)
    {
        _shader = new()
        {
            Module = assets.Find<Shader>("Shaders/TaaUpscaling.slang"),
            Name = "UpscalePicture",
            Stage = ShaderStage.Compute,
        };
        _upscaling = new Upscaling(Record);
    }

    /// <summary>
    /// The frame's upscaling; none while the shader builds.
    /// </summary>
    public Upscaling? Render(in Frame frame, RenderGraph graph) =>
        graph.GetState(_shader) != GpuState.Ready ? null : _upscaling;

    private static TextureDescription GetResolved(string name, int width, int height) =>
        GpuDescriptions.GetTexture(
            name,
            width,
            height,
            GpuFormat.R16G16B16A16Sfloat,
            TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource
        );

    /// <summary>
    /// The pass that resolves the view's output at the size it is shown at, and the picture it resolves; none when
    /// its pictures were not made.
    /// </summary>
    private GpuTexture Record(RenderGraph graph, GpuView view)
    {
        (int width, int height) = (view.ShownWidth, view.ShownHeight);
        Result<GpuTexture> first = graph.Texture(GetResolved("Resolved picture", width, height));
        Result<GpuTexture> second = graph.Texture(GetResolved("Other resolved picture", width, height));
        foreach (Result made in (ReadOnlySpan<Result>)[first, second])
        {
            if (!made.Ok)
            {
                Log.Error($"The picture is not upscaled: {made.Error}");

                return GpuTexture.None;
            }
        }

        // Last frame's resolved picture is this frame's history, where last frame drew this view at this size; the
        // shader reads none where the history it is handed is the picture it resolves.
        (GpuTexture history, GpuTexture resolved) =
            _resolved.Picture == first.Value ? (first.Value, second.Value) : (second.Value, first.Value);
        bool hasHistory = view.HasHistory && _resolved == (history, width, height);
        _resolved = (resolved, width, height);
        ComputePass pass = graph
            .Compute(
                "Upscale picture",
                _shader,
                new GpuConstants(view.Frame.Id, hasHistory ? history.Id : resolved.Id, resolved.Id),
                RenderStage.Post
            )
            .Reads(view.Frame, view.Output, view.Depth, view.Velocity);
        if (hasHistory)
            pass.Reads(history);

        pass.Writes(resolved).DispatchOver(width, height);

        return resolved;
    }
}
