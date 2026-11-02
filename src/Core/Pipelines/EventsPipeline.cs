using Magic.GemAPI;

namespace Magic.Pipelines;

/// <summary>
/// Gives the frame the messages published during the last one, first in <see cref="BeginUpdate"/>.
/// </summary>
internal sealed class EventsPipeline(Events events) : IPipeline<BeginUpdate>
{
    public ValueTask<BeginUpdate> RunAsync(BeginUpdate state, CancellationToken cancel) =>
        ValueTask.FromResult(new BeginUpdate(state.Frame with { Events = events.Take() }));
}
