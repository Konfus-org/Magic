using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Magic.Contexts.Subscriptions;

namespace Magic.Services;

/// <summary>
/// Messages between gems, by type: anything publishes one, from any thread, and every watcher of its type hears it on
/// the main thread at the start of the next frame, after the work handed to the main thread and before Update, in the
/// order the messages were published. A message is a struct, and publishing one allocates nothing: it goes into a queue
/// of its type, and one nobody watches goes nowhere. Safe from any thread.
/// </summary>
public sealed class Events
{
    private readonly ConcurrentDictionary<Type, Mailbox> _mailboxes = new();

    /// <summary>
    /// One entry per message published, in the order they were: the mailbox of its type, which delivers the oldest it
    /// holds. A queue of mailboxes rather than of messages, so publishing boxes nothing.
    /// </summary>
    private readonly ConcurrentQueue<Mailbox> _published = new();

    /// <summary>
    /// How many messages are in <see cref="_published"/> to deliver; what is published while they are waits for the
    /// next delivery.
    /// </summary>
    private int _waiting;

    /// <summary>
    /// Hands <paramref name="message"/> to every watcher of <typeparamref name="T"/>, on the main thread at the start
    /// of the next frame. With none, it goes nowhere.
    /// </summary>
    public void Publish<T>(in T message)
        where T : struct
    {
        if (!_mailboxes.TryGetValue(typeof(T), out Mailbox? found)
            || found is not Mailbox<T> { IsWatched: true } mailbox)
        {
            return;
        }

        mailbox.Messages.Enqueue(message);
        _published.Enqueue(mailbox);
        Interlocked.Increment(ref _waiting);
    }

    /// <summary>
    /// Calls <paramref name="watcher"/> with every <typeparamref name="T"/> published from now on, on the main thread,
    /// until the subscription is disposed. A watcher that throws is logged, and the others still hear the message.
    /// </summary>
    public Subscription Watch<T>(Action<T> watcher)
        where T : struct
    {
        Mailbox<T> mailbox = (Mailbox<T>)_mailboxes.GetOrAdd(typeof(T), static _ => new Mailbox<T>());
        mailbox.Add(watcher);
        return new Subscription(() => mailbox.Remove(watcher));
    }

    /// <summary>
    /// Hands the messages published so far to their watchers. On the main thread, at the start of each frame.
    /// </summary>
    internal void Deliver()
    {
        for (
            int waiting = Interlocked.Exchange(ref _waiting, 0);
            waiting > 0 && _published.TryDequeue(out Mailbox? mailbox);
            waiting--)
        {
            mailbox.DeliverOldest();
        }
    }

    /// <summary>
    /// The messages of one type and its watchers.
    /// </summary>
    private abstract class Mailbox
    {
        public abstract void DeliverOldest();
    }

    /// <inheritdoc cref="Mailbox"/>
    private sealed class Mailbox<T> : Mailbox
        where T : struct
    {
        private readonly Lock _changing = new();

        /// <summary>
        /// Replaced whole as watchers come and go, so delivery walks a set that does not change under it.
        /// </summary>
        private Action<T>[] _watchers = [];

        public ConcurrentQueue<T> Messages { get; } = new();

        public bool IsWatched => Volatile.Read(ref _watchers).Length > 0;

        public void Add(Action<T> watcher)
        {
            lock (_changing)
            {
                Volatile.Write(ref _watchers, [.. _watchers, watcher]);
            }
        }

        public void Remove(Action<T> watcher)
        {
            lock (_changing)
            {
                int index = Array.IndexOf(_watchers, watcher);
                if (index >= 0)
                {
                    Volatile.Write(ref _watchers, [.. _watchers.AsSpan(0, index), .. _watchers.AsSpan(index + 1)]);
                }
            }
        }

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Whatever a watcher throws, it is logged and the others still hear the message.")]
        public override void DeliverOldest()
        {
            if (!Messages.TryDequeue(out T message)) return;

            foreach (Action<T> watcher in Volatile.Read(ref _watchers))
            {
                try
                {
                    watcher(message);
                }
                catch (Exception ex)
                {
                    Debugging.Error($"A watcher of {typeof(T).Name} threw: {ex}");
                }
            }
        }
    }
}
