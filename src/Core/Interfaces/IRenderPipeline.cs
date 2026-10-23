using Magic.Contexts.Assets;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// A pipeline script: the frame a renderer draws, end to end, the renderer's steps and its own passes in order, each
/// with what it reads and writes. A class implementing it, named like its <c>.cs</c> file, is a <see cref="Script"/>
/// asset, which the renderer's settings name; the renderer makes one copy, its constructor given the services it asks
/// for, its properties at their defaults, and makes it again when its code is built again. It never starts on an
/// entity.
/// <para>
/// A renderer ships its own, which a project copies into its assets to change, renaming the class, and names in the
/// renderer's settings in its place.
/// </para>
/// </summary>
public interface IRenderPipeline : IScript
{
    /// <summary>
    /// Copies what the script reads of the world, at the hold-still point between frames, while neither thread runs:
    /// what <see cref="Record"/> then reads on the render thread. Main thread.
    /// </summary>
    void Sync(World world)
    {
    }

    /// <summary>
    /// Records one frame with <paramref name="rendering"/>. Render thread.
    /// </summary>
    void Record(IRendering rendering);
}
