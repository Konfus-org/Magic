namespace Magic.Utils;

/// <summary>
/// Objects taken and given back instead of made and thrown away: a buffer, a builder, anything whose making is the cost
/// or whose discarding is the garbage. <see cref="Rent"/> hands out one that was returned, or makes one with <c>make</c> 
/// <see cref="Return"/> keeps it for the next renter, up to <c>keep</c> at a time (more are let go), after
/// <c>reset</c> has made it as good as new. Any thread. What is rented is the renter's alone until returned, and
/// nothing is owed: an object never returned is simply made again.
/// </summary>
public sealed class Pool<T> where T : class
{
    private readonly Lock _lock = new();
    private readonly Stack<T> _idle = new();
    private readonly Func<T> _make;
    private readonly Action<T>? _reset;
    private readonly int _keep;

    /// <summary>
    /// <param name="make">Makes one when none is idle.</param>
    /// <param name="keep">How many idle ones are kept; a return past that is dropped.</param>
    /// <param name="reset">Done to each on its return, before it is kept.</param>
    /// </summary>
    public Pool(Func<T> make, int keep = 16, Action<T>? reset = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        _make = make;
        _keep = keep;
        _reset = reset;
    }

    /// <summary>
    /// How many are idle right now.
    /// </summary>
    public int Idle
    {
        get
        {
            lock (_lock)
                return _idle.Count;
        }
    }

    public T Rent()
    {
        lock (_lock)
        {
            if (_idle.TryPop(out T? idle))
                return idle;
        }

        return _make();
    }

    public void Return(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _reset?.Invoke(item);
        lock (_lock)
        {
            if (_idle.Count < _keep)
                _idle.Push(item);
        }
    }
}
