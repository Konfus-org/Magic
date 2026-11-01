namespace Magic.Assets.Types;

/// <summary>
/// A linear colour with straight alpha, written in a file as <c>{ "r": 1, "g": 0.5, "b": 0, "a": 1 }</c>. Channels can
/// go past 1, for light brighter than white.
/// </summary>
public readonly record struct Color(float R, float G, float B, float A = 1);
