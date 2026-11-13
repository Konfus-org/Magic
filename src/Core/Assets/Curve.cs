using System.Collections.Immutable;

namespace Magic.Assets;

/// <summary>
/// One key of a <see cref="Curve"/>: its value where it is along the curve.
/// </summary>
public readonly record struct CurveKey(float At, float Value);

/// <summary>
/// A value that changes along a height, a distance or a time: keys joined by straight lines.
/// </summary>
/// <remarks>
/// Before the first key, the value is the first key's value. After the last key, it is the last key's value. A
/// <c>.curve</c> file is this class as JSON, with its keys in order of <see cref="CurveKey.At"/>:
/// <c>{ "keys": [ { "at": 0, "value": 1 }, { "at": 120, "value": 0 } ] }</c>.
/// </remarks>
[AssetFormat(AssetFileFormat.Json)]
public sealed class Curve : IAsset<Curve>
{
    public ImmutableArray<CurveKey> Keys { get; set; } = [];
}
