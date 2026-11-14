using System.Drawing;
using System.Numerics;
using Magic.Assets;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.DeferredRendering;

/// <summary>
/// Draws one colour over the whole window, as <see cref="Worlds.World.CoverOpacity"/> hides it during a load.
/// </summary>
/// <remarks>
/// It draws after the views' output and before the overlay stage, so the debug UI and the failure banner show over it.
/// It draws nothing at an opacity of 0, with no window, or while its shaders are not built. Render thread.
/// </remarks>
internal sealed class Cover(AssetManager assets)
{
    /// <summary>
    /// The stage of the cover: after <see cref="RenderStages.Post"/>, before <see cref="RenderStage.Overlay"/>.
    /// </summary>
    private static readonly RenderStage _stage = new(5);

    /// <summary>
    /// The colour over a bright world: an off-white, as the screen shows it.
    /// </summary>
    private static readonly Vector3 _bright = new(0.9f); // PROTO

    private readonly DrawShaders _shaders = new(
        PassShaders.GetEntry(assets, "DrawCover", "DrawCoverVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "DrawCover", "DrawCoverPixel", ShaderStage.Pixel)
    )
    {
        Blend = BlendMode.Alpha,
        Cull = CullMode.None,
    };

    /// <summary>
    /// The colour of the cover as the screen shows it, picked from the world that loads. Black until it is picked.
    /// </summary>
    private Vector3 _color;

    /// <summary>
    /// Fills the window with black before the views draw into it, with no shader.
    /// </summary>
    /// <remarks>
    /// A part of the window that no view draws this frame, or the whole window while the world's step draws nothing,
    /// then shows black and not an older frame that the overlay draws over.
    /// </remarks>
    public static void ClearWindow(RenderGraph graph)
    {
        GpuTexture window = graph.WindowTarget;
        if (window.IsNone)
            return;

        Result<GpuTexture> black = graph.Texture(
            new("Black", 1, 1, GpuFormat.R8G8B8A8Unorm, TextureUsages.CopyDestination | TextureUsages.CopySource)
        );
        if (black.Ok && graph.Upload(black.Value, [0, 0, 0, 255]))
            graph.Blit(black.Value, window, RenderStage.Prepare, TextureFilter.Nearest);
    }

    /// <summary>
    /// Picks the colour of the cover from <paramref name="sun"/>, the light that the world is lit by: off-white when
    /// it is a sun or a light alone, above the horizon, that gives light, and black otherwise.
    /// </summary>
    /// <remarks>
    /// With no light (null), the renderer's white sun lights the open default sky, which shows bright, so the cover
    /// is off-white.
    /// </remarks>
    public void PickColor((Vector3 Direction, Vector3 Illuminance, DirectionalLightMode Mode)? sun)
    {
        bool isDay =
            sun is not { } light
            || (light.Direction.Y > 0 && light.Mode != DirectionalLightMode.Moon && light.Illuminance != Vector3.Zero);
        _color = isDay ? _bright : Vector3.Zero;
    }

    /// <summary>
    /// Makes the cover black until the next <see cref="PickColor"/>.
    /// </summary>
    public void MakeBlack() => _color = Vector3.Zero;

    /// <summary>
    /// Gets whether a shader of the cover is still being built; a failed one counts as done.
    /// </summary>
    public bool IsLoading(RenderGraph graph) => graph.IsAnyLoading(_shaders.Vertex, _shaders.Pixel);

    /// <summary>
    /// Records the cover over the window at <paramref name="opacity"/>, which the shader makes even to the eye.
    /// </summary>
    /// <remarks>
    /// It asks for its shaders each frame, at any opacity, so they are built before the first fade.
    /// </remarks>
    public void Record(RenderGraph graph, float opacity)
    {
        GpuState shaders = graph.GetState(_shaders.Vertex, _shaders.Pixel);
        if (shaders == GpuState.Failed)
            graph.Fail(RenderFailure.Shader, "The load is not covered", "a shader of the cover was not built.");

        GpuTexture target = graph.WindowTarget;
        if (opacity <= 0 || target.IsNone || shaders != GpuState.Ready)
            return;

        Result<GpuBuffer> indices = graph.Buffer(new("Cover indices", 3 * sizeof(uint), BufferUsages.Index));
        if (!indices.Ok || !graph.Upload<uint>(indices.Value, [0, 1, 2]))
            return;

        GpuConstants constants = new(
            BitConverter.SingleToUInt32Bits(_color.X),
            BitConverter.SingleToUInt32Bits(_color.Y),
            BitConverter.SingleToUInt32Bits(_color.Z),
            BitConverter.SingleToUInt32Bits(Math.Min(opacity, 1))
        );
        TextureDescription window = graph.GetDescription(target);
        IndexedDraw draw = new()
        {
            Scissor = new Rectangle(0, 0, window.Width, window.Height),
            Constants = constants,
            IndexCount = 3,
        };
        graph
            .Draw(
                "Cover",
                _shaders,
                new GpuRenderTargets { Colors = [new GpuTarget { Texture = target }] },
                constants,
                _stage
            )
            .DrawIndexed(indices.Value, [draw]);
    }
}
