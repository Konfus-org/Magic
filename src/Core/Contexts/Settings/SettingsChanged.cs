using Magic.Interfaces;

namespace Magic.Contexts.Settings;

/// <summary>
/// A gem's settings changed while running, told on <see cref="Services.Events"/> once the change is copied into the
/// object the gem holds: what they were, and what they are now, each a copy of its own. Only settings whose values
/// changed are told. A gem that follows a setting watches this, takes the change of its own settings' type, and
/// compares the setting in the two:
/// <c>if (changed is { Before: MySettings was, After: MySettings now } &amp;&amp; was.Size != now.Size) Resize();</c>.
/// </summary>
/// <param name="Before">The settings as they were.</param>
/// <param name="After">The settings as they are now.</param>
public readonly record struct SettingsChanged(ISettings Before, ISettings After);
