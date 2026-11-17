using System.ComponentModel;
using Magic.Configuration;

namespace Magic.TracedReflections;

/// <summary>
/// How smooth surfaces' reflections are traced.
/// </summary>
[Category("Rendering")]
public sealed class TracedReflectionsSettings : ISettings
{
    /// <summary>
    /// Whether smooth surfaces' reflections are traced; off, they reflect their diffuse light.
    /// </summary>
    public bool IsReflectionsOn { get; set; } = true;

    /// <summary>
    /// How far in metres the reflections' rays reach, from 0, before the sky stands for what lies beyond.
    /// </summary>
    public float ReflectionRayRange { get; set; } = 10000;

    /// <summary>
    /// How much of its last frames the reflections keep each frame, from 0 to 1. More is smoother and slower to follow
    /// a change.
    /// </summary>
    public float ReflectionHistory { get; set; } = 0.95f;
}
