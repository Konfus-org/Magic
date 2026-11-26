using Magic.CommandLine;

namespace Magic.GlobalIllumination;

/// <summary>
/// Which terms of the indirect light are on, to measure the share of each against the reference view (PROTO).
/// </summary>
/// <remarks>
/// Each is on until the console turns it off, as <c>set GlobalIllumination.IsDiffuseSkyOn false</c>. It is state of
/// the run and is not saved. The game thread writes it, and the render thread reads it.
/// </remarks>
[CliPath("GlobalIllumination")]
public sealed class LightTerms
{
    /// <summary>
    /// Whether a diffuse ray's hit takes the probes' light around it. Off, it takes only its direct light.
    /// </summary>
    public bool IsHitProbeLightOn { get; set; } = true;

    /// <summary>
    /// Whether a diffuse ray that reaches nothing brings back the sky's light. Off, it brings back none.
    /// </summary>
    public bool IsDiffuseSkyOn { get; set; } = true;
}
