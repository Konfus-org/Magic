using System.Drawing;
using System.Numerics;
using Magic.Assets;
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
    /// The lux of the light under which the world is dark and the cover black, and over which it is bright and the
    /// cover <see cref="_bright"/>; the colour blends between them by the log of the lux.
    /// </summary>
    private const float DarkLux = 10; // PROTO

    private const float BrightLux = 10_000; // PROTO

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
    /// The colour of the cover as the screen shows it, picked from the world that loads last. Black before the first
    /// load.
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
    /// Picks the colour of the cover from <paramref name="sun"/>, the light that the world is lit by: black when it
    /// is dark, off-white when it is bright, and a blend between.
    /// </summary>
    /// <remarks>
    /// A light below the horizon gives no light. With no light (null), the renderer's white sun lights the open
    /// default sky, which shows bright, so the cover is off-white.
    /// </remarks>
    public void PickColor((Vector3 Direction, Vector3 Illuminance)? sun)
    {
        float lux = sun switch
        {
            null => BrightLux,
            { Direction.Y: > 0 } up => Vector3.Dot(up.Illuminance, new Vector3(0.2126f, 0.7152f, 0.0722f)),
            _ => 0,
        };
        float bright = Math.Clamp(
            MathF.Log10(MathF.Max(lux, DarkLux) / DarkLux) / MathF.Log10(BrightLux / DarkLux),
            0,
            1
        );
        _color = _bright * bright;
    }

    /// <summary>
    /// Records the cover over the window at <paramref name="opacity"/>, which the shader makes even to the eye.
    /// </summary>
    public void Record(RenderGraph graph, float opacity)
    {
        GpuTexture target = graph.WindowTarget;
        if (opacity <= 0 || target.IsNone || graph.GetState(_shaders.Vertex, _shaders.Pixel) != GpuState.Ready)
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
