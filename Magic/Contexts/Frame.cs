using Magic.Contexts.Events;
using Magic.Contexts.Rendering;

namespace Magic.Contexts;

/// <summary>
/// One frame, as everything sees it: its number (from 1), seconds since start, the step in seconds (the fixed step
/// inside FixedUpdate), every event published since the previous frame, in publish order, and the frame's GPU
/// <see cref="DrawCommands"/>: the scene is recorded into them before the gems' Render hooks, which add theirs on top,
/// and they are submitted after. Built once by the frame loop and handed down to every gem and Core system in turn.
/// The command list is the one thing in it that changes; it is made once and cleared by every submit.
/// </summary>
public readonly record struct Frame(long Number, double Time, float Delta, ReadOnlyMemory<Event> Events, RenderCommands DrawCommands);
