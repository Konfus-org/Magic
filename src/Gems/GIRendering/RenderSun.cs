using System.Numerics;

namespace Magic.GIRendering;

/// <param name="Direction">Towards the sun, unit.</param>
/// <param name="Radiance">The sun's colour times its intensity, in lux, above the air.</param>
internal readonly record struct RenderSun(Vector3 Direction, Vector3 Radiance);
