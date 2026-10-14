using Magic.Contexts.Assets;
using Magic.Contexts.CmdLine;
using Magic.Contexts.Frames;
using Magic.Services;

namespace Magic.Interfaces;

/// <summary>
/// A plugin. Everything Magic does beyond running frames lives in one: a gem is an assembly holding exactly one
/// class that implements IGem, conventionally <c>internal sealed class XGem</c>.
/// <para>
/// Its constructor parameters are what it needs: Core's services (<see cref="IFileSystem"/>, <see cref="Lifetime"/>,
/// <see cref="Threads"/>, <see cref="Events"/>, <see cref="Project"/>, <see cref="Args"/>, <see cref="Settings"/>, and
/// <see cref="IServiceProvider"/> to find a service by its type as it runs) or interfaces other gems implement. Every
/// other interface the class implements is what it provides to the gems made after it, an
/// <see cref="IAssetLoader{T}"/> as any other, but <see cref="ILogSink"/>, which Core adds to the log instead. Core
/// makes gems in that dependency order and disposes those that are
/// <see cref="IDisposable"/> in reverse. Every frame it calls the main thread's hooks below in the order the gems were
/// made, so a gem is up to date before the gems that take it read it, and Render in reverse, so a gem finishes what the
/// gems that take it drew.
/// </para>
/// <para>
/// The constructor and Dispose run on the render thread, alone. Update, FixedUpdate and LateUpdate run on the main
/// thread; Render on the render thread, one frame behind, while the main thread runs the next frame's Update and
/// FixedUpdate. So Render reads only what the gem copied in LateUpdate, never what Update and FixedUpdate change.
/// Implement the hooks the gem needs; the rest are never called.
/// </para>
/// </summary>
public interface IGem
{
    /// <summary>
    /// <see cref="Phase.Update"/>: take in what happened since the last frame, and move the world on.
    /// </summary>
    void Update(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.FixedUpdate"/>: move the world on by one fixed step. Runs as many times as there are steps in
    /// the time since the last frame, none for some frames; <paramref name="frame"/>'s DeltaSeconds is the step.
    /// </summary>
    void FixedUpdate(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.LateUpdate"/>: copy what Render will read. The render thread is idle until every gem's
    /// LateUpdate is done, so this can write what Render reads without a lock.
    /// </summary>
    void LateUpdate(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.Render"/>: draw what LateUpdate copied, after the gems that take this one have drawn.
    /// </summary>
    void Render(in Frame frame)
    {
    }
}
