using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.DeferredRendering;

/// <summary>
/// The frame's render failures over the top of the window, one line each, as "Shader failed Surfaces are not shaded":
/// it uploads each line's kind and text as character codes, and draws them in one pass in the overlay stage, after the
/// picture is on the window. It draws nothing while nothing failed, and nothing while its own shaders are not built.
/// Render thread.
/// </summary>
internal sealed class FailureBanner(AssetManager assets)
{
    /// <summary>
    /// The most lines drawn; the last says how many more there are when there are more.
    /// </summary>
    private const int MaxLines = 8;

    /// <summary>
    /// A line's words, <c>FailureBanner.slang</c>'s: the kind and the length of the text, then the text, 4 character
    /// codes in each.
    /// </summary>
    private const int LineWords = 32;

    /// <summary>
    /// A line's height in window pixels: 11 dots of the font, each 2 pixels.
    /// </summary>
    private const int LinePixels = 22;

    /// <summary>
    /// The name of each kind, by its value.
    /// </summary>
    private static readonly string[] _kinds = Enum.GetNames<RenderFailure>();

    private readonly DrawShaders _shaders = new(
        PassShaders.GetEntry(assets, "FailureBanner", "DrawFailureBannerVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "FailureBanner", "DrawFailureBannerPixel", ShaderStage.Pixel)
    )
    {
        Cull = CullMode.None,
    };

    /// <summary>
    /// The lines of <see cref="RenderGraph.Failures"/> over the window's image. The shaders are asked for every frame,
    /// so they are built before the first failure; while they are not built, or the banner's buffers are not made,
    /// it draws nothing and tells no failure, as no banner would show it.
    /// </summary>
    public void Record(RenderGraph graph, in Frame frame)
    {
        if (graph.GetState(_shaders.Vertex, _shaders.Pixel) != GpuState.Ready)
            return;

        ReadOnlySpan<(RenderFailure Kind, string What)> failures = graph.Failures;
        GpuTexture target = graph.WindowTarget;
        if (failures.IsEmpty || target.IsNone)
            return;

        Result<GpuBuffer> lines = graph.Buffer(new("Failure banner", MaxLines * LineWords * sizeof(uint)));
        Result<GpuBuffer> indices = graph.Buffer(new("Failure banner indices", 3 * sizeof(uint), BufferUsages.Index));
        if (!lines.Ok || !indices.Ok)
            return;

        int count = Math.Min(failures.Length, MaxLines);
        Span<uint> words = graph.Upload<uint>(lines.Value, count * LineWords);
        for (int i = 0; i < count; i++)
        {
            Span<uint> line = words.Slice(i * LineWords, LineWords);
            if (i == MaxLines - 1 && failures.Length > MaxLines)
                WriteMore(line, failures[i].Kind, failures.Length - i);
            else
                WriteLine(line, failures[i].Kind, failures[i].What);
        }

        graph.Upload<uint>(indices.Value, [0, 1, 2]);
        TextureDescription window = graph.GetDescription(target);
        GpuConstants constants = new(lines.Value.Id, BitConverter.SingleToUInt32Bits((float)frame.AppSec), (uint)count);
        graph
            .Draw(
                "Failure banner",
                _shaders,
                new GpuRenderTargets { Colors = [new GpuTarget { Texture = target }] },
                constants,
                RenderStage.Overlay
            )
            .Reads(lines.Value)
            .DrawIndexed(
                indices.Value,
                [
                    new IndexedDraw
                    {
                        Scissor = new Rectangle(0, 0, window.Width, Math.Min(count * LinePixels, window.Height)),
                        Constants = constants,
                        IndexCount = 3,
                    },
                ]
            );
    }

    /// <summary>
    /// "Kind failed what", cut short where the line is full.
    /// </summary>
    private static void WriteLine(Span<uint> line, RenderFailure kind, string what)
    {
        Span<byte> text = MemoryMarshal.AsBytes(line[1..]);
        int length = Write(text, 0, _kinds[(int)kind]);
        length = Write(text, length, " failed ");
        length = Write(text, length, what);
        line[0] = (uint)kind | ((uint)length << 8);
    }

    /// <summary>
    /// "and N more", in the colour of the first failure not drawn.
    /// </summary>
    private static void WriteMore(Span<uint> line, RenderFailure kind, int more)
    {
        Span<byte> text = MemoryMarshal.AsBytes(line[1..]);
        int length = Write(text, 0, "and ");
        _ = more.TryFormat(text[length..], out int digits, provider: CultureInfo.InvariantCulture);
        length = Write(text, length + digits, " more");
        line[0] = (uint)kind | ((uint)length << 8);
    }

    /// <summary>
    /// <paramref name="part"/>'s character codes from <paramref name="at"/> on, as many as fit and up to its first that
    /// is not ASCII; where the text ends.
    /// </summary>
    private static int Write(Span<byte> text, int at, string part)
    {
        _ = Ascii.FromUtf16(part, text[at..], out int written);

        return at + written;
    }
}
