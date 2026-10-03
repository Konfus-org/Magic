using System.Drawing;

namespace Magic.Contexts;

/// <summary>
/// One line of the list the debug UI draws down the left of the main window (<see cref="Interfaces.IDebugUI.Lines"/>).
/// </summary>
public readonly record struct DebugLine(string Text, Color Color);
