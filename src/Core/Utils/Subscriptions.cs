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
public sealed class Subscriptions : IDisposable, IEnumerable<Subscription>
{
    private readonly List<Subscription> _all = [];

    /// <summary>
    /// Ends every subscription held, last added first, and holds none after.
    /// </summary>
    public void Dispose()
    {
        for (int i = _all.Count - 1; i >= 0; i--)
        {
            _all[i].Dispose();
        }

        _all.Clear();
    }

    public void Add(Subscription subscription) => _all.Add(subscription);

    /// <summary>
    /// Takes <paramref name="subscription"/> out without ending it; whether it was held.
    /// </summary>
    public bool Remove(Subscription subscription) => _all.Remove(subscription);

    public IEnumerator<Subscription> GetEnumerator() => _all.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
