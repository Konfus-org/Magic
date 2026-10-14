namespace Magic.Contexts.Settings;

/// <summary>
/// A gem's settings as <see cref="Services.Settings"/> keeps them: the gem's name, as the project lists it, and the
/// object its constructor was given, which a change while running is copied into.
/// </summary>
public sealed record GemSettings(string Gem, object Values);
