using System.Drawing;
using System.Numerics;

namespace Magic.Contexts.Debug;

/// <summary>
/// One thing the debug UI shows on screen for a while: a line of the list down the left of the window, or, with a
/// <see cref="Position"/>, text at that place in the world. <see cref="Count"/> is how many times it was reported
/// while showing, <see cref="Order"/> which was reported first.
/// </summary>
public readonly record struct DebugEntry(string Text, Color Color, Vector3? Position, int Count, long Order);
