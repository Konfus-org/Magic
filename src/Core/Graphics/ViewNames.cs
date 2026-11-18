using System.Collections.Concurrent;

namespace Magic.Graphics;

/// <summary>
/// The name of a resource in each view of the frame (<see cref="RenderViews"/>).
/// </summary>
internal static class ViewNames
{
    /// <summary>
    /// A resource's name in each view after the first, made once, so a frame formats no text.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Name, int View), string> _names = [];

    /// <summary>
    /// Gets <paramref name="name"/> in the first view, and a name of its own in each view after it.
    /// </summary>
    public static string Get(string name, int view) =>
        view == 0 ? name : _names.GetOrAdd((name, view), static key => $"{key.Name}, view {key.View}");
}
