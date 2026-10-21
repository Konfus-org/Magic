using Magic.Interfaces;

namespace Magic.Contexts.Assets;

/// <summary>
/// One key of a <see cref="Curve"/>: its value where it is along the curve.
/// </summary>
public readonly record struct CurveKey(float At, float Value);

/// <summary>
/// A value that changes along something, a height, a distance or a time: keys joined by straight lines, the first key's
/// value held before it and the last's after it. A <c>.curve</c> file is this class as JSON, its keys in the order of
/// where they are: <c>{ "keys": [ { "at": 0, "value": 1 }, { "at": 120, "value": 0 } ] }</c>. A pass takes one as a
/// param by its handle, <c>{ "curve": { "id": 3364 } }</c>, for a <c>Curve</c> member of its <c>PassParams</c>; code
/// reads one with <c>At</c> (<c>CurveExtensions</c>).
/// </summary>
public sealed class Curve : IJsonAsset<Curve>
{
    public static IReadOnlyList<string> Extensions { get; } = [".curve"];

    public Handle<Curve> Handle { get; init; }

    public IReadOnlyList<CurveKey> Keys { get; set; } = [];
}
