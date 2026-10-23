using Magic.Interfaces;

namespace Magic.Contexts.Rendering;

/// <summary>
/// The sky's air over the view, and the fog with it, which whatever draws the air draws when the weather lists it.
/// </summary>
public readonly record struct Air : IWeatherEffect;
