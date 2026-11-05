using Magic.Debugging;
using Magic.Events;
using Magic.Gpu;
using Magic.Input;
using Magic.Projects;

namespace Magic.DebugTools;

/// <summary>
/// The debug UI's windows, each opened and closed by its key: the stats (F3), or <c>set DebugTools.stats=true</c>. They are
/// drawn with <see cref="IDebugUI"/>'s widgets in Update, so the gem takes the UI and is made after it. Without a gem that
/// provides input no key opens a window, and without a GPU provider the stats show no GPU. It is public because nothing in
/// its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class DebugToolsGem(IDebugUI ui, IGraphicsApi? gpu = null, IInput? input = null) : IGem
{
    private const string StatsSetting = "DebugTools.stats";

    private readonly StatsWindow _stats = new(ui, gpu);

    public void Update(in Frame frame)
    {
        foreach (SettingChanged setting in frame.Events.Get<SettingChanged>())
        {
            if (setting.Key != StatsSetting)
                continue;

            if (bool.TryParse(setting.Value, out bool isOpen))
                _stats.IsOpen = isOpen;
            else
                Log.Error($"{setting.Key} takes true or false, not '{setting.Value}'.");
        }

        if (input is not null && input[Control.KeyF3].WasPressed)
            _stats.IsOpen = !_stats.IsOpen;

        _stats.Update(frame);
    }
}
