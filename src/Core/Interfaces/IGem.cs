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
/// other interface the class implements is what it provides to the gems made after it, an <see cref="IAssetLoader{T}"/>
/// as any other, but <see cref="ILogSink"/>, which Core adds to the log instead, and <see cref="IPreloader"/>, which
/// Core gives the world. Core makes gems in that dependency order and disposes those that are <see cref="IDisposable"/>
/// in reverse. Every frame it calls the main thread's hooks below in the order the gems were made, so a gem is up to
/// date before the gems that take it read it, and Render in reverse, so a gem finishes what the gems that take it drew.
/// </para>
/// <para>
/// The constructor and Dispose run on the render thread, alone. Update, FixedUpdate, LateUpdate and Sync run on the
/// main thread; Render on the render thread, one frame behind, while the main thread runs the next frame's Update,
/// FixedUpdate and LateUpdate. Sync is the one point where both hold still. So a gem prepares in LateUpdate what Render
/// will read, into a buffer Render is not reading, swaps the two in Sync, and Render reads only that: never what
/// Update, FixedUpdate and LateUpdate change. Once the frames end, Stop runs on the main thread, then Dispose on the
/// render thread, each gem in reverse order. Implement the hooks the gem needs; the rest are never called.
/// </para>
/// <para>
/// What a worker makes for a gem reaches it as data: the gem keeps what arrived and takes it in at the start of the
/// hook that owns the structure it goes into, never as work run on its behalf by an engine thread.
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
    /// <see cref="Phase.LateUpdate"/>: prepare what Render will read, into what Render is not reading, as the render
    /// thread may still be drawing the frame before.
    /// </summary>
    void LateUpdate(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.Sync"/>: swap what LateUpdate prepared in for Render to read. The render thread is idle until
    /// every gem's Sync is done, so this can touch what Render reads without a lock; it is the one such point, and
    /// every gem waits on it, so it does nothing more than the swap.
    /// </summary>
    void Sync(in Frame frame)
    {
    }

    /// <summary>
    /// <see cref="Phase.Render"/>: draw what Sync swapped in, after the gems that take this one have drawn. A gem
    /// that draws takes <see cref="IGpu"/> in its constructor and records into <see cref="IGpu.Commands"/> here, so
    /// the provider, made before it, submits after it.
    /// </summary>
    void Render(in Frame frame)
    {
    }

    /// <summary>
    /// The run is over: end what the gem owns of the main thread's, its job threads, its scripts, what workers fitted
    /// for it. On the main thread, once the last frame is done and before Dispose runs on the render thread.
    /// </summary>
    void Stop()
    {
    }
}
