using Magic.Mathematics;
using System.Numerics;

namespace Magic.Extensions;

/// <summary>
/// A <see cref="Color"/> as the GPU's vectors take it.
/// </summary>
public static class ColorExtensions
{
    extension(Color color)
    {
        /// <summary>
        /// The red, green and blue channels, without alpha.
        /// </summary>
        public Vector3 Rgb => new(color.R, color.G, color.B);
    }
}
