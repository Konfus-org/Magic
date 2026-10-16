using Magic.Contexts.Assets;
using Magic.Contexts.Gpu;

namespace Magic.Interfaces;

/// <summary>
/// A script that records part of a frame: a hook at the point of the frame a <see cref="Pass"/> of kind
/// <c>script</c> puts it, drawing into the target the pass names, without a gem or the renderer's internals. A class
/// implementing it, named like its <c>.cs</c> file, is a <see cref="Script"/> asset, which the pass names; the
/// renderer makes one copy for each listing of the frame it builds, on a worker, its constructor given the services it
/// asks for and keeping them, nothing more; and disposes it with that listing, on the render thread, if it is
/// <see cref="IDisposable"/>, so it may keep GPU objects, made in <see cref="Record"/>, the first time it runs. It
/// never starts on an entity.
/// <para>
/// <see cref="Record"/> runs on the render thread, once for each target the pass runs for, with the frame's commands
/// open: it uploads what it must, opens the render pass (<see cref="PipelineScriptContext.Begin"/>), draws with its own
/// pipelines, made for the context's format, encoded as the context says, and ends the pass. It reads nothing of the
/// world: what it draws was copied for it on the main thread, as the debug UI's draw lists are.
/// </para>
/// </summary>
public interface IPipelineScript : IScript
{
    void Record(in PipelineScriptContext context);
}
