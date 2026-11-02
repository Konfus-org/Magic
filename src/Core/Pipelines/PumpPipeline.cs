using Magic.Assets.Pipeline;
using Magic.CommandLine;
using Magic.Files;
using Magic.GemAPI;
using Magic.Runtime;
using Magic.Threading;

namespace Magic.Pipelines;

/// <summary>
/// Takes in what came from outside the frames, first in <see cref="BeginUpdate"/>, and gives the frame its messages.
/// </summary>
internal sealed class PumpPipeline(App app, IFileSystem files, AssetManager assets, Events events, Threads threads)
    : IPipeline<BeginUpdate>
{
    public ValueTask<BeginUpdate> RunAsync(BeginUpdate state, CancellationToken cancel)
    {
        Pump();

        return ValueTask.FromResult(new BeginUpdate(state.Frame with { Events = events.Take() }));
    }

    /// <summary>
    /// What an awaited pipeline may be waiting on: the main thread's turn, a line typed into the terminal, the
    /// assets. Not the messages, which wait for a frame to read them.
    /// </summary>
    internal void Pump()
    {
        threads.Pump(EngineThread.Main);
        Cli.Pump(app, files);
        assets.Pump();
    }
}
