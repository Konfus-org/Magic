using System.Diagnostics.CodeAnalysis;
using Magic.Debugging;
using Magic.Gems;
using Magic.Pipelines;

namespace Hello;

/// <summary>
/// The gem: the one class in its assembly that implements <see cref="IGem"/>. Magic makes it after whatever its
/// constructor takes. Each <see cref="IPipeline{TState}"/> it implements is a pipeline it joins, and every other
/// interface is what it provides to the gems made after it.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Magic makes it by reflection when a project lists the gem."
)]
internal sealed class HelloGem : IGem, IPipeline<Update>
{
    public ValueTask<Update> RunAsync(Update state, CancellationToken cancel)
    {
        if (state.Frame.Index % 60 == 0)
            Log.Info($"Hello is running, at frame {state.Frame.Index}.");

        return ValueTask.FromResult(state);
    }
}
