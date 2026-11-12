namespace Magic.Assets;

/// <summary>
/// A linear colour with straight alpha, written in a file as <c>{ "r": 1, "g": 0.5, "b": 0, "a": 1 }</c>.
/// </summary>
/// <remarks>
/// A channel can be more than 1, for light that is brighter than white.
/// </remarks>
public readonly record struct Color(float R, float G, float B, float A = 1);
