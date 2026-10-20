using Magic.Services;

namespace Magic.Contexts.Gpu;

/// <summary>
/// The debug view to show in place of the finished image changed: <see cref="Name"/>, a way of looking at one thing
/// the frame made, its depth, its normals, which lights reach each pixel; empty for the frame as it is. A message on
/// <see cref="Events"/>, heard the frame after, from whatever chooses the view, a debug window or a console command,
/// to whatever draws the frame, which decides what the name shows, a pass of its own.
/// </summary>
public readonly record struct DebugViewChanged(string Name);
