using System.Diagnostics.CodeAnalysis;

namespace Magic.Interfaces;

/// <summary>
/// A part of a world's weather, as its <see cref="Contexts.Components.Weather"/> lists it: values the sky, the
/// surfaces and the air read, and anything the wind moves. The renderer reads the ones it knows; a pipeline script may
/// read a project's own (<see cref="IRendering.Weather"/>).
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the weather's effects are listed by, which an attribute could not be.")]
public interface IWeatherEffect : IRenderEffect;
