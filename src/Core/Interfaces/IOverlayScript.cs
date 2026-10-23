using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.Interfaces;

/// <summary>
/// A script that draws over the window once the frame's views are shown, as the debug UI does, with its own pipelines
/// and without a gem or the renderer's internals. A class implementing it, named like its <c>.cs</c> file, is a
/// <see cref="Script"/> asset, which the renderer's settings name; the renderer makes one copy, on a worker, its
/// constructor given the services it asks for and keeping them, nothing more; and disposes it on the render thread, if
/// it is <see cref="IDisposable"/>, so it may keep GPU objects, made in <see cref="Record"/>, the first time it runs. It
/// never starts on an entity.
/// <para>
/// <see cref="Record"/> runs on the render thread with the frame's commands open: it uploads what it must, opens the
/// render pass (<see cref="OverlayContext.Begin"/>), draws with its own pipelines, made for the context's format,
/// encoded as the context says, and ends the pass. It reads nothing of the world: what it draws was copied for it on
/// the main thread, as the debug UI's draw lists are.
/// </para>
/// </summary>
public interface IOverlayScript : IScript
{
    void Record(in OverlayContext context);
}
