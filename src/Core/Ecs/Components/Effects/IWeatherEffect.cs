using System.Diagnostics.CodeAnalysis;

namespace Magic.Ecs.Components.Effects;

/// <summary>
/// A part of a world's weather, as its <see cref="Weather"/> lists it: values the sky, the surfaces and the air read, and
/// anything the wind moves. The renderer reads the ones it knows; a gem or script may read a project's own.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1040:Avoid empty interfaces",
    Justification = "A marker the weather's effects are listed by, which an attribute could not be."
)]
public interface IWeatherEffect : IRenderEffect;
