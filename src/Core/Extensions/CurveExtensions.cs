using Magic.Contexts.Assets;

namespace Magic.Extensions;

/// <summary>
/// What a <see cref="Curve"/> is at a place along it.
/// </summary>
public static class CurveExtensions
{
    extension(Curve curve)
    {
        /// <summary>
        /// The curve's value at <paramref name="place"/>: between two keys, the straight line between them; before the
        /// first, the first's; after the last, the last's; 0 for a curve with no keys.
        /// </summary>
        public float At(float place)
        {
            IReadOnlyList<CurveKey> keys = curve.Keys;
            if (keys.Count == 0) return 0;

            if (place <= keys[0].At) return keys[0].Value;

            for (int i = 1; i < keys.Count; i++)
            {
                CurveKey before = keys[i - 1];
                CurveKey after = keys[i];
                if (place <= after.At)
                {
                    float span = after.At - before.At;
                    return span > 0
                        ? before.Value + ((after.Value - before.Value) * ((place - before.At) / span))
                        : after.Value;
                }
            }

            return keys[^1].Value;
        }
    }
}
