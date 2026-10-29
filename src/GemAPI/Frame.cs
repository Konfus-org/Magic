namespace Magic.GemAPI;

public readonly record struct Frame
{
    public uint Index { get; }

    /// <summary>
    /// The time the app has been running, does not pause when 'paused'.
    /// Used for things that should still update when the simulation is paused.
    /// </summary>
    public ulong AppSecsRunning { get; }

    /// <summary>
    /// The 'real' delta, does not pause when 'paused'.
    /// Used for things that should still update when the simulation is paused.
    /// </summary>
    public uint AppMsDeltaTime { get; }

    /// <summary>
    /// The time the sim has been running. Respects pause.
    /// </summary>
    public ulong SimSecsRunning { get; }

    /// <summary>
    /// The time delta since the last frame. Respects pause.
    /// </summary>
    public uint SimMsDeltaTime { get; }

    public bool IsPaused { get; }
}
