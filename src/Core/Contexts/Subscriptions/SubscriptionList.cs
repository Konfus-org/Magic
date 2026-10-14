namespace Magic.Contexts.Subscriptions;

/// <summary>
/// Subscriptions kept for as long as their owner, such as a gem's watches, which end together: one Dispose ends them
/// all, rather than a loop over them wherever they are kept. The owner's thread's, as the subscriptions are.
/// </summary>
public sealed class SubscriptionList : IDisposable
{
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>
    /// Ends every subscription added, the last first, and empties the list.
    /// </summary>
    public void Dispose()
    {
        for (int i = _subscriptions.Count - 1; i >= 0; i--)
        {
            _subscriptions[i].Dispose();
        }

        _subscriptions.Clear();
    }

    public void Add(Subscription subscription)
    {
        _subscriptions.Add(subscription);
    }
}
