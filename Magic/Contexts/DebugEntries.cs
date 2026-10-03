using System.Drawing;
using System.Numerics;

namespace Magic.Contexts;

/// <summary>
/// One thing the debug UI shows on screen for a while: a line of the list down the left of the window, or, with a
/// <see cref="Position"/>, text at that place in the world. <see cref="Count"/> is how many times it was reported
/// while showing, <see cref="Order"/> which was reported first.
/// </summary>
public readonly record struct DebugEntry(string Text, Color Color, Vector3? Position, int Count, long Order);

/// <summary>
/// What is on screen from the log and the debug UI's warnings, errors and world text, each for its seconds after it was
/// last reported. Reporting the same text at the same place again while it shows keeps it up instead of adding another.
/// Any thread may report; whoever draws takes what shows each frame. Time is the caller's, in seconds.
/// </summary>
public sealed class DebugEntries
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Text, Vector3? Position), Shown> _entries = [];
    private readonly Dictionary<string, int> _places = []; // how many entries show each text
    private long _reported;

    /// <summary>
    /// Shows <paramref name="text"/> for <paramref name="seconds"/> from <paramref name="now"/>, or keeps it showing
    /// that long when it already is; with 0 seconds it shows until the first frame it is not reported in. True when
    /// the text was not showing anywhere, which is when a caller that logs what it shows should log.
    /// </summary>
    /// <param name="counted">Whether a repeat adds to the entry's count (a logged line) or only keeps it up (something reported every frame).</param>
    public bool Report(string text, Color color, Vector3? position, float seconds, double now, bool counted)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((text, position), out Shown? shown))
            {
                shown.Color = color;
                shown.Expires = now + seconds;
                shown.IsFresh = true;
                if (counted)
                    shown.Count++;

                return false;
            }

            _entries[(text, position)] = new Shown { Color = color, Expires = now + seconds, IsFresh = true, Count = 1, Order = _reported++ };
            int places = _places.GetValueOrDefault(text);
            _places[text] = places + 1;
            return places == 0;
        }
    }

    /// <summary>
    /// Adds what shows at <paramref name="now"/> to <paramref name="into"/>, in the order it was first reported: the
    /// entries with a position, or the ones without. The ones of that kind whose time is up, and that were not
    /// reported since the last take, are gone after it.
    /// </summary>
    public void Take(double now, bool positioned, List<DebugEntry> into)
    {
        lock (_lock)
        {
            foreach (((string text, Vector3? position), Shown shown) in _entries)
            {
                if (position.HasValue != positioned)
                    continue;

                if (!shown.IsFresh && now >= shown.Expires)
                {
                    _entries.Remove((text, position)); // removing while enumerating a Dictionary is allowed
                    int places = _places[text] - 1;
                    if (places == 0)
                        _places.Remove(text);
                    else
                        _places[text] = places;

                    continue;
                }

                shown.IsFresh = false;
                into.Add(new DebugEntry(text, shown.Color, position, shown.Count, shown.Order));
            }
        }

        into.Sort(static (left, right) => left.Order.CompareTo(right.Order));
    }

    private sealed class Shown
    {
        public Color Color { get; set; }

        public double Expires { get; set; }

        /// <summary>
        /// Reported since it was last taken: it shows at least once, however short its time.
        /// </summary>
        public bool IsFresh { get; set; }

        public int Count { get; set; }

        public long Order { get; init; }
    }
}
