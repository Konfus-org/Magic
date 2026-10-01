using System.Numerics;

namespace Magic.Mathematics;

/// <summary>
/// The engine's frame: left handed, +X right, +Y up, +Z forward, 1 unit = 1 metre. Three constants, so
/// nothing ever spells a direction out as numbers.
/// </summary>
public static class Axis
{
    public static Vector3 Right { get; } = Vector3.UnitX;

    public static Vector3 Up { get; } = Vector3.UnitY;

    public static Vector3 Forward { get; } = Vector3.UnitZ;
}
