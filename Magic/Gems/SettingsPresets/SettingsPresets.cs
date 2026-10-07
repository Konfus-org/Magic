using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Interfaces;
using Magic.Services;
using Magic.Utils;

namespace SettingsPresetsGem;

/// <summary>
/// The gem: settings presets. As it loads it applies the preset the game starts with, the <c>Preset.Path</c> setting
/// (<c>--preset</c>), else the default one (<c>Presets/Default.preset</c>, the project's if it has one), before the first
/// frame. The console commands that change settings are here: <c>preset</c> lists the presets or applies one,
/// <c>set Owner.Property=value</c> changes values until the next preset.
/// </summary>
internal sealed class SettingsPresets : IGem
{
    private readonly Settings _settings;
    private readonly Assets _assets;
    private readonly Subscription[] _commands;

    public SettingsPresets(Settings settings, Assets assets, PresetSettings startup, IConsoleCommands commands)
    {
        _settings = settings;
        _assets = assets;
        _commands =
        [
            commands.Add("preset", UsePreset),
            commands.Add("set", Set),
        ];

        Handle<Preset> preset = startup.Path.Length > 0 ? assets.Find<Preset>(startup.Path) : assets.Default<Preset>();
        if (!preset.IsValid)
            preset = assets.Default<Preset>();

        Apply(preset, LogLevel.Error);
    }

    public void Dispose()
    {
        foreach (Subscription command in _commands)
            command.Dispose();
    }

    /// <summary>
    /// <c>preset</c> lists them; <c>preset &lt;name&gt;</c> applies the one named by its path, file name or name.
    /// </summary>
    private void UsePreset(string[] args)
    {
        string[] paths = _assets.Paths(".preset");
        if (args.Length == 0)
        {
            Log.Info($"Preset {(_settings.Preset.Length > 0 ? _settings.Preset : "none")}; there are {string.Join(", ", paths)}.");
            return;
        }

        string[] matches = [.. paths.Where(path => path.Equals(args[0], StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals(args[0], StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(path).Equals(args[0], StringComparison.OrdinalIgnoreCase))];
        if (matches.Length != 1)
        {
            Log.Warn(matches.Length == 0 ? $"No preset is named {args[0]}." : $"{matches.Length} presets are named {args[0]}: {string.Join(", ", matches)}.");
            return;
        }

        Apply(_assets.Find<Preset>(matches[0]), LogLevel.Warning);
    }

    private void Set(string[] args)
    {
        if (args.Length == 0)
        {
            Log.Warn("Usage: set <Owner.Property=value> ..., like set Gpu.Vsync=false ShadowPlan.distance=500");
            return;
        }

        string[] unused = _settings.Set(args);
        foreach (string line in unused)
            Log.Warn($"set {line}: expected Owner.Property=value.");

        if (unused.Length < args.Length)
            Log.Info($"Set {string.Join(' ', args.Except(unused))}.");
    }

    /// <summary>
    /// Applies the preset; one that does not load is logged at <paramref name="failure"/> and changes nothing.
    /// </summary>
    private void Apply(Handle<Preset> handle, LogLevel failure)
    {
        if (_assets.Load(handle) is not { } preset)
        {
            string message = $"The settings preset {_assets.PathOf(handle.Id) ?? handle.ToString()} could not be loaded; the settings are as they were.";
            if (failure == LogLevel.Error)
                Log.Error(message);
            else
                Log.Warn(message);
            return;
        }

        _settings.Apply(preset.Path, preset.Values);
        Log.Info($"Settings preset: {preset.Path}");
    }
}
