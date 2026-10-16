using System.Diagnostics.CodeAnalysis;

namespace Magic.Interfaces;

/// <summary>
/// A gem's settings: the class its constructor takes, plain data like a file type, which <see cref="Services.Settings"/>
/// makes from the presets and <c>--set</c> as the gem is made and keeps for the whole run (Core's README, Gem settings).
/// A gem takes one; the loader knows it by this interface.
/// </summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker the gem loader knows a constructor parameter by, as it knows a gem by IGem.")]
public interface ISettings;
