using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;
using Magic.Interfaces;

namespace Magic.ClusterRendering;

/// <summary>
/// What failed this frame, in words across each view: the passes that do not run and the pipelines that do not load,
/// which the listing says (<see cref="Listing.Failures"/>), and what only the GPU finds wrong, which a shader raises as a
/// code in <see cref="Raised"/> (Include/Failure.hlsli), a word each, emptied every frame. The lines are packed as glyphs
/// of the text a shader draws itself into <see cref="Text"/>, laid out as Include/FailureBanner.hlsli reads it, which
/// the output draws over each view's finished image. Render thread.
/// </summary>
internal sealed class Failures(IGpu gpu) : IDisposable
{
    /// <summary>
    /// Codes a shader can raise, and glyphs a row holds, as Include/Failure.hlsli and Include/FailureBanner.hlsli
    /// number them.
    /// </summary>
    public const int CodeCount = 8;
    public const int LineGlyphs = 96;

    /// <summary>
    /// Rows the listing's failures fill at most, each wrapped over as many as it takes: the rest are in the log.
    /// </summary>
    private const int MaxLines = 24;

    /// <summary>
    /// The words of each code, in the order Include/Failure.hlsli numbers them: change both together.
    /// </summary>
    private static readonly string[] _codes =
    [
        "NaN or infinity in the lit scene",
        "A material row past the materials there are",
    ];

    /// <summary>
    /// The pulse's floor and period, as Include/Failure.hlsli has them.
    /// </summary>
    private const float PulseMin = 0.05f;
    private const float PulsePeriod = 2f;

    private readonly GpuArray<uint> _text = new(gpu, GpuBufferUsage.GraphicsRead, 1 + ((MaxLines + CodeCount) * LineGlyphs));
    private readonly List<string> _lines = [];
    private readonly List<string> _rows = [];
    private bool _isPacked;

    public void Dispose()
    {
        _text.Dispose();
        gpu.Release(Raised);
    }

    /// <summary>
    /// A word per code, 1 once a shader raised it this frame: the frame's <c>Failures</c>.
    /// </summary>
    public GpuBuffer Raised { get; } = gpu.CreateBuffer(GpuBufferUsage.ComputeRead | GpuBufferUsage.ComputeWrite | GpuBufferUsage.GraphicsRead, RaisedBytes);

    public static uint RaisedBytes => CodeCount * sizeof(uint);

    /// <summary>
    /// The lines and the codes' words as glyphs: the frame's <c>FailureText</c>.
    /// </summary>
    public GpuBuffer Text => _text.Buffer;

    /// <summary>
    /// The failure's colour as it breathes at <paramref name="time"/>, as Include/Failure.hlsli has it: what a target a
    /// pass that never runs would draw into is cleared to.
    /// </summary>
    public static Color PulseColor(float time)
    {
        float wave = (MathF.Sin(time * 2f * MathF.PI / PulsePeriod) * 0.5f) + 0.5f;
        float pulse = PulseMin + ((1f - PulseMin) * wave);
        return new Color(pulse, 0f, pulse);
    }

    /// <summary>
    /// The lines said this frame in place of the last frame's, each wrapped at its spaces over as many rows of
    /// <see cref="LineGlyphs"/> as it takes, the first <see cref="MaxLines"/> rows of them; packed again only when they
    /// changed.
    /// </summary>
    public void Say(IReadOnlyList<string> lines)
    {
        if (_isPacked && IsSaid(lines))
        {
            return;
        }

        _lines.Clear();
        _lines.AddRange(lines);
        _rows.Clear();
        foreach (string line in _lines)
        {
            Wrap(line);
        }

        if (_rows.Count > MaxLines)
        {
            _rows.RemoveRange(MaxLines, _rows.Count - MaxLines);
        }

        _text.Set(0, (uint)_rows.Count);
        for (int i = 0; i < _rows.Count; i++)
        {
            Write(_rows[i], 1 + (i * LineGlyphs));
        }

        for (int code = 0; code < CodeCount; code++)
        {
            Write(code < _codes.Length ? _codes[code] : string.Empty, 1 + ((_rows.Count + code) * LineGlyphs));
        }

        _isPacked = true;
    }

    /// <summary>
    /// Uploads the words and empties the raised codes, before the frame's passes run, so a code shows the frame it is
    /// raised; <paramref name="zeros"/> is a buffer of zeros at least <see cref="RaisedBytes"/> long.
    /// </summary>
    public void Begin(GpuBuffer zeros)
    {
        _text.Flush();
        gpu.Commands.CopyBuffer(zeros, 0, Raised, 0, RaisedBytes);
    }

    private bool IsSaid(IReadOnlyList<string> lines)
    {
        if (lines.Count != _lines.Count)
        {
            return false;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            if (!string.Equals(lines[i], _lines[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adds a line to the rows, broken at its spaces into rows of at most <see cref="LineGlyphs"/>; a word longer than
    /// a row is broken where the row ends.
    /// </summary>
    private void Wrap(string line)
    {
        int start = 0;
        while (start < line.Length)
        {
            int end = Math.Min(start + LineGlyphs, line.Length);
            if (end < line.Length)
            {
                int space = line.LastIndexOf(' ', end, end - start);
                if (space > start)
                {
                    end = space;
                }
            }

            _rows.Add(line[start..end]);
            start = end;
            while (start < line.Length && line[start] == ' ')
            {
                start++;
            }
        }
    }

    /// <summary>
    /// A line as the glyphs the font has, capitals, digits, an exclamation mark and space (Include/DebugText.hlsli),
    /// from <paramref name="first"/>: a small letter as its capital, anything else a space, and spaces past its end.
    /// </summary>
    private void Write(string line, int first)
    {
        for (int i = 0; i < LineGlyphs; i++)
        {
            char letter = i < line.Length ? char.ToUpperInvariant(line[i]) : ' ';
            uint glyph = letter switch
            {
                >= '0' and <= '9' => (uint)(letter - '0'),
                >= 'A' and <= 'Z' => 10u + (uint)(letter - 'A'),
                '!' => 36u,
                _ => 37u,
            };
            _text.Set(first + i, glyph);
        }
    }
}
