namespace Magic.Contexts.Settings;

/// <summary>
/// The project's settings, one section per subsystem, stored in the <c>.magic</c> file under
/// <c>"settings"</c> and overridable from the command line with <c>--set Section.Key=value</c>
/// (<c>--set Render.Vsync=false</c>). Every value has a default, so a file states only what differs.
/// </summary>
public sealed class Settings
{
    public RenderSettings Render { get; set; } = new();

    public AssetSettings Assets { get; set; } = new();
}
