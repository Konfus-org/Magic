using System.Collections.Immutable;

namespace Magic.Assets.Types;

/// <summary>
/// One key of a <see cref="Curve"/>: its value where it is along the curve.
/// </summary>
public readonly record struct CurveKey(float At, float Value);

/// <summary>
/// A value that changes along something, a height, a distance or a time: keys joined by straight lines, the first key's
/// value held before it and the last's after it. A <c>.curve</c> file is this class as JSON, its keys in the order of
/// where they are: <c>{ "keys": [ { "at": 0, "value": 1 }, { "at": 120, "value": 0 } ] }</c>.
/// </summary>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Curve : IAsset<Curve>
{
    public ImmutableArray<CurveKey> Keys { get; set; } = [];
}
