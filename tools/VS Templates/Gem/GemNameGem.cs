using Magic.Debugging;
using Magic.Gems;
using Magic.Pipelines;

namespace GemName;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it after whatever its
/// constructor takes. Each <see cref="IPipeline{TState}"/> it implements is a pipeline it joins, and every other
/// interface is what it provides to the gems made after it. It is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class GemNameGem : IGem, IPipeline<Update>
{
    public ValueTask<Update> RunAsync(Update state, CancellationToken cancel)
    {
        if (state.Frame.Index % 60 == 0)
            Log.Info($"GemName is running, at frame {state.Frame.Index}.");

        return ValueTask.FromResult(state);
    }
}
