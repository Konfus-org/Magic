using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// What a failure looks like, where it is said and how it is shown, decided in one place. What stands in for what
/// broke: the failure model and the failure materials by what broke (<see cref="Model"/>, <see cref="GetMaterial"/>),
/// which the residents hold from the start. The surfaces that do not compile, whose materials are drawn as the failure
/// until their shader changes (<see cref="Break"/>). What does not load, said in the log as it is found
/// (<see cref="Report"/>). And what is shown: in words across each view, the passes that are left out, which the
/// pipelines say (<see cref="GpuPipelines.Failed"/>), the world's own failures after them, and what only the GPU finds
/// wrong, which a shader raises as a code (Include/Failure.hlsli), packed as glyphs a shader draws itself
/// (<see cref="Text"/>, laid out as Include/FailureBanner.hlsli reads it, which the output draws over each view's
/// finished image); and while the debug UI shows, at each instance drawn as the failure, why (<see cref="Label"/>).
/// Render thread.
/// </summary>
internal sealed class FailureHandler
{
    /// <summary>
    /// Codes a shader can raise, as Include/Failure.hlsli numbers them.
    /// </summary>
    internal const int CodeCount = 8;

    /// <summary>
    /// Words of <see cref="Text"/>: how many rows the lines fill, then every row's glyphs, the codes' words after the
    /// lines.
    /// </summary>
    internal const int TextWords = 1 + ((MaxLines + CodeCount) * LineGlyphs);

    /// <summary>
    /// Glyphs a row holds, as Include/FailureBanner.hlsli lays them out.
    /// </summary>
    private const int LineGlyphs = 96;

    /// <summary>
    /// Rows the failures fill at most, each wrapped over as many as it takes: the rest are in the log.
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

    private readonly Assets _assets;
    private readonly IDebugUI? _debug;

    /// <summary>
    /// The failure materials, by what broke.
    /// </summary>
    private readonly Dictionary<FailureType, Handle<Material>> _materials = [];

    /// <summary>
    /// The instances drawn as the failure, by slot: why, in a few words, and what failed, by its path.
    /// </summary>
    private readonly Dictionary<int, (string Why, string What)> _marked = [];
    private readonly HashSet<Handle<Shader>> _broken = [];
    private readonly HashSet<Handle<Shader>> _broke = [];
    private readonly List<string> _rows = [];
    private readonly uint[] _text = new uint[TextWords];

    /// <summary>
    /// The lines last said, and how many there were: the pipelines' own list, which only grows until a shader changes,
    /// so the same list with the same count is the same lines; and the sky's line said after them, which is made again
    /// only when it changes, so the same string is the same line.
    /// </summary>
    private IReadOnlyList<string>? _said;
    private int _saidCount;
    private string? _saidSky;

    /// <summary>
    /// The world's sky material drawn as the failure, and why, for the banner; none while the sky is drawn as it is.
    /// </summary>
    private Handle<Material> _brokenSky;
    private string? _skyLine;

    internal FailureHandler(Assets assets, IDebugUI? debug)
    {
        _assets = assets;
        _debug = debug;
        Model = assets.GetFailure<Model>(FailureType.Mesh);
        foreach (FailureType type in Enum.GetValues<FailureType>())
        {
            _materials[type] = assets.GetFailure<Material>(type);
        }
    }

    /// <summary>
    /// The model a renderer whose model does not load is drawn with, in the mesh failure material: the failure cube.
    /// </summary>
    internal Handle<Model> Model { get; }

    /// <summary>
    /// The failure materials, each held from the start.
    /// </summary>
    internal IEnumerable<Handle<Material>> Materials => _materials.Values;

    /// <summary>
    /// The banner as glyphs, the lines then the codes' words, packed again when what is said changes
    /// (<see cref="TextVersion"/>).
    /// </summary>
    internal ReadOnlySpan<uint> Text => _text;

    /// <summary>
    /// Counts each time <see cref="Text"/> is packed again, so what sends it sends it only then.
    /// </summary>
    internal int TextVersion { get; private set; }

    /// <summary>
    /// Says that <paramref name="what"/> does not load, and why: in the log, as it is found.
    /// </summary>
    internal static void Report(string what, string why) => Debugging.Error($"{what}: {why}");

    /// <summary>
    /// The material drawn in a broken one's place when <paramref name="type"/> broke: what a model that does not load
    /// is drawn with in every slot, for the mesh failure.
    /// </summary>
    internal Handle<Material> GetMaterial(FailureType type) => _materials[type];

    /// <summary>
    /// Whether <paramref name="material"/> is a failure material, which the broken materials waiting for it are drawn
    /// as once it is.
    /// </summary>
    internal bool IsMaterial(Handle<Material> material) => _materials.ContainsValue(material);

    /// <summary>
    /// A surface does not compile for <paramref name="pass"/>: said, and its materials drawn as the failure until its
    /// shader changes (<see cref="Repair"/>). None, the renderer's own surface, is said alone.
    /// </summary>
    internal void Break(Handle<Shader> surface, string pass, string why)
    {
        Debugging.Error(
            $"A surface does not compile for pass {pass}, so its materials are drawn as the failure: {why}");

        if (surface == Handle<Shader>.None) return;

        _broken.Add(surface);
        _broke.Add(surface);
    }

    /// <summary>
    /// Whether <paramref name="surface"/> does not compile for some pass, so its materials are drawn as the failure.
    /// </summary>
    internal bool IsBroken(Handle<Shader> surface) => _broken.Contains(surface);

    /// <summary>
    /// <paramref name="surface"/>'s shader changed: no longer broken until it fails again.
    /// </summary>
    internal void Repair(Handle<Shader> surface) => _broken.Remove(surface);

    /// <summary>
    /// The surfaces that broke since this was last asked, whose materials are packed anew, handed to
    /// <paramref name="broke"/> once each; whether any did.
    /// </summary>
    internal bool TakeBroke(HashSet<Handle<Shader>> broke)
    {
        if (_broke.Count == 0) return false;

        broke.UnionWith(_broke);
        _broke.Clear();

        return true;
    }

    /// <summary>
    /// Takes <paramref name="broken"/> as the world's sky material drawn as the failure, none when the sky is drawn as
    /// it is: why goes in the log once and on the banner for as long as it stays broken.
    /// </summary>
    internal void SayBrokenSky(Handle<Material> broken, string why)
    {
        if (broken == _brokenSky) return;

        _brokenSky = broken;
        if (broken == Handle<Material>.None)
        {
            _skyLine = null;

            return;
        }

        _skyLine = $"Sky {_assets.GetPath(broken) ?? $"{broken.Id}"} is drawn as the failure: {why}";
        Debugging.Error($"{_skyLine}.");
    }

    /// <summary>
    /// Marks the instance at <paramref name="slot"/> as drawn as the failure, why and what failed.
    /// </summary>
    internal void Mark(int slot, (string Why, string What) failure) => _marked[slot] = failure;

    /// <summary>
    /// The instance at <paramref name="slot"/> is no longer drawn as the failure, or is gone.
    /// </summary>
    internal void Unmark(int slot) => _marked.Remove(slot);

    /// <summary>
    /// Says, at each instance drawn as the failure, why, with what failed while the mouse rests on it: for this frame,
    /// so the words go once it is repaired. Only while the debug UI shows.
    /// </summary>
    internal void Label(Instances instances)
    {
        if (_debug is not { IsEnabled: true } shown) return;

        foreach ((int slot, (string why, string what)) in _marked)
        {
            shown.Error(why, instances.GetPosition(slot), 0, what);
        }
    }

    /// <summary>
    /// The lines said this frame in place of the last frame's, the pipelines' then the broken sky's, or none, each
    /// wrapped at its spaces over as many rows of <see cref="LineGlyphs"/> as it takes, the first
    /// <see cref="MaxLines"/> rows of them; packed again only when they changed.
    /// </summary>
    internal void Say(IReadOnlyList<string> lines)
    {
        if (ReferenceEquals(lines, _said) && lines.Count == _saidCount && ReferenceEquals(_skyLine, _saidSky)) return;

        _said = lines;
        _saidCount = lines.Count;
        _saidSky = _skyLine;
        _rows.Clear();
        foreach (string line in lines)
        {
            Wrap(line);
        }

        if (_skyLine is not null) Wrap(_skyLine);

        if (_rows.Count > MaxLines) _rows.RemoveRange(MaxLines, _rows.Count - MaxLines);

        _text[0] = (uint)_rows.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            Write(_rows[i], 1 + (i * LineGlyphs));
        }

        for (int code = 0; code < CodeCount; code++)
        {
            Write(code < _codes.Length ? _codes[code] : string.Empty, 1 + ((_rows.Count + code) * LineGlyphs));
        }

        TextVersion++;
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
                if (space > start) end = space;
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
            _text[first + i] = letter switch
            {
                >= '0' and <= '9' => (uint)(letter - '0'),
                >= 'A' and <= 'Z' => 10u + (uint)(letter - 'A'),
                '!' => 36u,
                _ => 37u,
            };
        }
    }
}
