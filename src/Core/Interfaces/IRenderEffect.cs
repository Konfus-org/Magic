using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Rendering;

namespace Magic.Interfaces;

/// <summary>
/// Marks a struct as a render effect: plain data a <see cref="RenderEffects{T}"/> lists, which the renderer reads, and
/// what a file names by its type's name, found in the code loaded, Core's, the project's and each gem's. A post effect
/// is an <see cref="IPostEffect"/>, a part of the weather an <see cref="IWeatherEffect"/>.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker RenderEffects' constraints name, which an attribute could not be.")]
public interface IRenderEffect;
