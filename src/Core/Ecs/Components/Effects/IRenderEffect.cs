using System.Diagnostics.CodeAnalysis;

namespace Magic.Ecs.Components.Effects;

/// <summary>
/// Marks a struct as a render effect: plain data a <see cref="RenderEffects{T}"/> lists, which the renderer reads, and
/// what a file names by its type's name, found in the code loaded. It may name a shader by its handle and hold the values
/// that shader reads. A part of the weather is an <see cref="IWeatherEffect"/>.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker RenderEffects' constraints name, which an attribute could not be."
)]
public interface IRenderEffect;
