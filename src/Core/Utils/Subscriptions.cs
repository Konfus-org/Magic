using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Magic.Utils;

/// <summary>
/// Subscriptions that end together: each one held is disposed with it, last added first. Make one with
/// <c>new Subscriptions { a, b }</c> or <c>[a, b]</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification = "Named for what it holds, as Subscription is; it is a list only so it can be built like one."
)]
public sealed class Subscriptions : Disposable, IEnumerable<Subscription>
{
    private readonly List<Subscription> _all = [];

    /// <summary>
    /// Makes an empty list of subscriptions. Dispose it to end each one held, last added first, and hold none after.
    /// </summary>
    public Subscriptions() => Track(End);

    public IEnumerator<Subscription> GetEnumerator() => _all.GetEnumerator();

    public void Add(Subscription subscription) => _all.Add(subscription);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void End()
    {
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            _all[i].Dispose();
        }

        _all.Clear();
    }
}
