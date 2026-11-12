using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Magic.Assets;
using Magic.Graphics;
using Magic.Projects;
using Magic.Utils;

namespace Magic.DeferredRendering;

/// <summary>
/// The frame's render failures over the top of the window, one line each, as "Shader failed Surfaces are not shaded",
/// but a post effect's, which shows as a frame round the window and a line along its bottom: it uploads each line's
/// kind and text as character codes, and draws them in one pass in the overlay stage, after the picture is on the
/// window. It draws nothing while nothing failed, and nothing while its own shaders are not built. Its shader is in the
/// gem's assembly, registered at its asset path, so no file is read to build it; a file at that path replaces it.
/// Render thread.
/// </summary>
internal sealed class FailureBanner(AssetManager assets) : IDisposable
{
    /// <summary>
    /// The asset path of the banner's shader, and the name of the assembly's resource that holds it.
    /// </summary>
    private const string ShaderPath = "Shaders/FailureBanner.slang";

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
    /// The words of each kind, by its value.
    /// </summary>
    private static readonly string[] _kinds = ["Pass", "Resource", "Shader", "Sky", "Weather", "Post effect"];

    // Registered before the shaders below find it by its path.
    private readonly Subscription _shader = assets.Register(ShaderPath, new Shader { Text = ReadShader() });

    private readonly DrawShaders _shaders = new(
        PassShaders.GetEntry(assets, "FailureBanner", "DrawFailureBannerVertex", ShaderStage.Vertex),
        PassShaders.GetEntry(assets, "FailureBanner", "DrawFailureBannerPixel", ShaderStage.Pixel)
    )
    {
        Cull = CullMode.None,
    };

    public void Dispose() => _shader.Dispose();

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

        Result<GpuBuffer> lines = graph.Buffer(new("Failure banner", (MaxLines + 1) * LineWords * sizeof(uint)));
        Result<GpuBuffer> indices = graph.Buffer(new("Failure banner indices", 3 * sizeof(uint), BufferUsages.Index));
        if (!lines.Ok || !indices.Ok)
            return;

        // The lines over the top, and after them the first post effect's line, when one failed.
        int top = 0;
        int post = -1;
        for (int i = 0; i < failures.Length; i++)
        {
            if (failures[i].Kind != RenderFailure.PostEffect)
                top++;
            else if (post < 0)
                post = i;
        }

        int count = Math.Min(top, MaxLines);
        Span<uint> words = graph.Upload<uint>(lines.Value, (count + (post < 0 ? 0 : 1)) * LineWords);
        int written = 0;
        for (int i = 0; written < count; i++)
        {
            if (failures[i].Kind == RenderFailure.PostEffect)
                continue;

            Span<uint> line = words.Slice(written * LineWords, LineWords);
            if (written == MaxLines - 1 && top > MaxLines)
                WriteMore(line, failures[i].Kind, top - written);
            else
                WriteLine(line, failures[i].Kind, failures[i].What);

            written++;
        }

        if (post >= 0)
            WriteLine(words.Slice(count * LineWords, LineWords), RenderFailure.PostEffect, failures[post].What);

        // The scissor keeps the draw to the top lines, or, with a post effect's line, the shader to its frame and line.
        graph.Upload<uint>(indices.Value, [0, 1, 2]);
        TextureDescription window = graph.GetDescription(target);
        Rectangle drawn =
            post < 0
                ? new Rectangle(0, 0, window.Width, Math.Min(count * LinePixels, window.Height))
                : new Rectangle(0, 0, window.Width, window.Height);
        GpuConstants constants = new(
            lines.Value.Id,
            BitConverter.SingleToUInt32Bits((float)frame.AppSec),
            (uint)count,
            post < 0 ? 0 : (uint)window.Width | ((uint)window.Height << 16)
        );
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
                        Scissor = drawn,
                        Constants = constants,
                        IndexCount = 3,
                    },
                ]
            );
    }

    /// <summary>
    /// The banner's shader, as the build put it in the assembly.
    /// </summary>
    private static string ReadShader()
    {
        using Stream stream =
            typeof(FailureBanner).Assembly.GetManifestResourceStream(ShaderPath)
            ?? throw new InvalidOperationException($"The renderer's assembly holds no {ShaderPath}.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
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
