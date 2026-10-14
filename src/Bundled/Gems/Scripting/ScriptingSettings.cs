namespace Magic.Scripting;

/// <summary>
/// How often a far behaviour is updated: set in the project's <c>Presets/Default.preset</c>, by another preset, or with
/// <c>--set Scripting.maxInterval=1</c>, which updates every behaviour every frame.
/// </summary>
internal sealed class ScriptingSettings
{
    /// <summary>
    /// Metres from the nearest of the player's cameras within which a behaviour is updated every frame; up to n times as far, every nth
    /// frame.
    /// </summary>
    public float FullRateDistance { get; set; } = 64;

    /// <summary>
    /// The most frames between two updates of a behaviour, however far it is: 1 or more.
    /// </summary>
    public int MaxInterval { get; set; } = 32;
}
