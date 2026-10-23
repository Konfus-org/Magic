using System.Numerics;
using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The way the air moves, in metres per second: its direction and its strength in one. It carries the clouds, and
/// moves whatever reads it.
/// </summary>
public readonly record struct Wind(Vector3 Velocity) : IWeatherEffect;
