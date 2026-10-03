namespace Magic.Mathematics;

/// <summary>
/// A linear colour with straight alpha. Channels are floats so a value above 1 still reads as bright after
/// the tonemap; a JSON colour is <c>{ "r": 1, "g": 1, "b": 1, "a": 1 }</c>.
/// </summary>
public readonly record struct Color(float R, float G, float B, float A = 1f);
