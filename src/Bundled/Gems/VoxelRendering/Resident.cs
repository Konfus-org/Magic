using System.Threading.Channels;
using Magic.Contexts.Assets;
using Magic.Contexts.Utils;
using Magic.Extensions;
using Magic.Interfaces;
using Magic.Services;

namespace Magic.VoxelRendering;

/// <summary>
/// The assets of one kind held resident, and the flow every kind shares: how many hold each, one count however many
/// reasons there are; its entry, made the first time it is held, which starts its load; the load on a worker, where
/// the asset is made ready for the GPU (<c>prepare</c>), or said and handed over as none when it does not load;
/// taking in on the render thread what arrived for entries still held (<see cref="Take"/>); and letting go of those
/// nothing holds any more (<see cref="Purge"/>). What each kind does with what arrived, and what leaving frees, is
/// its own. Render thread, but for the loads.
/// </summary>
internal sealed class Resident<T, TEntry, TReady>(
    Assets assets,
    string failure,
    Func<T, Task<TReady>> prepare,
    CancellationToken stopping)
    where T : IAsset<T>
    where TEntry : class
    where TReady : class
{
    private readonly Dictionary<Handle<T>, int> _holds = [];

    /// <summary>
    /// The assets let go to none since the last purge, which it lets leave unless they were held again.
    /// </summary>
    private readonly HashSet<Handle<T>> _unheld = [];

    /// <summary>
    /// What workers made ready, or none for an asset that did not load, for the render thread to take in.
    /// </summary>
    private readonly Channel<(Handle<T> Handle, TEntry Entry, TReady? Ready)> _arrived =
        Channel.CreateUnbounded<(Handle<T> Handle, TEntry Entry, TReady? Ready)>(
            new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// The entry of each asset held, or let go and not yet purged.
    /// </summary>
    internal Dictionary<Handle<T>, TEntry> Entries { get; } = [];

    /// <summary>
    /// Holds <paramref name="handle"/>: its entry, made by <paramref name="make"/> the first time, which starts its
    /// load.
    /// </summary>
    internal TEntry Record(Handle<T> handle, Func<TEntry> make)
    {
        _holds[handle] = _holds.GetValueOrDefault(handle) + 1;

        if (Entries.TryGetValue(handle, out TEntry? entry)) return entry;

        entry = make();
        Entries[handle] = entry;
        LoadAsync(handle, entry).FireAndForget();

        return entry;
    }

    /// <summary>
    /// Lets one hold go: once none holds it, the next <see cref="Purge"/> lets it leave.
    /// </summary>
    internal void Release(Handle<T> handle)
    {
        if (!_holds.TryGetValue(handle, out int count)) return;

        if (count > 1)
        {
            _holds[handle] = count - 1;

            return;
        }

        _holds.Remove(handle);
        _unheld.Add(handle);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is still the asset's: what a worker made for one let go meanwhile is dropped.
    /// </summary>
    internal bool IsCurrent(Handle<T> handle, TEntry entry)
        => Entries.TryGetValue(handle, out TEntry? now) && ReferenceEquals(now, entry);

    /// <summary>
    /// The asset's file changed and it loaded again: it is made ready again for the entry it has, when it is held.
    /// </summary>
    internal void Reload(T asset)
    {
        if (!Entries.TryGetValue(asset.Handle, out TEntry? entry)) return;

        PrepareAsync(asset.Handle, entry, asset).FireAndForget();
    }

    /// <summary>
    /// Hands <paramref name="take"/> what arrived since the last frame for each entry still held: what was made ready,
    /// or none when it did not load.
    /// </summary>
    internal void Take(Action<Handle<T>, TEntry, TReady?> take)
    {
        while (_arrived.Reader.TryRead(out (Handle<T> Handle, TEntry Entry, TReady? Ready) arrived))
        {
            if (IsCurrent(arrived.Handle, arrived.Entry)) take(arrived.Handle, arrived.Entry, arrived.Ready);
        }
    }

    /// <summary>
    /// Lets every asset nothing holds any more leave, its entry handed to <paramref name="evict"/>.
    /// </summary>
    internal void Purge(Action<Handle<T>, TEntry> evict)
    {
        foreach (Handle<T> handle in _unheld)
        {
            if (!_holds.ContainsKey(handle) && Entries.Remove(handle, out TEntry? entry)) evict(handle, entry);
        }

        _unheld.Clear();
    }

    private async Task LoadAsync(Handle<T> handle, TEntry entry)
    {
        Result<T> loaded = await assets.LoadAsync(handle, stopping).ConfigureAwait(false);
        if (loaded.Ok)
        {
            await PrepareAsync(handle, entry, loaded.Value).ConfigureAwait(false);

            return;
        }

        FailureHandler.Report(failure, loaded.Error);
        _arrived.Writer.TryWrite((handle, entry, null));
    }

    private async Task PrepareAsync(Handle<T> handle, TEntry entry, T asset)
    {
        TReady ready = await prepare(asset).ConfigureAwait(false);
        _arrived.Writer.TryWrite((handle, entry, ready));
    }
}
